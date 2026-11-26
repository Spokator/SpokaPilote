using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;
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

public sealed partial class ServerStore
{
    /// <summary>Taille maximale du journal d'exécution conservé pour une tâche.</summary>
    public const int MaxJobLogChars = 64 * 1024;

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
        using var tx = c.BeginTransaction();
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
    /// Planificateur : ajoute une tâche sauf si une tâche du même type est déjà en attente pour ce poste (quels que soient
    /// ses paramètres). <paramref name="notAfter"/> : fin de la fenêtre de maintenance — la tâche n'est plus remise au
    /// poste après cette heure et expire.
    /// </summary>
    public (JobRecord Job, bool Created) EnqueueScheduledJob(long machineRef, string type, JobParameters parameters, string createdBy,
        DateTimeOffset now, DateTimeOffset? notAfter, long scheduleId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
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
            where.Add("m.name = $n");
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
        return Execute(c, """
            UPDATE jobs SET state = 'cancelled', finished_at = $now, message = $msg WHERE id = $id AND state = 'pending';
            """, ("$id", id), ("$now", Iso(now)), ("$msg", $"Annulée depuis la console ({by}).")) > 0;
    }

    /// <summary>
    /// Agent : renvoie les tâches en attente du poste (les plus anciennes d'abord) et les passe « en cours », en une
    /// transaction : deux interrogations simultanées ne reçoivent jamais la même tâche.
    /// </summary>
    public List<JobRecord> ClaimPendingJobs(long machineRef, DateTimeOffset now, int max = 10)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            // Une tâche dont la fenêtre de maintenance est terminée n'est plus remise (elle expire).
            cmd.CommandText = """
                SELECT id FROM jobs WHERE machine_ref = $m AND state = 'pending' AND (not_after IS NULL OR not_after > $now)
                ORDER BY id LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$m", machineRef);
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$max", max);
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
        using var c = Open();
        using var tx = c.BeginTransaction();
        var row = Row(c, "SELECT state FROM jobs WHERE id = $id AND machine_ref = $m;", ("$id", id), ("$m", machineRef));
        if (row is null) return (JobCompletion.NotFound, null);
        if ((string)row[0]! != JobStates.Running)
        {
            tx.Commit();
            return (JobCompletion.NotRunning, GetJob(c, id, withLog: false));
        }
        if (log is { Length: > MaxJobLogChars }) log = "[…]\n" + log[^MaxJobLogChars..];
        Execute(c, """
            UPDATE jobs SET state = $state, finished_at = $now, exit_code = $code, message = $msg, report_id = $report, log = $log WHERE id = $id;
            """, ("$id", id), ("$state", success ? JobStates.Succeeded : JobStates.Failed), ("$now", Iso(now)), ("$code", exitCode),
            ("$msg", message), ("$report", reportId), ("$log", log));
        tx.Commit();
        return (JobCompletion.Completed, GetJob(c, id, withLog: false));
    }

    /// <summary>
    /// Expire les tâches jamais récupérées (poste éteint, agent non activé) et les tâches « en cours » restées sans
    /// résultat (poste redémarré ou arrêté pendant l'exécution). Renvoie les tâches expirées par cet appel.
    /// </summary>
    public List<JobRecord> ExpireJobs(DateTimeOffset now, int pendingDays, int runningHours)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var expired = new List<(long Id, string Message)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, state, not_after IS NOT NULL AND not_after <= $now FROM jobs
                WHERE (state = 'pending' AND (created_at < $p OR (not_after IS NOT NULL AND not_after <= $now)))
                   OR (state = 'running' AND started_at < $r);
                """;
            cmd.Parameters.AddWithValue("$p", Iso(now.AddDays(-pendingDays)));
            cmd.Parameters.AddWithValue("$r", Iso(now.AddHours(-runningHours)));
            cmd.Parameters.AddWithValue("$now", Iso(now));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                expired.Add((reader.GetInt64(0), reader.GetString(1) == JobStates.Running
                    ? $"Aucun résultat reçu {runningHours} h après le démarrage : poste éteint ou redémarré pendant l'exécution ?"
                    : reader.GetBoolean(2)
                        ? "Fenêtre de maintenance terminée avant que le poste ne récupère la tâche (poste éteint ou agent non activé)."
                        : $"Non récupérée par le poste en {pendingDays} jour(s) : agent non activé (spokapilote agent enable) ou poste éteint."));
        }
        foreach (var (id, message) in expired)
            Execute(c, "UPDATE jobs SET finished_at = $now, message = $msg, state = 'expired' WHERE id = $id;",
                ("$id", id), ("$now", Iso(now)), ("$msg", message));
        tx.Commit();
        return expired.Select(e => GetJob(c, e.Id, withLog: false)!).ToList();
    }

    /// <summary>Supprime les tâches terminées depuis plus de <paramref name="days"/> jours.</summary>
    public int PurgeFinishedJobs(DateTimeOffset now, int days = 90)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM jobs WHERE state IN ('succeeded','failed','expired','cancelled') AND finished_at < $t;",
            ("$t", Iso(now.AddDays(-days))));
    }

    /// <summary>Compteurs des tâches, de tout le parc ou des seuls postes indiqués (filtre par groupe).</summary>
    public JobStats GetJobStats(DateTimeOffset now, IReadOnlyCollection<long>? machineRefs = null)
    {
        using var c = Open();
        var filter = machineRefs is null ? "" : $" WHERE machine_ref IN ({(machineRefs.Count == 0 ? "NULL" : string.Join(",", machineRefs))})";
        var row = Row(c, $"""
            SELECT SUM(state = 'pending'), SUM(state = 'running'), SUM(state = 'failed' AND finished_at >= $since) FROM jobs{filter};
            """, ("$since", Iso(now.AddDays(-1))))!;
        static int I(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        return new JobStats(I(row[0]), I(row[1]), I(row[2]));
    }

    /// <summary>Nombre de tâches en attente ou en cours, par poste.</summary>
    public Dictionary<long, int> GetActiveJobCounts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT machine_ref, COUNT(*) FROM jobs WHERE state IN ('pending','running') GROUP BY machine_ref;";
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (reader.Read()) counts[reader.GetInt64(0)] = reader.GetInt32(1);
        return counts;
    }

    /// <summary>
    /// Agent : retrouve le poste qui interroge sa file. Il est reconnu par son identifiant machine (MachineGuid) ; un poste
    /// connu par son seul nom (rapports antérieurs à la 3.0, sans identifiant) est rattaché à ce premier contact.
    /// Enregistre l'heure du contact (visible dans la console). Null : poste inconnu (aucun rapport reçu).
    /// </summary>
    public StoredMachine? FindMachineForAgent(string machineId, string? name, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var id = Scalar(c, "SELECT id FROM machines WHERE machine_id = $mid;", ("$mid", machineId)) as long?;
        if (id is null && !string.IsNullOrWhiteSpace(name) &&
            Scalar(c, "SELECT id FROM machines WHERE name = $n AND machine_id IS NULL;", ("$n", name.Trim())) is long legacy)
        {
            Execute(c, "UPDATE machines SET machine_id = $mid WHERE id = $id;", ("$mid", machineId), ("$id", legacy));
            id = legacy;
        }
        if (id is not null) Execute(c, "UPDATE machines SET agent_seen = $now WHERE id = $id;", ("$now", Iso(now)), ("$id", id));
        tx.Commit();
        return id is null ? null : GetMachine(id.Value);
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
