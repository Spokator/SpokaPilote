using System.Text.Json;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Api;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Jobs;

/// <summary>Demande de tâches depuis la console (un ou plusieurs postes).</summary>
public sealed class JobRequest
{
    public string? Type { get; set; }
    /// <summary>Postes visés (noms). Ignoré si Target vaut « to-process » ou « all ».</summary>
    public List<string>? Machines { get; set; }
    /// <summary>« selection » (défaut), « to-process » (tous les postes à traiter) ou « all » (tout le parc).</summary>
    public string? Target { get; set; }
    public bool DryRun { get; set; }
    /// <summary>Nom du technicien (facultatif), affiché dans l'historique des tâches.</summary>
    public string? RequestedBy { get; set; }
}

/// <summary>Résultat envoyé par l'agent du poste.</summary>
public sealed class JobResultRequest
{
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string? Message { get; set; }
    /// <summary>Rapport produit par la tâche (identifiant renvoyé par POST /api/reports).</summary>
    public long? ReportId { get; set; }
    /// <summary>Journal d'exécution (fin de la sortie console), 64 Ko au plus conservés.</summary>
    public string? Log { get; set; }
}

/// <summary>API des tâches à distance : administration (console) et agent (postes).</summary>
public static class JobEndpoints
{
    public const string MachineIdHeader = "X-SpokaPilote-Machine-Id";
    public const string MachineNameHeader = "X-SpokaPilote-Machine";

    public static object View(JobRecord j, bool withLog = false) => new
    {
        id = j.Id,
        machine = j.Machine,
        type = j.Type,
        typeLabel = JobTypes.Find(j.Type)?.Label ?? j.Type,
        dryRun = j.Parameters.DryRun,
        createdAt = j.CreatedAt,
        createdBy = j.CreatedBy,
        state = j.State,
        startedAt = j.StartedAt,
        finishedAt = j.FinishedAt,
        message = j.Message,
        exitCode = j.ExitCode,
        reportId = j.ReportId,
        log = withLog ? j.Log : null,
    };

    public static void MapJobApi(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapGet("/job-types", () => Results.Ok(JobTypes.All.Select(t => new
        {
            id = t.Id, label = t.Label, description = t.Description, changesSystem = t.ChangesSystem, supportsDryRun = t.SupportsDryRun,
        })));

        admin.MapGet("/jobs", (string? state, string? machine, int? limit, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            store.ExpireJobs(clock.GetUtcNow(), options.JobExpiryDays, options.JobRunningTimeoutHours);
            var states = JobStates.Filter(state);
            return Results.Ok(store.ListJobs(states, machine, limit ?? 500).Select(j => View(j)));
        });

        admin.MapGet("/jobs/{id:long}", (long id, ServerStore store) =>
            store.GetJob(id) is { } j ? Results.Ok(View(j, withLog: true)) : JobNotFound());

        admin.MapPost("/jobs", CreateAsync);

        admin.MapPost("/jobs/{id:long}/cancel", (long id, HttpContext http, ServerStore store, TimeProvider clock, ILogger<ServerStore> log) =>
        {
            var job = store.GetJob(id, withLog: false);
            if (job is null) return JobNotFound();
            if (!store.CancelJob(id, clock.GetUtcNow(), "administrateur"))
                return Results.Json(new ApiError(job.State == JobStates.Running
                    ? "La tâche est déjà en cours sur le poste : elle ne peut plus être annulée."
                    : "Seule une tâche en attente peut être annulée."), statusCode: StatusCodes.Status409Conflict);
            log.LogInformation("Tâche {Id} ({Type}) du poste {Machine} annulée depuis {Remote}.", id, job.Type, job.Machine, http.Connection.RemoteIpAddress);
            return Results.Ok(View(store.GetJob(id, withLog: false)!));
        });

        // ------------------------------------------------------------------ agent (jeton d'enrôlement)

        var agent = api.MapGroup("/agent").RequireAccess(Access.Agent);

        agent.MapGet("/jobs", (HttpRequest request, ServerStore store, ServerOptions options, TimeProvider clock, ILogger<ServerStore> log) =>
        {
            if (Identify(request, out var machineId, out var name) is { } error) return error;
            var now = clock.GetUtcNow();
            store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours);
            var machine = store.FindMachineForAgent(machineId!, name, now);
            if (machine is null)
                return Results.Ok(new { known = false, machine = name, jobs = Array.Empty<object>() });
            var jobs = store.ClaimPendingJobs(machine.Id, now);
            foreach (var j in jobs) log.LogInformation("Tâche {Id} ({Type}) remise au poste {Machine}.", j.Id, j.Type, machine.Name);
            return Results.Ok(new
            {
                known = true,
                machine = machine.Name,
                jobs = jobs.Select(j => new { id = j.Id, type = j.Type, dryRun = j.Parameters.DryRun, createdAt = j.CreatedAt, createdBy = j.CreatedBy }),
            });
        });

        agent.MapPost("/jobs/{id:long}/result", async (long id, HttpRequest request, ServerStore store, AlertService alerts, TimeProvider clock,
            ILogger<ServerStore> log, CancellationToken ct) =>
        {
            if (Identify(request, out var machineId, out var name) is { } error) return error;
            var body = await BodyReader.ReadAsync(request, ServerStore.MaxJobLogChars * 4L + 16 * 1024, "Résultat", ct);
            if (body.Error is not null) return body.Error;
            JobResultRequest? result;
            try { result = JsonSerializer.Deserialize<JobResultRequest>(body.Text!, ServerStore.Json); }
            catch (JsonException ex) { return Results.Json(new ApiError($"Résultat illisible : {ex.Message}"), statusCode: 400); }
            if (result is null) return Results.Json(new ApiError("Résultat vide."), statusCode: 400);

            var now = clock.GetUtcNow();
            var machine = store.FindMachineForAgent(machineId!, name, now);
            if (machine is null) return JobNotFound();
            var reportId = result.ReportId is { } r && store.ReportBelongsTo(r, machine.Id) ? r : (long?)null;
            var message = result.Message is { Length: > 2000 } m ? m[..2000] + "…" : result.Message;
            var (status, job) = store.CompleteJob(id, machine.Id, result.Success, result.ExitCode, message, reportId, result.Log, now);
            switch (status)
            {
                case JobCompletion.NotFound:
                    return JobNotFound();
                case JobCompletion.NotRunning:
                    return Results.Json(new ApiError($"La tâche {id} n'est plus en cours (état : {job!.State}) : résultat ignoré."), statusCode: StatusCodes.Status409Conflict);
            }
            log.LogInformation("Tâche {Id} ({Type}) du poste {Machine} : {State} — {Message}", job!.Id, job.Type, machine.Name, job.State, job.Message);
            alerts.OnJobFinished(job);
            return Results.Ok(View(job));
        });
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, ServerStore store, ServerOptions options, TimeProvider clock,
        ILogger<ServerStore> log, CancellationToken ct)
    {
        var body = await BodyReader.ReadAsync(request, 256 * 1024, "Demande", ct);
        if (body.Error is not null) return body.Error;
        JobRequest? req;
        try { req = JsonSerializer.Deserialize<JobRequest>(body.Text!, ServerStore.Json); }
        catch (JsonException ex) { return Results.Json(new ApiError($"Demande illisible : {ex.Message}"), statusCode: 400); }

        // Liste blanche : aucun autre type n'est accepté (jamais de commande ni de script).
        var type = JobTypes.Find(req?.Type);
        if (req is null || type is null)
            return Results.Json(new ApiError($"Type de tâche inconnu : « {req?.Type} ». Types autorisés : {string.Join(", ", JobTypes.All.Select(t => t.Id))}."),
                statusCode: StatusCodes.Status400BadRequest);
        if (req.DryRun && !type.SupportsDryRun)
            return Results.Json(new ApiError($"« {type.Label} » ne propose pas de simulation (l'action ne modifie rien)."), statusCode: 400);

        var now = clock.GetUtcNow();
        var all = store.GetMachines();
        List<StoredMachine> targets;
        var unknown = new List<string>();
        switch (req.Target?.Trim().ToLowerInvariant())
        {
            case "to-process":
                targets = all.Where(m => !m.Report.Healthy).ToList();
                break;
            case "all":
                targets = all;
                break;
            case null or "" or "selection":
                var names = (req.Machines ?? new List<string>()).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 0) return Results.Json(new ApiError("Aucun poste sélectionné."), statusCode: 400);
                var byName = all.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                targets = names.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
                unknown = names.Where(n => !byName.ContainsKey(n)).ToList();
                break;
            default:
                return Results.Json(new ApiError("Cible inconnue : selection, to-process ou all."), statusCode: 400);
        }
        if (targets.Count == 0 && unknown.Count == 0)
            return Results.Json(new ApiError(req.Target == "to-process" ? "Aucun poste à traiter." : "Aucun poste visé."), statusCode: 400);

        var by = string.IsNullOrWhiteSpace(req.RequestedBy) ? "administrateur"
            : new string(req.RequestedBy.Trim().Where(c => !char.IsControl(c)).Take(60).ToArray());
        var created = new List<object>();
        var skipped = unknown.Select(n => (object)new { machine = n, reason = "Poste inconnu de la console." }).ToList();
        foreach (var m in targets)
        {
            var (job, isNew) = store.CreateJob(m.Id, type.Id, new JobParameters(req.DryRun), by, now);
            if (isNew) created.Add(View(job));
            else skipped.Add(new { machine = m.Name, reason = $"Une tâche identique (n° {job.Id}) est déjà en attente." });
        }
        log.LogInformation("{Count} tâche(s) « {Type} »{DryRun} créée(s) par {By} depuis {Remote}.", created.Count, type.Id,
            req.DryRun ? " (simulation)" : "", by, request.HttpContext.Connection.RemoteIpAddress);
        return Results.Ok(new { created, skipped });
    }

    /// <summary>Identité du poste qui interroge sa file : identifiant machine obligatoire (MachineGuid), nom facultatif.</summary>
    private static IResult? Identify(HttpRequest request, out string? machineId, out string? name)
    {
        machineId = ReportParser.NormalizeMachineId(request.Headers[MachineIdHeader].ToString());
        var rawName = request.Headers[MachineNameHeader].ToString().Trim();
        name = ReportParser.IsValidMachineName(rawName) ? rawName.ToUpperInvariant() : null;
        return machineId is null
            ? Results.Json(new ApiError($"En-tête {MachineIdHeader} absent ou invalide : mettez SpokaPilote à jour sur ce poste (3.0 ou ultérieur)."),
                statusCode: StatusCodes.Status400BadRequest)
            : null;
    }

    private static IResult JobNotFound() => Results.Json(new ApiError("Tâche introuvable."), statusCode: StatusCodes.Status404NotFound);
}
