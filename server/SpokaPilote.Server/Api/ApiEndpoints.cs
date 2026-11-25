using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SpokaPilote.Server.Fleet;
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
            Results.Ok(FleetAggregator.Summarize(store.GetMachines(), clock.GetUtcNow(), options.StaleDays)));

        admin.MapGet("/machines", (ServerStore store, ServerOptions options, TimeProvider clock) =>
            Results.Ok(FleetAggregator.Rows(store.GetMachines(), clock.GetUtcNow(), options.StaleDays)));

        admin.MapGet("/machines/{name}", (string name, ServerStore store, ServerOptions options, TimeProvider clock) =>
            store.GetMachine(name) is { } m
                ? Results.Ok(new
                {
                    machine = FleetAggregator.ToRow(m, clock.GetUtcNow(), options.StaleDays),
                    report = m.Report,
                    history = store.GetHistory(name),
                })
                : NotFound(name));

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

        // ------------------------------------------------------------------ documents partagés avec les postes

        MapDocument(api, "/policy", ServerStore.PolicyKey, "Politique de pilotes");
        MapDocument(api, "/printer-profile", ServerStore.PrinterProfileKey, "Profil d'imprimantes");
    }

    private static async Task<IResult> ReceiveReportAsync(HttpRequest request, ServerStore store, ServerOptions options,
        TimeProvider clock, ILogger<ServerStore> log, CancellationToken ct)
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

        store.SaveReport(report, body.Text!, received);
        log.LogInformation("Rapport reçu : {Machine} ({Model}) — {Problems} pilote(s) en erreur, {Unresolved} sans solution, {Failed} échec(s).",
            report.Name, report.System.DisplayModel, report.DriverProblems, report.Unresolved, report.Failed);
        return Results.Ok(new
        {
            machine = report.Name,
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
