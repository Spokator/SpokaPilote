using System.Globalization;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>Actions planifiées : consultation (tous les rôles), création et modification (Technicien et Administrateur).</summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleApi(this RouteGroupBuilder api)
    {
        var view = api.MapGroup("").RequireAccess(Access.View);
        var operate = api.MapGroup("").RequireAccess(Access.Operate);

        view.MapGet("/schedules", (ServerStore store, ScheduleRunner runner) =>
        {
            var groups = store.GetGroups().ToDictionary(g => g.Id, g => g.Name);
            return Results.Ok(new
            {
                timeZone = runner.Zone.Id,
                timeZoneName = runner.Zone.DisplayName,
                schedules = store.GetSchedules().Select(s => View(s, groups)),
            });
        });

        operate.MapPost("/schedules", async (HttpRequest request, ServerStore store, ScheduleRunner runner, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (req, error) = await GroupEndpoints.ReadAsync<ScheduleRequest>(request, ct);
            if (error is not null) return error;
            var now = clock.GetUtcNow();
            if (Build(req!, store, out var schedule) is { } invalid) return invalid;
            var by = Principal.Of(request.HttpContext)!.Actor;
            schedule = schedule with { CreatedAt = now, UpdatedAt = now, CreatedBy = by };
            schedule = schedule with { NextRun = schedule.Enabled ? runner.NextRun(schedule, now) : null };
            var id = store.CreateSchedule(schedule);
            var saved = store.GetSchedule(id)!;
            audit.Record(request.HttpContext, AuditActions.ScheduleCreate, $"{saved.Name} (n° {id})", Describe(saved, store));
            return Results.Json(View(saved, Groups(store)), statusCode: StatusCodes.Status201Created);
        });

        operate.MapPut("/schedules/{id:long}", async (long id, HttpRequest request, ServerStore store, ScheduleRunner runner, AuditService audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (store.GetSchedule(id) is not { } before) return NotFound();
            var (req, error) = await GroupEndpoints.ReadAsync<ScheduleRequest>(request, ct);
            if (error is not null) return error;
            var now = clock.GetUtcNow();
            if (Build(req!, store, out var schedule) is { } invalid) return invalid;
            schedule = schedule with { Id = id, CreatedAt = before.CreatedAt, CreatedBy = before.CreatedBy, UpdatedAt = now };
            schedule = schedule with { NextRun = schedule.Enabled ? runner.NextRun(schedule, now) : null };
            store.UpdateSchedule(schedule);
            var saved = store.GetSchedule(id)!;
            audit.Record(request.HttpContext, AuditActions.ScheduleUpdate, $"{saved.Name} (n° {id})",
                (before.Enabled != saved.Enabled ? (saved.Enabled ? "activée — " : "désactivée — ") : "") + Describe(saved, store));
            return Results.Ok(View(saved, Groups(store)));
        });

        operate.MapDelete("/schedules/{id:long}", (long id, HttpContext http, ServerStore store, AuditService audit) =>
        {
            if (store.GetSchedule(id) is not { } s) return NotFound();
            store.DeleteSchedule(id);
            audit.Record(http, AuditActions.ScheduleDelete, $"{s.Name} (n° {id})", Describe(s, store));
            return Results.NoContent();
        });
    }

    private static IResult? Build(ScheduleRequest req, ServerStore store, out ScheduleRecord schedule)
    {
        schedule = null!;
        try
        {
            var (name, type, frequency, days, time, window) = ScheduleCalculator.Validate(req);
            if (req.GroupId is { } g && store.GetGroup(g) is null) return GroupEndpoints.BadRequest("Groupe introuvable.");
            schedule = new ScheduleRecord(0, name, type.Id, req.DryRun, req.GroupId, frequency, days, time, window, req.Enabled,
                default, "", default, null, null, null);
            return null;
        }
        catch (ScheduleValidationException ex)
        {
            return GroupEndpoints.BadRequest(ex.Message);
        }
    }

    private static Dictionary<long, string> Groups(ServerStore store) => store.GetGroups().ToDictionary(g => g.Id, g => g.Name);

    private static string Describe(ScheduleRecord s, ServerStore store)
    {
        var target = s.GroupId is { } g ? $"groupe « {store.GetGroup(g)?.Name ?? $"n° {g}"} »" : "tous les postes";
        var window = s.WindowMinutes is { } w ? $", fenêtre de maintenance de {FormatWindow(w)}" : "";
        return $"{JobTypes.Find(s.JobType)?.Label ?? s.JobType}{(s.DryRun ? " (simulation)" : "")} — {target} — " +
               $"{ScheduleCalculator.Describe(s.Frequency, s.Days, s.Time)}{window}";
    }

    private static string FormatWindow(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60} h" : minutes > 60 ? $"{minutes / 60} h {minutes % 60:00}" : $"{minutes} min";

    private static object View(ScheduleRecord s, IReadOnlyDictionary<long, string> groups) => new
    {
        id = s.Id,
        name = s.Name,
        type = s.JobType,
        typeLabel = JobTypes.Find(s.JobType)?.Label ?? s.JobType,
        dryRun = s.DryRun,
        groupId = s.GroupId,
        groupName = s.GroupId is { } g ? groups.GetValueOrDefault(g) : null,
        frequency = s.Frequency,
        days = s.Days.Select(ScheduleCalculator.ToIso).Order(),
        time = s.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
        windowMinutes = s.WindowMinutes,
        enabled = s.Enabled,
        description = ScheduleCalculator.Describe(s.Frequency, s.Days, s.Time),
        createdAt = s.CreatedAt,
        createdBy = s.CreatedBy,
        updatedAt = s.UpdatedAt,
        nextRun = s.NextRun,
        lastRun = s.LastRun,
        lastResult = s.LastResult,
    };

    private static IResult NotFound() => Results.Json(new ApiError("Planification introuvable."), statusCode: StatusCodes.Status404NotFound);
}
