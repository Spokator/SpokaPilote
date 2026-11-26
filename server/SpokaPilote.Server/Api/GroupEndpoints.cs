using System.Text.Json;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>Création ou modification d'un groupe (console).</summary>
public sealed class GroupRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public GroupRules? Rules { get; set; }
}

/// <summary>Ajout et retrait de membres manuels (noms des postes).</summary>
public sealed class GroupMembersRequest
{
    public List<string>? Add { get; set; }
    public List<string>? Remove { get; set; }
}

/// <summary>Groupes de postes : consultation (tous les rôles), création et modification (Technicien et Administrateur).</summary>
public static class GroupEndpoints
{
    public static IResult GroupNotFound() => Results.Json(new ApiError("Groupe introuvable."), statusCode: StatusCodes.Status404NotFound);

    public static void MapGroupApi(this RouteGroupBuilder api)
    {
        var view = api.MapGroup("").RequireAccess(Access.View);
        var operate = api.MapGroup("").RequireAccess(Access.Operate);

        view.MapGet("/group-fields", () => Results.Ok(new
        {
            fields = GroupMatcher.Fields.Select(f => new { id = f.Key, label = f.Value }),
            operators = GroupMatcher.Operators.Select(o => new { id = o.Key, label = o.Value }),
        }));

        view.MapGet("/groups", (ServerStore store) =>
        {
            var counts = store.GetGroupCounts();
            var schedules = store.GetSchedules();
            return Results.Ok(store.GetGroups().Select(g => Summary(g, counts, schedules.Count(s => s.GroupId == g.Id))));
        });

        // Membres : tous par défaut (comme en 3.1) ; ?limit=&offset= (3.2) pour une page, toProcessCount pour tout le groupe.
        view.MapGet("/groups/{id:long}", (long id, int? limit, int? offset, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            if (store.GetGroup(id) is not { } g) return GroupNotFound();
            var now = clock.GetUtcNow();
            var schedules = store.GetSchedules().Where(s => s.GroupId == id).ToList();
            var (total, toProcess, members) = store.GetGroupMembers(id, offset ?? 0, limit is null ? null : Math.Clamp(limit.Value, 1, 10_000));
            var rows = members.Select(x =>
            {
                var m = x.Machine;
                return new
                {
                    name = m.Name, model = m.DisplayModel, os = m.Os, healthy = m.Healthy, stale = FleetAggregator.IsStale(m.LastSeen, now, options.StaleDays),
                    lastReport = m.LastReport, lastIp = m.LastIp, manual = x.Manual, byRule = x.ByRule,
                };
            });
            return Results.Ok(new
            {
                group = Summary(g, store.GetGroupCounts(), schedules.Count),
                members = limit is null ? rows.OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase).ToList() : rows.ToList(),
                memberTotal = total,
                toProcessCount = toProcess,
                schedules = schedules.Select(s => new { id = s.Id, name = s.Name, enabled = s.Enabled }),
            });
        });

        // Aperçu des règles avant enregistrement : postes qui seraient désignés.
        view.MapPost("/groups/preview", async (HttpRequest request, ServerStore store, CancellationToken ct) =>
        {
            var (req, error) = await ReadAsync<GroupRequest>(request, ct);
            if (error is not null) return error;
            GroupRules rules;
            try { rules = GroupMatcher.Normalize(req!.Rules); }
            catch (GroupValidationException ex) { return BadRequest(ex.Message); }
            var matching = store.PreviewRules(rules).OrderBy(n => n).ToList();
            return Results.Ok(new { count = matching.Count, machines = matching.Take(50), description = GroupMatcher.Describe(rules) });
        });

        operate.MapPost("/groups", async (HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var (req, error) = await ReadAsync<GroupRequest>(request, ct);
            if (error is not null) return error;
            if (Validate(req!, out var name, out var description, out var rules) is { } invalid) return invalid;
            var id = store.CreateGroup(name, description, rules, clock.GetUtcNow());
            if (id is null) return Conflict($"Un groupe « {name} » existe déjà.");
            audit.Record(request.HttpContext, AuditActions.GroupCreate, name, RulesText(rules));
            var g = store.GetGroup(id.Value)!;
            return Results.Json(Summary(g, store.GetGroupCounts(), 0), statusCode: StatusCodes.Status201Created);
        });

        operate.MapPut("/groups/{id:long}", async (long id, HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (store.GetGroup(id) is not { } before) return GroupNotFound();
            var (req, error) = await ReadAsync<GroupRequest>(request, ct);
            if (error is not null) return error;
            if (Validate(req!, out var name, out var description, out var rules) is { } invalid) return invalid;
            try
            {
                store.UpdateGroup(id, name, description, rules, clock.GetUtcNow());
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
            var changes = new List<string>();
            if (!string.Equals(before.Name, name, StringComparison.Ordinal)) changes.Add($"renommé (ancien nom : « {before.Name} »)");
            if (!string.Equals(before.Description ?? "", description ?? "", StringComparison.Ordinal)) changes.Add("description modifiée");
            var oldRules = RulesText(before.Rules);
            var newRules = RulesText(rules);
            if (oldRules != newRules) changes.Add($"règles : {newRules}");
            audit.Record(request.HttpContext, AuditActions.GroupUpdate, name, changes.Count == 0 ? "aucun changement" : string.Join(" ; ", changes));
            return Results.Ok(Summary(store.GetGroup(id)!, store.GetGroupCounts(), store.CountSchedulesForGroup(id)));
        });

        operate.MapDelete("/groups/{id:long}", (long id, HttpContext http, ServerStore store, AuditService audit) =>
        {
            if (store.GetGroup(id) is not { } g) return GroupNotFound();
            var used = store.CountSchedulesForGroup(id);
            if (used > 0)
                return Conflict($"Le groupe « {g.Name} » est utilisé par {used} planification(s) : supprimez-les ou changez leur cible d'abord.");
            store.DeleteGroup(id);
            audit.Record(http, AuditActions.GroupDelete, g.Name);
            return Results.NoContent();
        });

        operate.MapPost("/groups/{id:long}/members", async (long id, HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (store.GetGroup(id) is not { } g) return GroupNotFound();
            var (req, error) = await ReadAsync<GroupMembersRequest>(request, ct);
            if (error is not null) return error;
            List<string> Clean(List<string>? names) =>
                (names ?? new List<string>()).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var add = Clean(req!.Add);
            var remove = Clean(req.Remove);
            if (add.Count + remove.Count == 0) return BadRequest("Aucun poste indiqué.");
            if (add.Count + remove.Count > 10_000) return BadRequest("Trop de postes en une seule demande.");
            var byName = store.FindMachinesByName(add.Concat(remove));
            var unknown = add.Concat(remove).Where(n => !byName.ContainsKey(n)).ToList();
            var changes = store.ChangeGroupMembers(id, add.Where(byName.ContainsKey).Select(n => byName[n].Id),
                remove.Where(byName.ContainsKey).Select(n => byName[n].Id), clock.GetUtcNow());
            var known = add.Where(byName.ContainsKey).Select(n => byName[n].Name).ToList();
            var gone = remove.Where(byName.ContainsKey).Select(n => byName[n].Name).ToList();
            if (changes > 0)
                audit.Record(request.HttpContext, AuditActions.GroupMembers, g.Name,
                    string.Join(" ; ", new[]
                    {
                        known.Count > 0 ? $"ajout : {Names(known)}" : null,
                        gone.Count > 0 ? $"retrait : {Names(gone)}" : null,
                    }.Where(x => x is not null)));
            return Results.Ok(new { changes, unknown, group = Summary(store.GetGroup(id)!, store.GetGroupCounts(), store.CountSchedulesForGroup(id)) });
        });
    }

    private static object Summary(GroupRecord g, IReadOnlyDictionary<long, (int Members, int Manual)> counts, int scheduleCount) => new
    {
        id = g.Id,
        name = g.Name,
        description = g.Description,
        rules = g.Rules,
        rulesText = GroupMatcher.Describe(g.Rules),
        manualCount = counts.GetValueOrDefault(g.Id).Manual,
        memberCount = counts.GetValueOrDefault(g.Id).Members,
        scheduleCount,
        createdAt = g.CreatedAt,
        updatedAt = g.UpdatedAt,
    };

    private static IResult? Validate(GroupRequest req, out string name, out string? description, out GroupRules rules)
    {
        name = new string((req.Name ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
        description = string.IsNullOrWhiteSpace(req.Description) ? null : new string(req.Description.Trim().Where(c => c is '\n' || !char.IsControl(c)).ToArray());
        rules = new GroupRules();
        if (name.Length is 0 or > 80) return BadRequest("Nom du groupe obligatoire (80 caractères au plus).");
        if (description is { Length: > 300 }) return BadRequest("Description trop longue (300 caractères au plus).");
        try
        {
            rules = GroupMatcher.Normalize(req.Rules);
        }
        catch (GroupValidationException ex)
        {
            return BadRequest(ex.Message);
        }
        return null;
    }

    private static string RulesText(GroupRules rules) => rules.IsEmpty ? "aucune règle (membres manuels uniquement)" : GroupMatcher.Describe(rules);

    private static string Names(List<string> names) =>
        names.Count <= 10 ? string.Join(", ", names) : string.Join(", ", names.Take(10)) + $" et {names.Count - 10} autre(s)";

    internal static async Task<(T? Value, IResult? Error)> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        var body = await BodyReader.ReadAsync(request, 256 * 1024, "Demande", ct);
        if (body.Error is not null) return (null, body.Error);
        try
        {
            var value = JsonSerializer.Deserialize<T>(body.Text!, ServerStore.Json);
            return value is null ? (null, BadRequest("Demande vide.")) : (value, null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest($"Demande illisible : {ex.Message}"));
        }
    }

    internal static IResult BadRequest(string message) => Results.Json(new ApiError(message), statusCode: StatusCodes.Status400BadRequest);
    internal static IResult Conflict(string message) => Results.Json(new ApiError(message), statusCode: StatusCodes.Status409Conflict);
}
