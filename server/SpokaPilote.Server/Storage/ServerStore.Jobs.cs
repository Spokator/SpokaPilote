using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Jobs;

namespace SpokaPilote.Server.Storage;

/// <summary>Tâche à distance (file d'attente d'un poste).</summary>
public sealed record JobRecord(
    long Id,
    long MachineRef,
    string Machine,
    string Type,
    JobParameters Parameters,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    int? ExitCode,
    long? ReportId,
    string? Log,
    DateTimeOffset? NotAfter = null,
    long? ScheduleId = null);

public enum JobCompletion { Completed, NotFound, NotRunning }

/// <summary>Compteurs de la file de tâches pour la vue d'ensemble.</summary>
public sealed record JobStats(int Pending, int Running, int FailedLast24h);

/// <summary>Poste qui interroge sa file de tâches (clé interne et nom).</summary>
public sealed record AgentIdentity(long Id, string Name);

/// <summary>Création d'une tâche pour un poste d'une demande groupée.</summary>
/// <param name="JobId">Tâche créée, ou tâche identique déjà en attente (Created = false).</param>
/// <param name="Job">Tâche créée (null si une tâche identique était déjà en attente).</param>
public sealed record JobCreation(long MachineRef, string Machine, long JobId, bool Created, JobRecord? Job);

public sealed partial class ServerStore
{
    /// <summary>Taille maximale du journal d'exécution conservé pour une tâche.</summary>
    public const int MaxJobLogChars = 64 * 1024;

    /// <summary>Un agent qui interroge plus souvent n'écrit son « dernier contact » qu'une fois par minute.</summary>
    public static readonly TimeSpan AgentSeenResolution = TimeSpan.FromMinutes(1);

    private const string JobColumns = """
        j.id, j.machine_ref, m.name, j.type, j.parameters, j.created_at, j.created_by, j.state, j.started_at, j.finished_at,
        j.message, j.exit_code, j.report_id, j.not_after, j.schedule_id
        """;

    /// <summary>
    /// Ajoute une tâche à la file du poste. Une tâche identique (même type, mêmes paramètres) déjà en attente n'est pas
    /// dupliquée : elle est renvoyée avec Created = false.
    /// </summary>
    public (JobRecord Job, bool Created) CreateJob(long machineRef, string type, JobParameters parameters, string createdBy, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(parameters, Json);
        using var c = Open();
        using var tx = BeginWrite(c);
        if (Scalar(c, "SELECT id FROM jobs WHERE machine_ref = $m AND type = $t AND parameters = $p AND state = 'pending' ORDER BY id LIMIT 1;",
                ("$m", machineRef), ("$t", type), ("$p", json)) is long existing)
        {
            tx.Commit();
            return (GetJob(c, existing, withLog: false)!, false);
        }
        var id = Convert.ToInt64(Scalar(c, """
            INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state) VALUES($m, $t, $p, $at, $by, 'pending') RETURNING id;
            """, ("$m", machineRef), ("$t", type), ("$p", json), ("$at", Iso(now)), ("$by", createdBy)), CultureInfo.InvariantCulture);
        tx.Commit();
        return (GetJob(c, id, withLog: false)!, true);
    }

    /// <summary>
    /// Demande groupée (sélection, postes à traiter, tout le parc, groupe) : même règle que CreateJob (pas de doublon d'une
    /// tâche identique en attente), par lots de 500 postes par transaction : le verrou d'écriture reste bref même sur
    /// 20 000 postes, et les envois de rapports continuent pendant ce temps.
    /// </summary>
    public List<JobCreation> CreateJobs(IReadOnlyList<(long Id, string Name)> machines, string type, JobParameters parameters, string createdBy,
        DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(parameters, Json);
        var createdAt = ParseIso(Iso(now));
        var result = new List<JobCreation>(machines.Count);
        using var c = Open();
        foreach (var chunk in machines.Chunk(500))
        {
            using var tx = BeginWrite(c);
            using var find = c.CreateCommand();
            find.CommandText = "SELECT id FROM jobs WHERE machine_ref = $m AND type = $t AND parameters = $p AND state = 'pending' ORDER BY id LIMIT 1;";
            var findMachine = find.Parameters.Add("$m", SqliteType.Integer);
            find.Parameters.AddWithValue("$t", type);
            find.Parameters.AddWithValue("$p", json);
            using var insert = c.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state) VALUES($m, $t, $p, $at, $by, 'pending') RETURNING id;
                """;
            var insertMachine = insert.Parameters.Add("$m", SqliteType.Integer);
            insert.Parameters.AddWithValue("$t", type);
            insert.Parameters.AddWithValue("$p", json);
            insert.Parameters.AddWithValue("$at", Iso(now));
            insert.Parameters.AddWithValue("$by", createdBy);
            foreach (var (machineRef, name) in chunk)
            {
                findMachine.Value = machineRef;
                if (find.ExecuteScalar() is long existing)
                {
                    result.Add(new JobCreation(machineRef, name, existing, false, null));
                    continue;
                }
                insertMachine.Value = machineRef;
                var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                result.Add(new JobCreation(machineRef, name, id, true,
                    new JobRecord(id, machineRef, name, type, parameters, createdAt, createdBy, JobStates.Pending, null, null, null, null, null, null)));
            }
            tx.Commit();
        }
        return result;
    }

    /// <summary>
    /// Planificateur : ajoute une tâche sauf si une tâche du même type est déjà en attente pour ce poste (quels que soient
    /// ses paramètres). <paramref name="notAfter"/> : fin de la fenêtre de maintenance — la tâche n'est plus remise au
    /// poste après cette heure et expire.
    /// </summary>
    public (JobRecord Job, bool Created) EnqueueScheduledJob(long machineRef, string type, JobParameters parameters, string createdBy,
        DateTimeOffset now, DateTimeOffset? notAfter, long scheduleId)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        if (Scalar(c, "SELECT id FROM jobs WHERE machine_ref = $m AND type = $t AND state = 'pending' ORDER BY id LIMIT 1;",
                ("$m", machineRef), ("$t", type)) is long existing)
        {
            tx.Commit();
            return (GetJob(c, existing, withLog: false)!, false);
        }
        var id = Convert.ToInt64(Scalar(c, """
            INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state, not_after, schedule_id)
            VALUES($m, $t, $p, $at, $by, 'pending', $na, $sid) RETURNING id;
            """, ("$m", machineRef), ("$t", type), ("$p", JsonSerializer.Serialize(parameters, Json)), ("$at", Iso(now)), ("$by", createdBy),
            ("$na", notAfter is { } n ? Iso(n) : null), ("$sid", scheduleId)), CultureInfo.InvariantCulture);
        tx.Commit();
        return (GetJob(c, id, withLog: false)!, true);
    }

    /// <summary>Planificateur, version groupée d'EnqueueScheduledJob (lots de 500 postes par transaction). Renvoie (créées, ignorées).</summary>
    public (int Created, int Skipped) EnqueueScheduledJobs(IReadOnlyList<long> machineRefs, string type, JobParameters parameters, string createdBy,
        DateTimeOffset now, DateTimeOffset? notAfter, long scheduleId)
    {
        int created = 0, skipped = 0;
        var json = JsonSerializer.Serialize(parameters, Json);
        using var c = Open();
        foreach (var chunk in machineRefs.Chunk(500))
        {
            using var tx = BeginWrite(c);
            using var find = c.CreateCommand();
            find.CommandText = "SELECT 1 FROM jobs WHERE machine_ref = $m AND type = $t AND state = 'pending' LIMIT 1;";
            var findMachine = find.Parameters.Add("$m", SqliteType.Integer);
            find.Parameters.AddWithValue("$t", type);
            using var insert = c.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state, not_after, schedule_id)
                VALUES($m, $t, $p, $at, $by, 'pending', $na, $sid);
                """;
            var insertMachine = insert.Parameters.Add("$m", SqliteType.Integer);
            insert.Parameters.AddWithValue("$t", type);
            insert.Parameters.AddWithValue("$p", json);
            insert.Parameters.AddWithValue("$at", Iso(now));
            insert.Parameters.AddWithValue("$by", createdBy);
            insert.Parameters.AddWithValue("$na", notAfter is { } n ? Iso(n) : DBNull.Value);
            insert.Parameters.AddWithValue("$sid", scheduleId);
            foreach (var machineRef in chunk)
            {
                findMachine.Value = machineRef;
                if (find.ExecuteScalar() is not null)
                {
                    skipped++;
                    continue;
                }
                insertMachine.Value = machineRef;
                insert.ExecuteNonQuery();
                created++;
            }
            tx.Commit();
        }
        return (created, skipped);
    }

    public JobRecord? GetJob(long id, bool withLog = true)
    {
        using var c = Open();
        return GetJob(c, id, withLog);
    }

    private static JobRecord? GetJob(SqliteConnection c, long id, bool withLog)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns}, {(withLog ? "j.log" : "NULL")} FROM jobs j JOIN machines m ON m.id = j.machine_ref WHERE j.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>Tâches les plus récentes d'abord, filtrées par état(s) et/ou par poste (le journal n'est pas inclus).</summary>
    public List<JobRecord> ListJobs(string[]? states = null, string? machine = null, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (states is not null)
        {
            if (states.Length == 0) return new List<JobRecord>();
            where.Add($"j.state IN ({string.Join(", ", states.Select((_, i) => "$s" + i))})");
            for (var i = 0; i < states.Length; i++) cmd.Parameters.AddWithValue("$s" + i, states[i]);
        }
        if (!string.IsNullOrWhiteSpace(machine))
        {
            // Par la clé du poste (index ix_jobs_machine) plutôt que par son nom après jointure.
            where.Add("j.machine_ref = (SELECT id FROM machines WHERE name = $n)");
            cmd.Parameters.AddWithValue("$n", machine.Trim());
        }
        cmd.CommandText = $"""
            SELECT {JobColumns}, NULL FROM jobs j JOIN machines m ON m.id = j.machine_ref
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY j.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = cmd.ExecuteReader();
        var list = new List<JobRecord>();
        while (reader.Read()) list.Add(ReadJob(reader));
        return list;
    }

    /// <summary>Annule une tâche encore en attente (une tâche en cours s'exécute déjà sur le poste).</summary>
    public bool CancelJob(long id, DateTimeOffset now, string by)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var cancelled = Execute(c, """
            UPDATE jobs SET state = 'cancelled', finished_at = $now, message = $msg WHERE id = $id AND state = 'pending';
            """, ("$id", id), ("$now", Iso(now)), ("$msg", $"Annulée depuis la console ({by}).")) > 0;
        tx.Commit();
        return cancelled;
    }

    /// <summary>
    /// Agent : renvoie les tâches en attente du poste (les plus anciennes d'abord) et les passe « en cours », en une
    /// transaction : deux interrogations simultanées ne reçoivent jamais la même tâche. Sans tâche à remettre (cas de
    /// loin le plus fréquent), aucune écriture n'est faite.
    /// </summary>
    /// <param name="pendingDays">Délai d'expiration des tâches en attente : une tâche plus ancienne n'est plus remise.</param>
    public List<JobRecord> ClaimPendingJobs(long machineRef, DateTimeOffset now, int max = 10, int? pendingDays = null)
    {
        const string Select = """
            SELECT id FROM jobs WHERE machine_ref = $m AND state = 'pending' AND (not_after IS NULL OR not_after > $now)
              AND ($cutoff IS NULL OR created_at >= $cutoff)
            ORDER BY id LIMIT $max;
            """;
        (string, object?)[] parameters =
            [("$m", machineRef), ("$now", Iso(now)), ("$max", max), ("$cutoff", pendingDays is { } d ? Iso(now.AddDays(-d)) : null)];
        using var c = Open();
        if (Scalar(c, Select, parameters) is null) return new List<JobRecord>();

        using var tx = BeginWrite(c);
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            // Une tâche dont la fenêtre de maintenance est terminée n'est plus remise (elle expire).
            cmd.CommandText = Select;
            foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        foreach (var id in ids)
            Execute(c, "UPDATE jobs SET state = 'running', started_at = $now WHERE id = $id;", ("$id", id), ("$now", Iso(now)));
        tx.Commit();
        return ids.Select(id => GetJob(c, id, withLog: false)!).ToList();
    }

    /// <summary>
    /// Agent : enregistre le résultat d'une tâche « en cours ». Une tâche d'un autre poste est traitée comme inexistante
    /// (aucune information divulguée) ; une tâche annulée ou expirée entre-temps n'est pas modifiée.
    /// </summary>
    public (JobCompletion Status, JobRecord? Job) CompleteJob(long id, long machineRef, bool success, int? exitCode, string? message,
        long? reportId, string? log, DateTimeOffset now)
    {
        if (log is { Length: > MaxJobLogChars }) log = "[…]\n" + log[^MaxJobLogChars..];
        using var c = Open();
        using var tx = BeginWrite(c);
        var row = Row(c, "SELECT state FROM jobs WHERE id = $id AND machine_ref = $m;", ("$id", id), ("$m", machineRef));
        if (row is null) return (JobCompletion.NotFound, null);
        if ((string)row[0]! != JobStates.Running)
        {
            tx.Commit();
            return (JobCompletion.NotRunning, GetJob(c, id, withLog: false));
        }
        Execute(c, """
            UPDATE jobs SET state = $state, finished_at = $now, exit_code = $code, message = $msg, report_id = $report, log = $log WHERE id = $id;
            """, ("$id", id), ("$state", success ? JobStates.Succeeded : JobStates.Failed), ("$now", Iso(now)), ("$code", exitCode),
            ("$msg", message), ("$report", reportId), ("$log", log));
        tx.Commit();
        return (JobCompletion.Completed, GetJob(c, id, withLog: false));
    }

    /// <summary>
    /// Expire les tâches jamais récupérées (poste éteint, agent non activé) et les tâches « en cours » restées sans
    /// résultat (poste redémarré ou arrêté pendant l'exécution). Renvoie les tâches expirées par cet appel. La recherche
    /// passe par les index (état et date) ; rien n'est écrit s'il n'y a rien à expirer. <paramref name="machineRef"/> :
    /// seulement les tâches de ce poste.
    /// </summary>
    public List<JobRecord> ExpireJobs(DateTimeOffset now, int pendingDays, int runningHours, long? machineRef = null)
    {
        var machineFilter = machineRef is null ? "" : " AND machine_ref = $mref";
        var select = $"""
            SELECT id, state, not_after IS NOT NULL AND not_after <= $now FROM jobs WHERE state = 'pending' AND created_at < $p{machineFilter}
            UNION SELECT id, state, 1 FROM jobs WHERE state = 'pending' AND not_after IS NOT NULL AND not_after <= $now{machineFilter}
            UNION SELECT id, state, 0 FROM jobs WHERE state = 'running' AND started_at < $r{machineFilter};
            """;
        (string, object?)[] parameters =
        [
            ("$p", Iso(now.AddDays(-pendingDays))), ("$r", Iso(now.AddHours(-runningHours))), ("$now", Iso(now)), ("$mref", machineRef),
        ];
        List<(long Id, string Message)> Find(SqliteConnection c)
        {
            var found = new Dictionary<long, string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = select;
            foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var windowOver = reader.GetInt64(2) != 0;
                if (found.TryGetValue(id, out var known) && !windowOver) continue;
                found[id] = reader.GetString(1) == JobStates.Running
                    ? $"Aucun résultat reçu {runningHours} h après le démarrage : poste éteint ou redémarré pendant l'exécution ?"
                    : windowOver
                        ? "Fenêtre de maintenance terminée avant que le poste ne récupère la tâche (poste éteint ou agent non activé)."
                        : $"Non récupérée par le poste en {pendingDays} jour(s) : agent non activé (spokapilote agent enable) ou poste éteint.";
            }
            return found.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        using var c = Open();
        if (Find(c).Count == 0) return new List<JobRecord>();
        using var tx = BeginWrite(c);
        var expired = Find(c);
        foreach (var (id, message) in expired)
            Execute(c, "UPDATE jobs SET finished_at = $now, message = $msg, state = 'expired' WHERE id = $id;",
                ("$id", id), ("$now", Iso(now)), ("$msg", message));
        tx.Commit();
        return expired.Select(e => GetJob(c, e.Id, withLog: false)!).ToList();
    }

    /// <summary>
    /// Supprime les tâches terminées depuis plus de <paramref name="days"/> jours, par lots (le verrou d'écriture n'est
    /// jamais tenu longtemps, même pour des centaines de milliers de tâches).
    /// </summary>
    public int PurgeFinishedJobs(DateTimeOffset now, int days = 90, int batch = 2000)
    {
        var total = 0;
        using var c = Open();
        while (true)
        {
            using var tx = BeginWrite(c);
            var deleted = Execute(c, """
                DELETE FROM jobs WHERE id IN (SELECT id FROM jobs WHERE state IN ('succeeded','failed','expired','cancelled') AND finished_at < $t LIMIT $n);
                """, ("$t", Iso(now.AddDays(-days))), ("$n", batch));
            tx.Commit();
            total += deleted;
            if (deleted < batch) return total;
        }
    }

    /// <summary>Compteurs des tâches, de tout le parc ou d'un groupe (requêtes sur index).</summary>
    public JobStats GetJobStats(DateTimeOffset now, long? groupId = null)
    {
        using var c = Open();
        var filter = groupId is null ? "" : $" AND machine_ref IN {GroupMembersOf}";
        var row = Row(c, $"""
            SELECT (SELECT COUNT(*) FROM jobs WHERE state = 'pending'{filter}),
                   (SELECT COUNT(*) FROM jobs WHERE state = 'running'{filter}),
                   (SELECT COUNT(*) FROM jobs WHERE state = 'failed' AND finished_at >= $since{filter});
            """, ("$since", Iso(now.AddDays(-1))), ("$group", groupId))!;
        static int I(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        return new JobStats(I(row[0]), I(row[1]), I(row[2]));
    }

    /// <summary>Compteurs des tâches des seuls postes indiqués.</summary>
    public JobStats GetJobStats(DateTimeOffset now, IReadOnlyCollection<long>? machineRefs)
    {
        if (machineRefs is null) return GetJobStats(now);
        using var c = Open();
        var filter = $" AND machine_ref IN ({(machineRefs.Count == 0 ? "NULL" : string.Join(",", machineRefs))})";
        var row = Row(c, $"""
            SELECT (SELECT COUNT(*) FROM jobs WHERE state = 'pending'{filter}),
                   (SELECT COUNT(*) FROM jobs WHERE state = 'running'{filter}),
                   (SELECT COUNT(*) FROM jobs WHERE state = 'failed' AND finished_at >= $since{filter});
            """, ("$since", Iso(now.AddDays(-1))))!;
        static int I(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        return new JobStats(I(row[0]), I(row[1]), I(row[2]));
    }

    /// <summary>Nombre de tâches en attente ou en cours, par poste.</summary>
    public Dictionary<long, int> GetActiveJobCounts()
    {
        using var c = Open();
        return ActiveJobCounts(c, null);
    }

    /// <summary>
    /// Agent : retrouve le poste qui interroge sa file. Il est reconnu par son identifiant machine (MachineGuid) ; un poste
    /// connu par son seul nom (rapports antérieurs à la 3.0, sans identifiant) est rattaché à ce premier contact.
    /// Enregistre l'heure du contact (visible dans la console), au plus une fois par minute : une interrogation ordinaire
    /// ne fait alors qu'une lecture. Null : poste inconnu (aucun rapport reçu).
    /// </summary>
    public AgentIdentity? FindMachineForAgent(string machineId, string? name, DateTimeOffset now)
    {
        using var c = Open();
        if (Row(c, "SELECT id, name, agent_seen FROM machines WHERE machine_id = $mid;", ("$mid", machineId)) is { } known &&
            known[2] is string seen && now - ParseIso(seen) < AgentSeenResolution && now >= ParseIso(seen))
            return new AgentIdentity((long)known[0]!, (string)known[1]!);

        using var tx = BeginWrite(c);
        var id = Scalar(c, "SELECT id FROM machines WHERE machine_id = $mid;", ("$mid", machineId)) as long?;
        if (id is null && !string.IsNullOrWhiteSpace(name) &&
            Scalar(c, "SELECT id FROM machines WHERE name = $n AND machine_id IS NULL;", ("$n", name.Trim())) is long legacy)
        {
            Execute(c, "UPDATE machines SET machine_id = $mid WHERE id = $id;", ("$mid", machineId), ("$id", legacy));
            id = legacy;
        }
        string? machineName = null;
        if (id is not null)
        {
            Execute(c, "UPDATE machines SET agent_seen = $now WHERE id = $id;", ("$now", Iso(now)), ("$id", id));
            machineName = Scalar(c, "SELECT name FROM machines WHERE id = $id;", ("$id", id)) as string;
        }
        tx.Commit();
        return id is null ? null : new AgentIdentity(id.Value, machineName!);
    }

    private static JobRecord ReadJob(SqliteDataReader r)
    {
        JobParameters parameters;
        try { parameters = JsonSerializer.Deserialize<JobParameters>(r.GetString(4), Json) ?? new JobParameters(); }
        catch (JsonException) { parameters = new JobParameters(); }
        return new JobRecord(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), parameters,
            ParseIso(r.GetString(5)), r.GetString(6), r.GetString(7),
            r.IsDBNull(8) ? null : ParseIso(r.GetString(8)),
            r.IsDBNull(9) ? null : ParseIso(r.GetString(9)),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetInt32(11),
            r.IsDBNull(12) ? null : r.GetInt64(12),
            r.IsDBNull(15) ? null : r.GetString(15),
            r.IsDBNull(13) ? null : ParseIso(r.GetString(13)),
            r.IsDBNull(14) ? null : r.GetInt64(14));
    }
}
