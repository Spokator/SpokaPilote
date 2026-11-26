using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>API REST de la console centrale (toutes les routes sous /api).</summary>
public static class ApiEndpoints
{
    public static string Version { get; } =
        typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Possibilités annoncées par /api/health : rapports compressés gzip (toutes les versions de la console),
    /// refus temporaire 503 + Retry-After en cas d'afflux, liste des postes paginée, supervision, sauvegarde à chaud ;
    /// cache de pilotes et de catalogues (4.0, état réel : GET /api/cache/status).
    /// </summary>
    public static readonly string[] Capabilities = ["gzip-reports", "retry-after", "machines-paging", "metrics", "backup", Cache.DriverCacheEndpoints.Capability];

    public static void MapSpokaPiloteApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Sonde de vie (HEALTHCHECK Docker, reverse proxy) : publique, ne révèle rien du parc. « capabilities » (3.2) :
        // possibilités de ce serveur, pour les clients qui veulent s'adapter (les clients plus anciens l'ignorent).
        api.MapGet("/health", (ServerStore store) => store.IsHealthy()
            ? Results.Ok(new { status = "ok", version = Version, capabilities = Capabilities })
            : Results.Json(new { status = "error", version = Version }, statusCode: StatusCodes.Status503ServiceUnavailable));

        // Vérifie un jeton (connexion à la console, « spokapilote server status ») : « admin » ou « agent » pour les jetons
        // (inchangé depuis la 3.0), « user » pour une session de compte nominatif.
        api.MapGet("/whoami", (HttpContext http) =>
        {
            var p = Principal.Of(http)!;
            return p.IsSession
                ? Results.Ok(new { access = "user", version = Version, login = p.User!.Login, role = p.Role })
                : Results.Ok(new { access = p.Access.HasFlag(Access.Admin) ? "admin" : "agent", version = Version });
        }).RequireAccess(Access.Agent | Access.View);

        // ------------------------------------------------------------------ postes → console

        api.MapPost("/reports", ReceiveReportAsync).RequireAccess(Access.Agent);

        // ------------------------------------------------------------------ consultation (tous les rôles)

        // Synthèse, liste et fiche des postes, matériel sans pilote, exports (FleetEndpoints).
        api.MapFleetApi();

        var view = api.MapGroup("").RequireAccess(Access.View);

        view.MapGet("/machines/{name}/reports/{id:long}", (string name, long id, ServerStore store) =>
            store.GetHistoricReport(name, id) is { } r ? Results.Ok(r) : Results.Json(new ApiError("Rapport introuvable."), statusCode: 404));

        // Rapport brut complet : à donner à « spokapilote pack » pour préparer les pilotes d'un poste sans réseau.
        view.MapGet("/machines/{name}/raw", (string name, ServerStore store) =>
            store.GetRawReport(name) is { } raw
                ? Results.File(Encoding.UTF8.GetBytes(raw), "application/json", $"{name.ToUpperInvariant()}.json")
                : NotFound(name));

        // ------------------------------------------------------------------ administration (jeton administrateur ou rôle Administrateur)

        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapDelete("/machines/{name}", (string name, HttpContext http, ServerStore store, AuditService audit, ILogger<ServerStore> log) =>
        {
            if (!store.DeleteMachine(name)) return NotFound(name);
            log.LogInformation("Poste {Machine} supprimé de la console.", name);
            audit.Record(http, AuditActions.MachineDelete, name.ToUpperInvariant());
            return Results.NoContent();
        });

        admin.MapGet("/enrollment", (TokenService tokens, ServerOptions options) => Results.Ok(new
        {
            agentToken = tokens.AgentToken,
            version = Version,
            staleDays = options.StaleDays,
            historyPerMachine = options.HistoryPerMachine,
            maxReportMegabytes = options.MaxReportBytes / 1024 / 1024,
        }));

        // ------------------------------------------------------------------ alertes

        admin.MapGet("/settings/alerts", (AlertSettingsService settings) => Results.Ok(settings.View()));

        admin.MapPut("/settings/alerts", async (HttpRequest request, AlertSettingsService settings, TimeProvider clock, AuditService audit,
            ILogger<AlertService> log, CancellationToken ct) =>
        {
            var body = await BodyReader.ReadAsync(request, 256 * 1024, "Réglages", ct);
            if (body.Error is not null) return body.Error;
            try
            {
                var update = JsonSerializer.Deserialize<AlertSettingsUpdate>(body.Text!, ServerStore.Json)
                             ?? throw new AlertSettingsException("Réglages vides.");
                settings.Update(update, clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is JsonException or AlertSettingsException)
            {
                return Results.Json(new ApiError(ex is JsonException ? $"Réglages illisibles : {ex.Message}" : ex.Message),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            log.LogInformation("Réglages des alertes modifiés depuis {Remote}.", request.HttpContext.Connection.RemoteIpAddress);
            var current = settings.Current();
            audit.Record(request.HttpContext, AuditActions.SettingsAlerts, "Alertes",
                $"E-mail : {(current.Smtp.IsConfigured ? "configuré" : "non configuré")} · webhooks actifs : {current.Webhooks.Count(w => w.Enabled)}");
            return Results.Ok(settings.View());
        });

        // Alerte de test : envoi immédiat sur chaque canal, avec le résultat de chacun (erreur SMTP, webhook refusé...).
        admin.MapPost("/alerts/test", async (HttpContext http, AlertService alerts, AlertSender sender, AlertSettingsService settings, ServerStore store,
            AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var current = settings.Current();
            if (!current.HasChannel)
                return Results.Json(new ApiError("Aucun canal configuré : renseignez un serveur e-mail ou un webhook, puis enregistrez."),
                    statusCode: StatusCodes.Status400BadRequest);
            var alert = alerts.BuildTest(clock.GetUtcNow());
            var results = await sender.SendAsync(alert, current, ct);
            store.AddAlertEvent(clock.GetUtcNow(), null, alert.Reason, alert.Title,
                results.All(r => r.Success) ? "envoyée" : results.Any(r => r.Success) ? "partielle" : "échec",
                string.Join(" · ", results.Select(r => $"{r.Channel} : {r.Message}")));
            audit.Record(http, AuditActions.AlertTest, "Alertes", string.Join(" · ", results.Select(r => $"{r.Channel} : {(r.Success ? "OK" : "échec")}")),
                results.All(r => r.Success));
            return Results.Ok(new { results = results.Select(r => new { channel = r.Channel, success = r.Success, message = r.Message }) });
        });

        admin.MapGet("/alerts/events", (ServerStore store) => Results.Ok(store.GetAlertEvents().Select(e => new
        {
            id = e.Id,
            createdAt = e.CreatedAt,
            machine = e.Machine,
            reason = e.Reason.Split(':')[0],
            reasonLabel = AlertReasons.Label(e.Reason.Split(':')[0]),
            title = e.Title,
            status = e.Status,
            detail = e.Detail,
        })));

        // ------------------------------------------------------------------ tâches, groupes, planifications, comptes, audit

        api.MapJobApi();
        api.MapGroupApi();
        api.MapScheduleApi();
        api.MapAccountApi();
        api.MapAuditApi();
        api.MapOperationsApi();

        // ------------------------------------------------------------------ documents partagés avec les postes

        MapDocument(api, "/policy", ServerStore.PolicyKey, "Politique de pilotes", AuditActions.PolicyUpdate, AuditActions.PolicyDelete);
        MapDocument(api, "/printer-profile", ServerStore.PrinterProfileKey, "Profil d'imprimantes",
            AuditActions.PrinterProfileUpdate, AuditActions.PrinterProfileDelete);
    }

    private static async Task<IResult> ReceiveReportAsync(HttpRequest request, ServerStore store, ServerOptions options,
        AlertService alerts, AdmissionControl admission, TimeProvider clock, ILogger<ServerStore> log, CancellationToken ct)
    {
        // Afflux (démarrage simultané de milliers de postes) : file d'attente bornée, puis 503 + Retry-After étalé.
        using var lease = await admission.Uploads.EnterAsync(ct);
        if (lease is null)
        {
            log.LogDebug("Rapport refusé temporairement ({Remote}) : console surchargée.", request.HttpContext.Connection.RemoteIpAddress);
            return admission.Busy(request.HttpContext, "réception des rapports");
        }
        var body = await BodyReader.ReadAsync(request, options.MaxReportBytes, "Rapport", ct, keepCompressed: true);
        if (body.Error is not null) return body.Error;

        MachineReport report;
        var received = clock.GetUtcNow();
        try
        {
            report = ReportParser.Parse(body.Text!, received);
        }
        catch (InvalidReportException ex)
        {
            log.LogWarning("Rapport refusé ({Remote}) : {Reason}", request.HttpContext.Connection.RemoteIpAddress, ex.Message);
            return Results.Json(new ApiError(ex.Message), statusCode: StatusCodes.Status400BadRequest);
        }

        // L'adresse d'envoi sert aux règles de groupe « sous-réseau » (contrat des postes inchangé).
        var remote = request.HttpContext.Connection.RemoteIpAddress;
        var remoteIp = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        // Corps gzip du poste conservé tel quel comme rapport brut (pas de recompression).
        var saved = store.SaveReport(report, body.Text!, received, remoteIp, body.Compressed);
        if (saved.RenamedFrom is not null)
            log.LogInformation("Poste {Old} renommé en {New} (même identifiant machine).", saved.RenamedFrom, report.Name);
        try
        {
            alerts.OnReportSaved(report, saved);
        }
        catch (Exception ex)
        {
            // Une alerte ne doit jamais faire refuser un rapport.
            log.LogError(ex, "Évaluation des alertes impossible pour {Machine}.", report.Name);
        }
        log.LogInformation("Rapport reçu : {Machine} ({Model}) — {Problems} pilote(s) en erreur, {Unresolved} sans solution, {Failed} échec(s).",
            report.Name, report.System.DisplayModel, report.DriverProblems, report.Unresolved, report.Failed);
        return Results.Ok(new
        {
            machine = report.Name,
            machineId = report.System.MachineId,
            reportId = saved.ReportId,
            renamedFrom = saved.RenamedFrom,
            timestamp = report.Timestamp,
            receivedAt = received,
            healthy = report.Healthy,
            driverProblems = report.DriverProblems,
            unresolved = report.Unresolved,
        });
    }

    /// <summary>
    /// Document JSON brut stocké tel quel : GET (postes et console, 204 si absent), PUT et DELETE (administrateurs).
    /// Le format est défini par l'équipe qui le consomme ; le serveur vérifie seulement que c'est du JSON valide.
    /// </summary>
    private static void MapDocument(RouteGroupBuilder api, string route, string key, string label, string updateAction, string deleteAction)
    {
        api.MapGet(route, (HttpContext http, ServerStore store) =>
        {
            var doc = store.GetDocument(key);
            if (doc is null) return Results.NoContent();
            http.Response.Headers.LastModified = doc.UpdatedAt.ToString("R", CultureInfo.InvariantCulture);
            return Results.Text(doc.Json, "application/json; charset=utf-8");
        }).RequireAccess(Access.Agent | Access.View);

        api.MapPut(route, async (HttpRequest request, ServerStore store, ServerOptions options, TimeProvider clock, AuditService audit,
            ILogger<ServerStore> log, CancellationToken ct) =>
        {
            var body = await BodyReader.ReadAsync(request, options.MaxDocumentBytes, label, ct);
            if (body.Error is not null) return body.Error;
            try
            {
                using var _ = JsonDocument.Parse(body.Text!);
            }
            catch (JsonException ex)
            {
                return Results.Json(new ApiError($"JSON invalide : {ex.Message}"), statusCode: StatusCodes.Status400BadRequest);
            }
            var now = clock.GetUtcNow();
            store.SetDocument(key, body.Text!, now);
            log.LogInformation("{Label} mis(e) à jour ({Bytes} octets).", label, Encoding.UTF8.GetByteCount(body.Text!));
            audit.Record(request.HttpContext, updateAction, label, $"{Encoding.UTF8.GetByteCount(body.Text!)} octets");
            return Results.Ok(new { updatedAt = now, bytes = Encoding.UTF8.GetByteCount(body.Text!) });
        }).RequireAccess(Access.Admin);

        api.MapDelete(route, (HttpContext http, ServerStore store, AuditService audit) =>
            {
                if (!store.DeleteDocument(key)) return Results.Json(new ApiError($"{label} : aucun document enregistré."), statusCode: 404);
                audit.Record(http, deleteAction, label);
                return Results.NoContent();
            })
            .RequireAccess(Access.Admin);
    }

    internal static IResult NotFound(string name) =>
        Results.Json(new ApiError($"Poste « {name} » inconnu de la console."), statusCode: StatusCodes.Status404NotFound);
}
