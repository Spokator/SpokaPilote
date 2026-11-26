using System.Globalization;
using System.Text;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>
/// Consultation du parc : synthèse, liste des postes (filtres, tri et pagination côté serveur depuis la 3.2), fiche d'un
/// poste, matériel sans pilote, exports CSV en continu. Les routes et réponses historiques sont inchangées : les nouvelles
/// possibilités passent par des paramètres facultatifs et de nouvelles routes.
/// </summary>
public static class FleetEndpoints
{
    public const int MaxPageSize = 1000;

    public static void MapFleetApi(this RouteGroupBuilder api)
    {
        var view = api.MapGroup("").RequireAccess(Access.View);

        // Synthèse du parc, éventuellement limitée à un groupe (?group=id). ?top=N (3.2) : listes limitées aux N premiers
        // (postes à traiter, non vus, matériel sans pilote) ; les compteurs restent ceux du parc entier.
        view.MapGet("/fleet", (long? group, int? top, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours);
            if (group is { } groupId && !store.GroupExists(groupId)) return GroupEndpoints.GroupNotFound();
            var cached = store.GetFleetSummary(group, now, options.StaleDays);
            var limit = top is null ? (int?)null : Math.Clamp(top.Value, 0, 100_000);
            // Dernier contact des agents : frais à chaque appel (il n'entre pas dans la révision du parc mise en cache).
            var shown = (limit is { } n ? cached.ToProcessMachines.Take(n).Concat(cached.StaleMachines.Take(n))
                : cached.ToProcessMachines.Concat(cached.StaleMachines)).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var seen = shown.Count == 0 ? new Dictionary<string, DateTimeOffset?>() : store.GetAgentSeen(shown.Count <= 2000 ? shown : null);
            var summary = cached.Copy(limit, row => row.AgentSeen = seen.GetValueOrDefault(row.Name));
            var jobs = store.GetJobStats(now, group);
            summary.PendingJobs = jobs.Pending;
            summary.RunningJobs = jobs.Running;
            summary.FailedJobs24h = jobs.FailedLast24h;
            return Results.Ok(summary);
        });

        // Liste des postes. Sans paramètre de pagination : tableau de tous les postes, comme en 3.1 (?group=id pour un
        // groupe). 3.2 : filtres ?q= (nom, modèle, n° de série, Windows), ?status=, ?group=none, tri ?sort=&dir=, et avec
        // ?page=&pageSize= ou ?offset=&limit= une page { total, items, ... } calculée par la base.
        view.MapGet("/machines", (HttpRequest request, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            var (query, error) = ParseQuery(request, store, out var paged);
            if (error is not null) return error;
            var now = clock.GetUtcNow();
            var page = store.QueryMachines(query!, now, options.StaleDays);
            if (!paged) return Results.Ok(page.Rows);
            var fleet = store.CountMachines();
            (int Machines, int ToProcess, bool AnyHealth)? inGroup = query!.GroupId is { } g ? store.CountMachines(g) : null;
            return Results.Ok(new
            {
                total = page.Total,
                offset = query.Offset,
                limit = query.Limit,
                sort = query.Sort,
                dir = query.Descending ? "desc" : "asc",
                items = page.Rows,
                machineCount = fleet.Machines,
                toProcessCount = fleet.ToProcess,
                hasHealth = fleet.AnyHealth,
                group = inGroup is { } c ? new { id = query.GroupId, machines = c.Machines, toProcess = c.ToProcess } : null,
            });
        });

        // Export CSV de la liste des postes (mêmes filtres que /machines), écrit en continu : mémoire constante.
        view.MapGet("/export/machines", (HttpContext http, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            var (query, error) = ParseQuery(http.Request, store, out _);
            if (error is not null) return error;
            var now = clock.GetUtcNow();
            return new CsvResult($"spokapilote-postes-{now:yyyy-MM-dd}.csv", async writer =>
            {
                await writer.WriteAsync("Poste;Identifiant;Constructeur;Modèle;N° de série;Windows;Version;Architecture;SpokaPilote;État;Non vu;" +
                                        "Santé;Note;Périphériques;Pilotes en erreur;Sans solution;Pannes matérielles;Proposés;Bloqués;Installés;Échecs;" +
                                        "Pilote dangereux;Dernier rapport (UTC);Dernier contact (UTC);Agent (UTC);Adresse IP;Groupes\r\n");
                foreach (var (m, groups) in store.StreamMachines(query! with { Offset = 0, Limit = null }, now, options.StaleDays))
                {
                    var s = m.System;
                    await writer.WriteAsync(string.Join(';',
                        Cell(m.Name), Cell(m.MachineId), Cell(s.Manufacturer), Cell(m.DisplayModel), Cell(s.SerialNumber), Cell(m.Os), Cell(s.OsVersion),
                        Cell(s.Architecture), Cell(m.AppVersion), Cell(m.Healthy ? "sain" : "à traiter"),
                        Cell(FleetAggregator.IsStale(m.LastSeen, now, options.StaleDays) ? "oui" : "non"),
                        Number(m.HealthScore), Cell(m.HealthGrade), Number(m.DeviceCount), Number(m.DriverProblems), Number(m.Unresolved),
                        Number(m.HardwareFaults), Number(m.Proposed), Number(m.Blocked), Number(m.Installed), Number(m.Failed),
                        Cell(m.DangerousDriver ? "oui" : "non"), Date(m.LastReport), Date(m.LastSeen), Date(m.AgentSeen), Cell(m.LastIp),
                        Cell(string.Join(", ", groups))) + "\r\n");
                }
            });
        });

        view.MapGet("/machines/{name}", (string name, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            if (store.GetMachine(name) is not { } m) return ApiEndpoints.NotFound(name);
            var now = clock.GetUtcNow();
            store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours, m.Id);
            var row = FleetAggregator.ToRow(m, now, options.StaleDays);
            var jobs = store.ListJobs(machine: m.Name, limit: 20);
            row.ActiveJobs = jobs.Count(j => j.State is JobStates.Pending or JobStates.Running);
            var groups = store.GetMachineGroupDetails(m.Id);
            row.Groups = groups.Select(g => g.Id).ToList();
            return Results.Ok(new
            {
                machine = row,
                report = m.Report,
                history = store.GetHistory(m.Name),
                jobs = jobs.Select(j => JobEndpoints.View(j)),
                groups = groups.Select(g => new { id = g.Id, name = g.Name, manual = g.Manual, byRule = g.ByRule }),
            });
        });

        // Matériel sans pilote (3.2) : liste paginée et filtrable (?q=), postes concernés limités à ?machines=N par ligne.
        view.MapGet("/missing-drivers", (long? group, string? q, int? offset, int? limit, int? machines, ServerStore store, ServerOptions options,
            TimeProvider clock) =>
        {
            if (group is { } groupId && !store.GroupExists(groupId)) return GroupEndpoints.GroupNotFound();
            var summary = store.GetFleetSummary(group, clock.GetUtcNow(), options.StaleDays);
            var list = FilterMissing(summary.MissingDrivers, q);
            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? 100, 1, 5000);
            var perRow = Math.Clamp(machines ?? 12, 0, 1_000_000);
            return Results.Ok(new
            {
                total = list.Count,
                kinds = summary.MissingDrivers.Count,
                devices = summary.Unresolved,
                offset = skip,
                limit = take,
                items = list.Skip(skip).Take(take).Select(h => new
                {
                    hardwareId = h.HardwareId, name = h.Name, errorCode = h.ErrorCode, statusText = h.StatusText, hint = h.Hint, count = h.Count,
                    blockedOn = h.BlockedOn, models = h.Models, machines = h.Machines.Take(perRow), moreMachines = Math.Max(0, h.Count - perRow),
                }),
            });
        });

        view.MapGet("/export/missing-drivers", (long? group, string? q, ServerStore store, ServerOptions options, TimeProvider clock) =>
        {
            if (group is { } groupId && !store.GroupExists(groupId)) return GroupEndpoints.GroupNotFound();
            var now = clock.GetUtcNow();
            var list = FilterMissing(store.GetFleetSummary(group, now, options.StaleDays).MissingDrivers, q);
            return new CsvResult($"materiel-sans-pilote-{now:yyyy-MM-dd}.csv", async writer =>
            {
                await writer.WriteAsync("Périphérique;ID matériel;Code d'erreur;Nombre de postes;Postes;Modèles;Bloqué sur\r\n");
                foreach (var h in list)
                    await writer.WriteAsync(string.Join(';', Cell(h.Name), Cell(h.HardwareId), Number(h.ErrorCode), Number(h.Count),
                        Cell(string.Join(", ", h.Machines)), Cell(string.Join(", ", h.Models)), Number(h.BlockedOn)) + "\r\n");
            });
        });
    }

    private static List<MissingHardware> FilterMissing(List<MissingHardware> list, string? q)
    {
        var needle = q?.Trim();
        return string.IsNullOrEmpty(needle)
            ? list
            : list.Where(h => h.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                              h.HardwareId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                              h.Machines.Any(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                              h.Models.Any(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Paramètres de la liste des postes (validés) ; <paramref name="paged"/> : une page est demandée.</summary>
    internal static (MachineQuery? Query, IResult? Error) ParseQuery(HttpRequest request, ServerStore store, out bool paged)
    {
        var q = request.Query;
        string? S(string key) => q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;
        int? N(string key, out bool bad)
        {
            bad = false;
            if (S(key) is not { } text) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            bad = true;
            return null;
        }
        IResult Bad(string message) => Results.Json(new ApiError(message), statusCode: StatusCodes.Status400BadRequest);

        paged = false;
        long? groupId = null;
        var withoutGroup = false;
        if (S("group") is { } group)
        {
            if (group.Equals("none", StringComparison.OrdinalIgnoreCase)) withoutGroup = true;
            else if (long.TryParse(group, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                if (!store.GroupExists(id)) return (null, GroupEndpoints.GroupNotFound());
                groupId = id;
            }
            else return (null, Bad("Paramètre group : identifiant de groupe ou « none »."));
        }
        var status = S("status")?.ToLowerInvariant();
        if (status is not null && !ServerStore.StatusFilters.Contains(status))
            return (null, Bad($"Filtre d'état inconnu : « {status} » ({string.Join(", ", ServerStore.StatusFilters)})."));
        var sort = S("sort");
        if (sort is not null)
        {
            sort = ServerStore.SortKeys.FirstOrDefault(k => k.Equals(sort, StringComparison.OrdinalIgnoreCase));
            if (sort is null) return (null, Bad($"Tri inconnu ({string.Join(", ", ServerStore.SortKeys)})."));
        }
        var dir = S("dir")?.ToLowerInvariant();
        if (dir is not (null or "asc" or "desc")) return (null, Bad("Paramètre dir : asc ou desc."));

        var limit = N("limit", out var b1);
        var offset = N("offset", out var b2);
        var page = N("page", out var b3);
        var pageSize = N("pageSize", out var b4);
        if (b1 || b2 || b3 || b4) return (null, Bad("Paramètres de pagination : nombres entiers attendus."));
        paged = limit is not null || offset is not null || page is not null || pageSize is not null;
        int? take = null;
        var skip = 0;
        if (paged)
        {
            if (page is not null || pageSize is not null)
            {
                take = Math.Clamp(pageSize ?? 100, 1, MaxPageSize);
                skip = (Math.Max(1, page ?? 1) - 1) * take.Value;
            }
            else
            {
                take = Math.Clamp(limit ?? 100, 1, MaxPageSize);
                skip = Math.Max(0, offset ?? 0);
            }
            sort ??= "status";
        }
        // Sens par défaut : croissant pour les textes, l'état et la santé ; décroissant pour les compteurs et la date.
        var descending = dir is null ? sort is not (null or "name" or "model" or "os" or "status" or "health") : dir == "desc";
        return (new MachineQuery(S("q"), status is "all" ? null : status, groupId, withoutGroup, sort, descending, skip, take), null);
    }

    /// <summary>Cellule CSV (guillemets, neutralisation des formules : même règle que l'export du journal d'audit).</summary>
    private static string Cell(string? value) => AuditEndpoints.Cell(value);

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Date(DateTimeOffset? value) =>
        value is { } d ? d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
}

/// <summary>Réponse CSV écrite en continu (UTF-8 avec BOM, séparateur « ; » : ouverture directe dans Excel en français).</summary>
public sealed class CsvResult(string fileName, Func<TextWriter, Task> write) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024);
        await write(writer);
        await writer.FlushAsync(http.RequestAborted);
    }
}
