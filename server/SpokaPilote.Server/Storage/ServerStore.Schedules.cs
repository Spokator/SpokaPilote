using System.Globalization;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Jobs;

namespace SpokaPilote.Server.Storage;

public sealed partial class ServerStore
{
    private const string ScheduleColumns = """
        id, name, job_type, dry_run, group_id, frequency, days, time, window_minutes, enabled, created_at, created_by, updated_at,
        next_run, last_run, last_result
        """;

    public List<ScheduleRecord> GetSchedules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ScheduleColumns} FROM schedules ORDER BY name COLLATE NOCASE, id;";
        using var r = cmd.ExecuteReader();
        var list = new List<ScheduleRecord>();
        while (r.Read()) list.Add(ReadSchedule(r));
        return list;
    }

    public ScheduleRecord? GetSchedule(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ScheduleColumns} FROM schedules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSchedule(r) : null;
    }

    public long CreateSchedule(ScheduleRecord s)
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, """
            INSERT INTO schedules(name, job_type, dry_run, group_id, frequency, days, time, window_minutes, enabled, created_at, created_by,
                                  updated_at, next_run)
            VALUES($n, $t, $dry, $g, $f, $days, $time, $w, $en, $at, $by, $at, $next) RETURNING id;
            """, [.. ScheduleParameters(s), ("$at", Iso(s.CreatedAt)), ("$by", s.CreatedBy)]), CultureInfo.InvariantCulture);
    }

    /// <summary>Modifie une planification (l'historique de la dernière exécution est conservé).</summary>
    public bool UpdateSchedule(ScheduleRecord s)
    {
        using var c = Open();
        return Execute(c, """
            UPDATE schedules SET name = $n, job_type = $t, dry_run = $dry, group_id = $g, frequency = $f, days = $days, time = $time,
                window_minutes = $w, enabled = $en, updated_at = $up, next_run = $next WHERE id = $id;
            """, [.. ScheduleParameters(s), ("$id", s.Id), ("$up", Iso(s.UpdatedAt))]) > 0;
    }

    public bool DeleteSchedule(long id)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM schedules WHERE id = $id;", ("$id", id)) > 0;
    }

    /// <summary>Enregistre un passage du planificateur : date, résultat lisible et prochaine échéance.</summary>
    public void RecordScheduleRun(long id, DateTimeOffset? lastRun, string? result, DateTimeOffset? nextRun)
    {
        using var c = Open();
        Execute(c, """
            UPDATE schedules SET last_run = COALESCE($last, last_run), last_result = COALESCE($res, last_result), next_run = $next WHERE id = $id;
            """, ("$id", id), ("$last", lastRun is { } l ? Iso(l) : null), ("$res", result), ("$next", nextRun is { } n ? Iso(n) : null));
    }

    public int CountSchedulesForGroup(long groupId)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM schedules WHERE group_id = $g;", ("$g", groupId)), CultureInfo.InvariantCulture);
    }

    private static IEnumerable<(string, object?)> ScheduleParameters(ScheduleRecord s) =>
    [
        ("$n", s.Name), ("$t", s.JobType), ("$dry", s.DryRun ? 1 : 0), ("$g", s.GroupId), ("$f", s.Frequency),
        ("$days", string.Join(",", s.Days.Select(ScheduleCalculator.ToIso))),
        ("$time", s.Time.ToString("HH:mm", CultureInfo.InvariantCulture)), ("$w", s.WindowMinutes), ("$en", s.Enabled ? 1 : 0),
        ("$next", s.NextRun is { } n ? Iso(n) : null),
    ];

    private static ScheduleRecord ReadSchedule(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
        r.IsDBNull(4) ? null : r.GetInt64(4),
        r.GetString(5),
        r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => ScheduleCalculator.FromIso(int.Parse(d, CultureInfo.InvariantCulture))).ToList(),
        TimeOnly.ParseExact(r.GetString(7), "HH:mm", CultureInfo.InvariantCulture),
        r.IsDBNull(8) ? null : r.GetInt32(8),
        r.GetInt64(9) != 0,
        ParseIso(r.GetString(10)), r.GetString(11), ParseIso(r.GetString(12)),
        r.IsDBNull(13) ? null : ParseIso(r.GetString(13)),
        r.IsDBNull(14) ? null : ParseIso(r.GetString(14)),
        r.IsDBNull(15) ? null : r.GetString(15));
}
