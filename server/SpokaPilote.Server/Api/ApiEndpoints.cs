using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>API REST de la console centrale (toutes les routes sous /api).</summary>
public static class ApiEndpoints
{
    public static string Version { get; } =
        typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void MapSpokaPiloteApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Sonde de vie (HEALTHCHECK Docker, reverse proxy) : publique, ne révèle rien du parc.
        api.MapGet("/health", (ServerStore store) => store.IsHealthy()
            ? Results.Ok(new { status = "ok", version = Version })
            : Results.Json(new { status = "error", version = Version }, statusCode: StatusCodes.Status503ServiceUnavailable));

        // Vérifie un jeton (connexion à la console, « spokapilote server status »).
        api.MapGet("/whoami", (HttpContext http) =>
            Results.Ok(new { access = (Access)http.Items[RequireAccessFilter.AccessItem]! == Access.Admin ? "admin" : "agent", version = Version }))
            .RequireAccess(Access.Agent | Access.Admin);

        // ------------------------------------------------------------------ postes → console

        api.MapPost("/reports", ReceiveReportAsync).RequireAccess(Access.Agent);

        // ------------------------------------------------------------------ administration

        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapGet("/fleet", (ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours);
            var summary = FleetAggregator.Summarize(store.GetMachines(), now, options.StaleDays);
            var jobs = store.GetJobStats(now);
            summary.PendingJobs = jobs.Pending;
            summary.RunningJobs = jobs.Running;
            summary.FailedJobs24h = jobs.FailedLast24h;
            return Results.Ok(summary);
        });

        admin.MapGet("/machines", (ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            var machines = store.GetMachines();
            var active = store.GetActiveJobCounts();
            var ids = machines.ToDictionary(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);
            var rows = FleetAggregator.Rows(machines, clock.GetUtcNow(), options.StaleDays);
            foreach (var r in rows) r.ActiveJobs = active.GetValueOrDefault(ids[r.Name]);
            return Results.Ok(rows);
        });

        admin.MapGet("/machines/{name}", (string name, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            if (store.GetMachine(name) is not { } m) return NotFound(name);
            var now = clock.GetUtcNow();
            store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours);
            var row = FleetAggregator.ToRow(m, now, options.StaleDays);
            var jobs = store.ListJobs(machine: m.Name, limit: 20);
            row.ActiveJobs = jobs.Count(j => j.State is JobStates.Pending or JobStates.Running);
            return Results.Ok(new
            {
                machine = row,
                report = m.Report,
                history = store.GetHistory(m.Name),
                jobs = jobs.Select(j => JobEndpoints.View(j)),
            });
        });

        admin.MapGet("/machines/{name}/reports/{id:long}", (string name, long id, ServerStore store) =>
            store.GetHistoricReport(name, id) is { } r ? Results.Ok(r) : Results.Json(new ApiError("Rapport introuvable."), statusCode: 404));

        // Rapport brut complet : à donner à « spokapilote pack » pour préparer les pilotes d'un poste sans réseau.
        admin.MapGet("/machines/{name}/raw", (string name, ServerStore store) =>
            store.GetRawReport(name) is { } raw
                ? Results.File(Encoding.UTF8.GetBytes(raw), "application/json", $"{name.ToUpperInvariant()}.json")
                : NotFound(name));

        admin.MapDelete("/machines/{name}", (string name, ServerStore store, ILogger<ServerStore> log) =>
        {
            if (!store.DeleteMachine(name)) return NotFound(name);
            log.LogInformation("Poste {Machine} supprimé de la console.", name);
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

        admin.MapPut("/settings/alerts", async (HttpRequest request, AlertSettingsService settings, TimeProvider clock,
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
            return Results.Ok(settings.View());
        });

        // Alerte de test : envoi immédiat sur chaque canal, avec le résultat de chacun (erreur SMTP, webhook refusé...).
        admin.MapPost("/alerts/test", async (AlertService alerts, AlertSender sender, AlertSettingsService settings, ServerStore store,
            TimeProvider clock, CancellationToken ct) =>
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

        // ------------------------------------------------------------------ tâches à distance

        api.MapJobApi();

        // ------------------------------------------------------------------ documents partagés avec les postes

        MapDocument(api, "/policy", ServerStore.PolicyKey, "Politique de pilotes");
        MapDocument(api, "/printer-profile", ServerStore.PrinterProfileKey, "Profil d'imprimantes");
    }

    private static async Task<IResult> ReceiveReportAsync(HttpRequest request, ServerStore store, ServerOptions options,
        AlertService alerts, TimeProvider clock, ILogger<ServerStore> log, CancellationToken ct)
    {
        var body = await BodyReader.ReadAsync(request, options.MaxReportBytes, "Rapport", ct);
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

        var saved = store.SaveReport(report, body.Text!, received);
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
    /// Document JSON brut stocké tel quel : GET (postes et administrateurs, 204 si absent), PUT et DELETE (administrateurs).
    /// Le format est défini par l'équipe qui le consomme ; le serveur vérifie seulement que c'est du JSON valide.
    /// </summary>
    private static void MapDocument(RouteGroupBuilder api, string route, string key, string label)
    {
        api.MapGet(route, (HttpContext http, ServerStore store) =>
        {
            var doc = store.GetDocument(key);
            if (doc is null) return Results.NoContent();
            http.Response.Headers.LastModified = doc.UpdatedAt.ToString("R", CultureInfo.InvariantCulture);
            return Results.Text(doc.Json, "application/json; charset=utf-8");
        }).RequireAccess(Access.Agent | Access.Admin);

        api.MapPut(route, async (HttpRequest request, ServerStore store, ServerOptions options, TimeProvider clock,
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
            return Results.Ok(new { updatedAt = now, bytes = Encoding.UTF8.GetByteCount(body.Text!) });
        }).RequireAccess(Access.Admin);

        api.MapDelete(route, (ServerStore store) => store.DeleteDocument(key) ? Results.NoContent()
                : Results.Json(new ApiError($"{label} : aucun document enregistré."), statusCode: 404))
            .RequireAccess(Access.Admin);
    }

    private static IResult NotFound(string name) =>
        Results.Json(new ApiError($"Poste « {name} » inconnu de la console."), statusCode: StatusCodes.Status404NotFound);
}
