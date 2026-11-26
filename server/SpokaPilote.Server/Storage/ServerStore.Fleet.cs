using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;

namespace SpokaPilote.Server.Storage;

/// <summary>
/// Filtres, tri et pagination de la liste des postes (côté serveur). <paramref name="Limit"/> null : tous les postes.
/// <paramref name="Sort"/> null : ordre historique (à traiter d'abord, puis par nom).
/// </summary>
public sealed record MachineQuery(
    string? Search = null,
    string? Status = null,
    long? GroupId = null,
    bool WithoutGroup = false,
    string? Sort = null,
    bool Descending = false,
    int Offset = 0,
    int? Limit = null);

/// <summary>Page de la liste des postes : lignes demandées et nombre total de postes correspondant aux filtres.</summary>
public sealed record MachinePage(int Total, List<MachineRow> Rows);

/// <summary>Même classement que la collation NOCASE de SQLite (lettres ASCII sans distinction de casse, puis ordre des caractères).</summary>
public sealed class SqliteNoCaseComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var n = Math.Min(x.Length, y.Length);
        for (var i = 0; i < n; i++)
        {
            int a = x[i], b = y[i];
            if (a is >= 'A' and <= 'Z') a += 32;
            if (b is >= 'A' and <= 'Z') b += 32;
            if (a != b) return a - b;
        }
        return x.Length - y.Length;
    }
}

public sealed partial class ServerStore
{
    internal static readonly SqliteNoCaseComparer NoCase = new();

    /// <summary>Filtres d'état de la liste des postes (mêmes définitions que la console).</summary>
    public static readonly IReadOnlyList<string> StatusFilters =
        ["all", "todo", "healthy", "stale", "unresolved", "hardware", "failed", "blocked", "dangerous", "jobs"];

    /// <summary>Tris de la liste des postes.</summary>
    public static readonly IReadOnlyList<string> SortKeys =
        ["status", "name", "model", "os", "driverProblems", "unresolved", "proposed", "failed", "lastReport", "health"];

    private const string SummaryColumns = """
        m.id, m.name, m.machine_id, m.sys_machine_id, m.last_seen, m.agent_seen, m.last_ip, m.report_count, m.report_ts, m.last_report,
        m.manufacturer, m.model, m.friendly_model, m.display_model, m.serial, m.os_caption, m.os_display, m.os_version, m.architecture,
        m.app_version, m.device_count, m.driver_problems, m.hardware_faults, m.unresolved, m.proposed, m.blocked, m.installed, m.failed,
        m.source_errors, m.analysis_only, m.reboot_required, m.healthy, m.health_score, m.health_grade, m.health_critical,
        m.health_warnings, m.dangerous_driver
        """;

    /// <summary>Révision du parc : augmente à chaque changement d'un poste, d'un groupe ou d'une appartenance (triggers SQLite).</summary>
    public long FleetRevision()
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, "SELECT value FROM meta WHERE key = 'fleet_rev';") ?? 0L, CultureInfo.InvariantCulture);
    }

    private static MachineSummary ReadSummaryRow(SqliteDataReader r, IReadOnlyList<DeviceEntry>? missing = null)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var system = new SystemSnapshot
        {
            ComputerName = r.GetString(1),
            MachineId = S(3),
            Manufacturer = S(10),
            Model = S(11),
            FriendlyModel = S(12),
            SerialNumber = S(14),
            OsCaption = S(15),
            OsDisplayVersion = S(16),
            OsVersion = S(17),
            Architecture = S(18),
        };
        return new MachineSummary
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            MachineId = S(2) ?? S(3),
            LastSeen = ParseIso(r.GetString(4)),
            AgentSeen = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
            LastIp = S(6),
            ReportCount = r.GetInt32(7),
            LastReport = r.IsDBNull(8) ? ParseIso(r.GetString(9)) : ParseTimestamp(r.GetString(8)),
            System = system,
            DisplayModel = r.GetString(13),
            AppVersion = S(19),
            DeviceCount = r.GetInt32(20),
            DriverProblems = r.GetInt32(21),
            HardwareFaults = r.GetInt32(22),
            Unresolved = r.GetInt32(23),
            Proposed = r.GetInt32(24),
            Blocked = r.GetInt32(25),
            Installed = r.GetInt32(26),
            Failed = r.GetInt32(27),
            SourceErrors = r.GetInt32(28),
            AnalysisOnly = r.GetInt32(29) != 0,
            RebootRequired = r.GetInt32(30) != 0,
            Healthy = r.GetInt32(31) != 0,
            HealthScore = r.IsDBNull(32) ? null : r.GetInt32(32),
            HealthGrade = S(33),
            HealthCritical = r.GetInt32(34),
            HealthWarnings = r.GetInt32(35),
            DangerousDriver = r.GetInt32(36) != 0,
            Missing = missing ?? [],
        };
    }

    private const string GroupMembersOf = """
        (SELECT machine_ref FROM group_members WHERE group_id = $group UNION SELECT machine_ref FROM group_auto_members WHERE group_id = $group)
        """;

    /// <summary>
    /// Indicateurs de tous les postes (ou des membres d'un groupe), par nom ; <paramref name="withMissing"/> : avec leur
    /// matériel sans pilote (synthèse du parc). Aucun rapport n'est relu.
    /// </summary>
    public List<MachineSummary> GetMachineSummaries(long? groupId = null, bool withMissing = false)
    {
        using var c = Open();
        var missing = new Dictionary<long, List<DeviceEntry>>();
        if (withMissing)
        {
            using var mc = c.CreateCommand();
            mc.CommandText = $"""
                SELECT mm.machine_ref, mm.hw_key, mm.name, mm.error_code, mm.status_text, mm.hint, mm.only_blocked FROM machine_missing mm
                {(groupId is null ? "" : $"WHERE mm.machine_ref IN {GroupMembersOf}")}
                ORDER BY mm.machine_ref, mm.ord;
                """;
            if (groupId is not null) mc.Parameters.AddWithValue("$group", groupId);
            using var mr = mc.ExecuteReader();
            while (mr.Read())
            {
                if (!missing.TryGetValue(mr.GetInt64(0), out var list)) missing[mr.GetInt64(0)] = list = new List<DeviceEntry>();
                list.Add(new DeviceEntry
                {
                    HardwareIds = [mr.GetString(1)],
                    Name = mr.GetString(2),
                    ErrorCode = mr.GetInt32(3),
                    StatusText = mr.IsDBNull(4) ? null : mr.GetString(4),
                    Hint = mr.IsDBNull(5) ? null : mr.GetString(5),
                    OnlyBlockedCandidates = mr.GetInt32(6) != 0,
                });
            }
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns} FROM machines m {(groupId is null ? "" : $"WHERE m.id IN {GroupMembersOf}")};";
        if (groupId is not null) cmd.Parameters.AddWithValue("$group", groupId);
        using var r = cmd.ExecuteReader();
        var result = new List<MachineSummary>();
        while (r.Read()) result.Add(ReadSummaryRow(r, missing.GetValueOrDefault(r.GetInt64(0))));
        // Lecture dans l'ordre physique (séquentielle), puis tri par nom comme ORDER BY name (classement NOCASE de SQLite).
        result.Sort((a, b) => NoCase.Compare(a.Name, b.Name));
        return result;
    }

    public MachineSummary? GetMachineSummary(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns} FROM machines m WHERE m.name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSummaryRow(r) : null;
    }

    // ------------------------------------------------------------------ liste des postes (filtres, tri, pagination)

    private static string BuildWhere(SqliteCommand cmd, MachineQuery q, DateTimeOffset now, int staleDays)
    {
        var where = new List<string>();
        if (q.GroupId is { } g)
        {
            where.Add($"m.id IN {GroupMembersOf}");
            cmd.Parameters.AddWithValue("$group", g);
        }
        else if (q.WithoutGroup)
        {
            where.Add("NOT EXISTS (SELECT 1 FROM group_members gm WHERE gm.machine_ref = m.id) AND " +
                      "NOT EXISTS (SELECT 1 FROM group_auto_members ga WHERE ga.machine_ref = m.id)");
        }
        switch (q.Status)
        {
            case "todo": where.Add("m.healthy = 0"); break;
            case "healthy": where.Add("m.healthy = 1"); break;
            case "stale":
                if (staleDays <= 0) where.Add("0");
                else
                {
                    where.Add("m.last_seen < $staleCutoff");
                    cmd.Parameters.AddWithValue("$staleCutoff", Iso(now - TimeSpan.FromDays(staleDays)));
                }
                break;
            case "unresolved": where.Add("m.unresolved > 0"); break;
            case "hardware": where.Add("m.hardware_faults > 0"); break;
            case "failed": where.Add("m.failed > 0"); break;
            case "blocked": where.Add("m.blocked > 0"); break;
            case "dangerous": where.Add("m.dangerous_driver = 1"); break;
            case "jobs": where.Add("EXISTS (SELECT 1 FROM jobs j WHERE j.machine_ref = m.id AND j.state IN ('pending','running'))"); break;
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            where.Add("""
                (m.name LIKE $q ESCAPE '\' OR m.display_model LIKE $q ESCAPE '\' OR m.serial LIKE $q ESCAPE '\'
                 OR (COALESCE(m.os_caption, '') || ' ' || COALESCE(m.os_display, '')) LIKE $q ESCAPE '\')
                """);
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(q.Search.Trim()) + "%");
        }
        return where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
    }

    private static string OrderBy(string sort, bool descending)
    {
        string Dir(bool desc) => desc ? "DESC" : "ASC";
        var d = Dir(descending);
        var key = sort switch
        {
            "name" => $"m.name COLLATE NOCASE {d}",
            "model" => $"m.display_model COLLATE NOCASE {d}",
            "os" => $"(COALESCE(m.os_caption, '') || ' ' || COALESCE(m.os_display, '') || ' ' || COALESCE(m.os_version, '')) COLLATE NOCASE {d}",
            "driverProblems" => $"m.driver_problems {d}",
            "unresolved" => $"m.unresolved {d}",
            "proposed" => $"m.proposed {d}",
            "failed" => $"m.failed {d}",
            "lastReport" => $"m.last_report {d}",
            "health" => $"COALESCE(m.health_score, 101) {d}",
            // « État » : à traiter d'abord, les plus d'échecs puis de pilotes en erreur en tête (ordre croissant).
            _ => $"m.healthy {d}, m.failed {Dir(!descending)}, m.driver_problems {Dir(!descending)}",
        };
        return $"ORDER BY {key}, m.name COLLATE NOCASE {d}, m.id {d}";
    }

    /// <summary>
    /// Liste des postes filtrée, triée et paginée par SQLite (colonnes précalculées) ; seules les lignes de la page sont
    /// complétées (tâches actives, groupes). Sans tri ni pagination : ordre historique de l'API (à traiter d'abord).
    /// </summary>
    public MachinePage QueryMachines(MachineQuery q, DateTimeOffset now, int staleDays)
    {
        // Liste historique complète (tout le parc ou un groupe) : depuis l'index en mémoire, dernier contact des agents relu.
        if (q.Limit is null && q.Sort is null && string.IsNullOrWhiteSpace(q.Search) && q.Status is null && !q.WithoutGroup)
            return AllMachinesFromIndex(q.GroupId, now, staleDays);

        using var c = Open();
        List<MachineSummary> summaries;
        int total;
        using (var cmd = c.CreateCommand())
        {
            var where = BuildWhere(cmd, q, now, staleDays);
            if (q.Limit is { } limit)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM machines m {where};";
                total = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                cmd.CommandText = $"SELECT {SummaryColumns} FROM machines m {where} {OrderBy(q.Sort ?? "status", q.Descending)} LIMIT $limit OFFSET $offset;";
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", Math.Max(0, q.Offset));
            }
            else
            {
                cmd.CommandText = $"SELECT {SummaryColumns} FROM machines m {where} {(q.Sort is null ? "" : OrderBy(q.Sort, q.Descending))};";
                total = -1;
            }
            using var r = cmd.ExecuteReader();
            summaries = new List<MachineSummary>();
            while (r.Read()) summaries.Add(ReadSummaryRow(r));
        }
        if (total < 0) total = summaries.Count;

        var ids = summaries.Count <= 500 ? summaries.Select(m => m.Id).ToList() : null;   // grande liste : tout le parc d'un coup
        var active = ActiveJobCounts(c, ids);
        var membership = Membership(c, ids);
        var rows = summaries.Select(m =>
        {
            var row = FleetAggregator.ToRow(m, now, staleDays);
            row.ActiveJobs = active.GetValueOrDefault(m.Id);
            row.Groups = membership.GetValueOrDefault(m.Id) ?? new List<long>();
            return row;
        });
        return new MachinePage(total, q.Sort is null && q.Limit is null ? FleetAggregator.DefaultOrder(rows).ToList() : rows.ToList());
    }

    private MachinePage AllMachinesFromIndex(long? groupId, DateTimeOffset now, int staleDays)
    {
        IEnumerable<MachineSummary> list = CurrentSummaries();
        if (groupId is { } g)
        {
            var members = GroupMemberIds(g);
            list = list.Where(m => members.Contains(m.Id));
        }
        using var c = Open();
        var seen = new Dictionary<long, DateTimeOffset>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, agent_seen FROM machines WHERE agent_seen IS NOT NULL;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) seen[r.GetInt64(0)] = ParseIso(r.GetString(1));
        }
        var active = ActiveJobCounts(c, null);
        var membership = Membership(c, null);
        var rows = list.Select(m =>
        {
            var row = FleetAggregator.ToRow(m, now, staleDays);
            row.AgentSeen = seen.TryGetValue(m.Id, out var s) ? s : null;
            row.ActiveJobs = active.GetValueOrDefault(m.Id);
            row.Groups = membership.GetValueOrDefault(m.Id) ?? new List<long>();
            return row;
        }).ToList();
        return new MachinePage(rows.Count, FleetAggregator.DefaultOrder(rows).ToList());
    }

    /// <summary>Nombre de postes et de postes à traiter, de tout le parc ou d'un groupe.</summary>
    public (int Machines, int ToProcess, bool AnyHealth) CountMachines(long? groupId = null)
    {
        using var c = Open();
        var row = Row(c, $"""
            SELECT COUNT(*), COALESCE(SUM(healthy = 0), 0), EXISTS (SELECT 1 FROM machines WHERE health_score IS NOT NULL OR health_grade IS NOT NULL)
            FROM machines m {(groupId is null ? "" : $"WHERE m.id IN {GroupMembersOf}")};
            """, ("$group", groupId))!;
        return (Convert.ToInt32(row[0], CultureInfo.InvariantCulture), Convert.ToInt32(row[1], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[2], CultureInfo.InvariantCulture) != 0);
    }

    private static Dictionary<long, int> ActiveJobCounts(SqliteConnection c, IReadOnlyCollection<long>? ids)
    {
        var counts = new Dictionary<long, int>();
        if (ids is { Count: 0 }) return counts;
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT machine_ref, COUNT(*) FROM jobs WHERE state IN ('pending','running')
            {(ids is null ? "" : $"AND machine_ref IN ({string.Join(",", ids)})")} GROUP BY machine_ref;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt32(1);
        return counts;
    }

    /// <summary>Groupes de chaque poste (membres manuels et par règle), dans l'ordre des noms de groupe.</summary>
    private static Dictionary<long, List<long>> Membership(SqliteConnection c, IReadOnlyCollection<long>? ids)
    {
        var map = new Dictionary<long, List<long>>();
        if (ids is { Count: 0 }) return map;
        var filter = ids is null ? "" : $"WHERE machine_ref IN ({string.Join(",", ids)})";
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT x.machine_ref, x.group_id FROM
              (SELECT machine_ref, group_id FROM group_members {filter} UNION SELECT machine_ref, group_id FROM group_auto_members {filter}) x
            JOIN machine_groups g ON g.id = x.group_id ORDER BY g.name COLLATE NOCASE, g.id;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!map.TryGetValue(r.GetInt64(0), out var list)) map[r.GetInt64(0)] = list = new List<long>();
            list.Add(r.GetInt64(1));
        }
        return map;
    }

    /// <summary>Groupes d'un poste (identifiants, par nom de groupe).</summary>
    public List<long> GetMachineGroups(long machineRef)
    {
        using var c = Open();
        return Membership(c, [machineRef]).GetValueOrDefault(machineRef) ?? new List<long>();
    }

    /// <summary>Groupes d'un poste avec la nature de l'appartenance (ajouté à la main, désigné par les règles), par nom.</summary>
    public List<(long Id, string Name, bool Manual, bool ByRule)> GetMachineGroupDetails(long machineRef)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT g.id, g.name,
                   EXISTS (SELECT 1 FROM group_members gm WHERE gm.group_id = g.id AND gm.machine_ref = $m),
                   EXISTS (SELECT 1 FROM group_auto_members ga WHERE ga.group_id = g.id AND ga.machine_ref = $m)
            FROM machine_groups g
            WHERE g.id IN (SELECT group_id FROM group_members WHERE machine_ref = $m UNION SELECT group_id FROM group_auto_members WHERE machine_ref = $m)
            ORDER BY g.name COLLATE NOCASE, g.id;
            """;
        cmd.Parameters.AddWithValue("$m", machineRef);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, bool, bool)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3) != 0));
        return list;
    }

    public bool GroupExists(long groupId)
    {
        using var c = Open();
        return Scalar(c, "SELECT 1 FROM machine_groups WHERE id = $g;", ("$g", groupId)) is not null;
    }

    /// <summary>Lecture en continu des postes filtrés (export CSV : mémoire constante quel que soit le parc).</summary>
    public IEnumerable<(MachineSummary Machine, IReadOnlyList<string> Groups)> StreamMachines(MachineQuery q, DateTimeOffset now, int staleDays)
    {
        using var c = Open();
        var groupNames = new Dictionary<long, string>();
        using (var gc = c.CreateCommand())
        {
            gc.CommandText = "SELECT id, name FROM machine_groups;";
            using var gr = gc.ExecuteReader();
            while (gr.Read()) groupNames[gr.GetInt64(0)] = gr.GetString(1);
        }
        var membership = groupNames.Count == 0 ? new Dictionary<long, List<long>>() : Membership(c, null);
        using var cmd = c.CreateCommand();
        var where = BuildWhere(cmd, q, now, staleDays);
        cmd.CommandText = $"SELECT {SummaryColumns} FROM machines m {where} {OrderBy(q.Sort ?? "name", q.Descending)};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var m = ReadSummaryRow(r);
            var groups = membership.GetValueOrDefault(m.Id)?.Select(id => groupNames.GetValueOrDefault(id) ?? "").ToList() ?? [];
            yield return (m, groups);
        }
    }

    /// <summary>Postes visés par une action : tout le parc, les postes à traiter ou les membres d'un groupe (clé, nom).</summary>
    public List<(long Id, string Name)> GetMachineRefs(long? groupId = null, bool toProcessOnly = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (groupId is not null)
        {
            where.Add($"m.id IN {GroupMembersOf}");
            cmd.Parameters.AddWithValue("$group", groupId);
        }
        if (toProcessOnly) where.Add("m.healthy = 0");
        cmd.CommandText = $"SELECT m.id, m.name FROM machines m {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY m.name;";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>Postes désignés par leur nom (insensible à la casse) : nom demandé → (clé, nom enregistré).</summary>
    public Dictionary<string, (long Id, string Name)> FindMachinesByName(IEnumerable<string> names)
    {
        var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var c = Open();
        foreach (var chunk in list.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT id, name FROM machines WHERE name IN ({string.Join(",", chunk.Select((_, i) => "$n" + i))});";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$n" + i, chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(1)] = (r.GetInt64(0), r.GetString(1));
        }
        return result;
    }

    /// <summary>Dernier contact des agents, par nom de poste (tous, ou les noms indiqués).</summary>
    public Dictionary<string, DateTimeOffset?> GetAgentSeen(IReadOnlyCollection<string>? names = null)
    {
        var result = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        var chunks = names is null ? new List<string[]?> { null } : names.Chunk(400).Select(x => (string[]?)x).ToList();
        foreach (var chunk in chunks)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = chunk is null
                ? "SELECT name, agent_seen FROM machines;"
                : $"SELECT name, agent_seen FROM machines WHERE name IN ({string.Join(",", chunk.Select((_, i) => "$n" + i))});";
            if (chunk is not null) for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$n" + i, chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.IsDBNull(1) ? null : ParseIso(r.GetString(1));
        }
        return result;
    }

    // ------------------------------------------------------------------ synthèse du parc (mise en cache)

    private sealed record CachedFleet(long Revision, DateTimeOffset ComputedAt, int StaleDays, FleetSummary Summary);

    private readonly ConcurrentDictionary<long, CachedFleet> _fleetCache = new();
    private long _fleetCacheHits;
    private long _fleetCacheMisses;

    /// <summary>Statistiques du cache des synthèses (supervision).</summary>
    public (long Hits, long Misses) FleetCacheStats => (Interlocked.Read(ref _fleetCacheHits), Interlocked.Read(ref _fleetCacheMisses));

    /// <summary>Durée de validité d'une synthèse en l'absence de changement (le statut « non vu » dépend de l'heure).</summary>
    public static readonly TimeSpan FleetCacheMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Synthèse du parc ou d'un groupe (indicateurs, postes à traiter, matériel sans pilote, modèles). Calculée sur les
    /// indicateurs précalculés, puis gardée en cache tant que la révision du parc ne change pas (et 30 s au plus). Les
    /// lignes de la synthèse sont partagées : ne pas les modifier (voir FleetSummary.Copy).
    /// </summary>
    public FleetSummary GetFleetSummary(long? groupId, DateTimeOffset now, int staleDays)
    {
        var key = groupId ?? 0;
        bool Fresh(out FleetSummary result, long revision)
        {
            if (_fleetCache.TryGetValue(key, out var cached) && cached.Revision == revision && cached.StaleDays == staleDays &&
                (now - cached.ComputedAt).Duration() < FleetCacheMaxAge)
            {
                result = cached.Summary;
                return true;
            }
            result = null!;
            return false;
        }
        if (Fresh(out var hit, FleetRevision()))
        {
            Interlocked.Increment(ref _fleetCacheHits);
            return hit;
        }
        // Un seul calcul à la fois par synthèse : les requêtes simultanées attendent ce calcul au lieu d'en lancer d'autres.
        lock (_fleetLocks.GetOrAdd(key, _ => new object()))
        {
            var revision = FleetRevision();
            if (Fresh(out hit, revision))
            {
                Interlocked.Increment(ref _fleetCacheHits);
                return hit;
            }
            Interlocked.Increment(ref _fleetCacheMisses);
            var all = CurrentSummaries();
            IReadOnlyCollection<MachineSummary> machines = all;
            if (groupId is { } g)
            {
                var members = GroupMemberIds(g);
                machines = all.Where(m => members.Contains(m.Id)).ToList();
            }
            var summary = FleetAggregator.SummarizeFleet(machines, now, staleDays);
            if (_fleetCache.Count > 1000) _fleetCache.Clear();
            _fleetCache[key] = new CachedFleet(revision, now, staleDays, summary);
            return summary;
        }
    }

    private readonly ConcurrentDictionary<long, object> _fleetLocks = new();

    /// <summary>Membres d'un groupe (manuels et par règle).</summary>
    private HashSet<long> GroupMemberIds(long groupId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT machine_ref FROM {GroupMembersOf} x;";
        cmd.Parameters.AddWithValue("$group", groupId);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    // ------------------------------------------------------------------ index des indicateurs en mémoire (incrémental)

    /// <summary>
    /// Indicateurs de tous les postes gardés en mémoire (~600 octets par poste) et rafraîchis de façon incrémentale :
    /// seuls les postes dont la révision (colonne rev, tenue par les triggers) a changé depuis le dernier passage sont
    /// relus. Lire les 37 colonnes de 20 000 postes coûte ~200 ms ; après un rapport, la mise à jour n'en relit qu'un.
    /// Une suppression est détectée par le nombre de postes (rechargement complet, rare).
    /// </summary>
    private sealed class SummaryIndex
    {
        public long Revision;
        public long MaxRowRevision;
        public readonly Dictionary<long, MachineSummary> ById = new();
        public List<MachineSummary>? Sorted;
    }

    private readonly Lock _indexLock = new();
    private SummaryIndex? _index;
    private long _indexFullLoads;
    private long _indexIncrementalRows;

    /// <summary>Rechargements complets de l'index et postes relus de façon incrémentale (supervision, tests).</summary>
    public (long FullLoads, long IncrementalRows) SummaryIndexStats => (Interlocked.Read(ref _indexFullLoads), Interlocked.Read(ref _indexIncrementalRows));

    /// <summary>Indicateurs (avec le matériel sans pilote) de tous les postes, triés par nom ; liste partagée : ne pas modifier.</summary>
    internal List<MachineSummary> CurrentSummaries()
    {
        lock (_indexLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction(deferred: true);   // un seul instantané pour la révision, les postes et leur matériel
            var revision = Convert.ToInt64(Scalar(c, "SELECT value FROM meta WHERE key = 'fleet_rev';") ?? 0L, CultureInfo.InvariantCulture);
            if (_index is { Sorted: not null } current && current.Revision == revision) return current.Sorted;

            var index = _index;
            if (index is not null)
            {
                var changed = new List<MachineSummary>();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"SELECT {SummaryColumns}, m.rev FROM machines m WHERE m.rev > $rev;";
                    cmd.Parameters.AddWithValue("$rev", index.MaxRowRevision);
                    using var r = cmd.ExecuteReader();
                    while (r.Read() && changed.Count <= 5000)
                    {
                        changed.Add(ReadSummaryRow(r));
                        index.MaxRowRevision = Math.Max(index.MaxRowRevision, r.GetInt64(37));
                    }
                }
                var count = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM machines;"), CultureInfo.InvariantCulture);
                if (changed.Count > 5000) index = null;   // beaucoup de changements : rechargement complet
                else
                {
                    var missing = LoadMissing(c, changed.Select(m => m.Id).ToList());
                    foreach (var m in changed) index.ById[m.Id] = WithMissing(m, missing.GetValueOrDefault(m.Id));
                    Interlocked.Add(ref _indexIncrementalRows, changed.Count);
                    if (index.ById.Count != count) index = null;   // un poste a été supprimé
                }
            }
            if (index is null)
            {
                index = new SummaryIndex();
                var missing = LoadMissing(c, null);
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT {SummaryColumns}, m.rev FROM machines m;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var m = ReadSummaryRow(r, missing.GetValueOrDefault(r.GetInt64(0)));
                    index.ById[m.Id] = m;
                    index.MaxRowRevision = Math.Max(index.MaxRowRevision, r.GetInt64(37));
                }
                Interlocked.Increment(ref _indexFullLoads);
            }
            index.Revision = revision;
            var sorted = index.ById.Values.ToList();
            sorted.Sort((a, b) => NoCase.Compare(a.Name, b.Name));
            index.Sorted = sorted;
            _index = index;
            return sorted;
        }
    }

    private static MachineSummary WithMissing(MachineSummary m, IReadOnlyList<DeviceEntry>? missing) =>
        missing is null || missing.Count == 0 ? m : new MachineSummary
        {
            Id = m.Id, Name = m.Name, MachineId = m.MachineId, LastSeen = m.LastSeen, AgentSeen = m.AgentSeen, LastIp = m.LastIp,
            ReportCount = m.ReportCount, System = m.System, DisplayModel = m.DisplayModel, LastReport = m.LastReport, AppVersion = m.AppVersion,
            DeviceCount = m.DeviceCount, DriverProblems = m.DriverProblems, HardwareFaults = m.HardwareFaults, Unresolved = m.Unresolved,
            Proposed = m.Proposed, Blocked = m.Blocked, Installed = m.Installed, Failed = m.Failed, SourceErrors = m.SourceErrors,
            AnalysisOnly = m.AnalysisOnly, RebootRequired = m.RebootRequired, Healthy = m.Healthy, HealthScore = m.HealthScore,
            HealthGrade = m.HealthGrade, HealthCritical = m.HealthCritical, HealthWarnings = m.HealthWarnings, DangerousDriver = m.DangerousDriver,
            Missing = missing,
        };

    /// <summary>Matériel sans pilote par poste (tous, ou les postes indiqués), dans l'ordre du rapport.</summary>
    private static Dictionary<long, List<DeviceEntry>> LoadMissing(SqliteConnection c, IReadOnlyCollection<long>? ids)
    {
        var missing = new Dictionary<long, List<DeviceEntry>>();
        if (ids is { Count: 0 }) return missing;
        using var mc = c.CreateCommand();
        mc.CommandText = $"""
            SELECT mm.machine_ref, mm.hw_key, mm.name, mm.error_code, mm.status_text, mm.hint, mm.only_blocked FROM machine_missing mm
            {(ids is null ? "" : $"WHERE mm.machine_ref IN ({string.Join(",", ids)})")}
            ORDER BY mm.machine_ref, mm.ord;
            """;
        using var mr = mc.ExecuteReader();
        while (mr.Read())
        {
            if (!missing.TryGetValue(mr.GetInt64(0), out var list)) missing[mr.GetInt64(0)] = list = new List<DeviceEntry>();
            list.Add(new DeviceEntry
            {
                HardwareIds = [mr.GetString(1)],
                Name = mr.GetString(2),
                ErrorCode = mr.GetInt32(3),
                StatusText = mr.IsDBNull(4) ? null : mr.GetString(4),
                Hint = mr.IsDBNull(5) ? null : mr.GetString(5),
                OnlyBlockedCandidates = mr.GetInt32(6) != 0,
            });
        }
        return missing;
    }

    // ------------------------------------------------------------------ groupes : appartenance par règle (matérialisée)

    /// <summary>Groupes qui ont des règles automatiques (les autres n'ont que des membres manuels).</summary>
    private static List<(long Id, GroupRules Rules)> LoadRuleGroups(SqliteConnection c)
    {
        var list = new List<(long, GroupRules)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, rules_json FROM machine_groups;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            GroupRules? rules;
            try { rules = JsonSerializer.Deserialize<GroupRules>(r.GetString(1), Json); }
            catch (JsonException) { rules = null; }
            if (rules is { IsEmpty: false }) list.Add((r.GetInt64(0), rules));
        }
        return list;
    }

    private const string SubjectColumns = "id, name, last_ip, manufacturer, model, friendly_model, os_caption, os_display, os_version";

    private static (long Id, string Name, string? Ip, SystemSnapshot System) ReadSubject(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return (r.GetInt64(0), r.GetString(1), S(2), new SystemSnapshot
        {
            ComputerName = r.GetString(1), Manufacturer = S(3), Model = S(4), FriendlyModel = S(5), OsCaption = S(6), OsDisplayVersion = S(7),
            OsVersion = S(8),
        });
    }

    /// <summary>Recalcule les groupes par règle d'un poste (après un rapport, un renommage ou un changement d'adresse).</summary>
    private static void RefreshAutoMembership(SqliteConnection c, long machineRef, List<(long Id, GroupRules Rules)> groups)
    {
        var current = new HashSet<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT group_id FROM group_auto_members WHERE machine_ref = $m;";
            cmd.Parameters.AddWithValue("$m", machineRef);
            using var r = cmd.ExecuteReader();
            while (r.Read()) current.Add(r.GetInt64(0));
        }
        if (groups.Count == 0 && current.Count == 0) return;
        (long Id, string Name, string? Ip, SystemSnapshot System) subject;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {SubjectColumns} FROM machines WHERE id = $m;";
            cmd.Parameters.AddWithValue("$m", machineRef);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return;
            subject = ReadSubject(r);
        }
        var desired = groups.Where(g => GroupMatcher.Matches(g.Rules, subject.Name, subject.Ip, subject.System)).Select(g => g.Id).ToHashSet();
        foreach (var g in current.Where(g => !desired.Contains(g)))
            Execute(c, "DELETE FROM group_auto_members WHERE group_id = $g AND machine_ref = $m;", ("$g", g), ("$m", machineRef));
        foreach (var g in desired.Where(g => !current.Contains(g)))
            Execute(c, "INSERT OR IGNORE INTO group_auto_members(group_id, machine_ref) VALUES($g, $m);", ("$g", g), ("$m", machineRef));
    }

    /// <summary>Recalcule tous les membres par règle d'un groupe (création ou modification de ses règles).</summary>
    private static void RebuildAutoMembers(SqliteConnection c, long groupId, GroupRules rules)
    {
        var current = new HashSet<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT machine_ref FROM group_auto_members WHERE group_id = $g;";
            cmd.Parameters.AddWithValue("$g", groupId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) current.Add(r.GetInt64(0));
        }
        var desired = new HashSet<long>();
        if (!rules.IsEmpty)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SubjectColumns} FROM machines;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = ReadSubject(r);
                if (GroupMatcher.Matches(rules, s.Name, s.Ip, s.System)) desired.Add(s.Id);
            }
        }
        using var delete = c.CreateCommand();
        delete.CommandText = "DELETE FROM group_auto_members WHERE group_id = $g AND machine_ref = $m;";
        delete.Parameters.AddWithValue("$g", groupId);
        var dm = delete.Parameters.Add("$m", SqliteType.Integer);
        foreach (var m in current.Where(m => !desired.Contains(m)))
        {
            dm.Value = m;
            delete.ExecuteNonQuery();
        }
        using var insert = c.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO group_auto_members(group_id, machine_ref) VALUES($g, $m);";
        insert.Parameters.AddWithValue("$g", groupId);
        var im = insert.Parameters.Add("$m", SqliteType.Integer);
        foreach (var m in desired.Where(m => !current.Contains(m)))
        {
            im.Value = m;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Postes désignés par des règles (aperçu avant enregistrement d'un groupe), par nom.</summary>
    public List<string> PreviewRules(GroupRules rules)
    {
        var names = new List<string>();
        if (rules.IsEmpty) return names;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SubjectColumns} FROM machines ORDER BY name;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var s = ReadSubject(r);
            if (GroupMatcher.Matches(rules, s.Name, s.Ip, s.System)) names.Add(s.Name);
        }
        return names;
    }

    /// <summary>Nombre de membres (manuels ou par règle) et de membres manuels de chaque groupe.</summary>
    public Dictionary<long, (int Members, int Manual)> GetGroupCounts()
    {
        using var c = Open();
        var result = new Dictionary<long, (int, int)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT g.id,
                  (SELECT COUNT(*) FROM (SELECT machine_ref FROM group_members WHERE group_id = g.id
                                         UNION SELECT machine_ref FROM group_auto_members WHERE group_id = g.id)),
                  (SELECT COUNT(*) FROM group_members WHERE group_id = g.id)
                FROM machine_groups g;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetInt64(0)] = (r.GetInt32(1), r.GetInt32(2));
        }
        return result;
    }

    /// <summary>Membres d'un groupe (indicateurs, manuel / par règle), par nom ; pagination facultative.</summary>
    public (int Total, int ToProcess, List<(MachineSummary Machine, bool Manual, bool ByRule)> Members) GetGroupMembers(long groupId, int offset = 0,
        int? limit = null)
    {
        using var c = Open();
        var counts = Row(c, $"SELECT COUNT(*), COALESCE(SUM(healthy = 0), 0) FROM machines m WHERE m.id IN {GroupMembersOf};", ("$group", groupId))!;
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SummaryColumns},
                   EXISTS (SELECT 1 FROM group_members gm WHERE gm.group_id = $group AND gm.machine_ref = m.id),
                   EXISTS (SELECT 1 FROM group_auto_members ga WHERE ga.group_id = $group AND ga.machine_ref = m.id)
            FROM machines m WHERE m.id IN {GroupMembersOf} ORDER BY m.name COLLATE NOCASE
            {(limit is null ? "" : "LIMIT $limit OFFSET $offset")};
            """;
        cmd.Parameters.AddWithValue("$group", groupId);
        if (limit is not null)
        {
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        }
        using var r = cmd.ExecuteReader();
        var members = new List<(MachineSummary, bool, bool)>();
        while (r.Read()) members.Add((ReadSummaryRow(r), r.GetInt64(37) != 0, r.GetInt64(38) != 0));
        return (Convert.ToInt32(counts[0], CultureInfo.InvariantCulture), Convert.ToInt32(counts[1], CultureInfo.InvariantCulture), members);
    }
}
