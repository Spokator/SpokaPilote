namespace SpokaPilote.Server.Storage;

/// <summary>Alerte envoyée (ou tentée) : historique affiché dans la page Paramètres.</summary>
public sealed record AlertEventRecord(long Id, DateTimeOffset CreatedAt, string? Machine, string Reason, string Title, string Status, string? Detail);

public sealed partial class ServerStore
{
    public const string AlertSettingsKey = "alert-settings";
    public const string DailySummaryKey = "alert-daily-summary";
    private const int AlertEventsKept = 200;

    /// <summary>
    /// Anti-spam : réserve l'envoi d'une alerte pour ce sujet (poste) et ce motif. Renvoie false si la même alerte a déjà
    /// été envoyée il y a moins de <paramref name="window"/> ; sinon enregistre l'envoi et renvoie true (atomique).
    /// </summary>
    public bool TryRegisterAlert(string subject, string reason, DateTimeOffset now, TimeSpan window)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Scalar(c, "SELECT last_sent FROM alert_log WHERE subject = $s AND reason = $r;", ("$s", subject), ("$r", reason)) is string last &&
            now - ParseIso(last) < window)
            return false;
        Execute(c, """
            INSERT INTO alert_log(subject, reason, last_sent) VALUES($s, $r, $now)
            ON CONFLICT(subject, reason) DO UPDATE SET last_sent = excluded.last_sent;
            """, ("$s", subject), ("$r", reason), ("$now", Iso(now)));
        tx.Commit();
        return true;
    }

    /// <summary>Oublie les envois anciens (la fenêtre anti-spam ne dépasse jamais quelques jours).</summary>
    public int PurgeAlertLog(DateTimeOffset now, int days = 30)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM alert_log WHERE last_sent < $t;", ("$t", Iso(now.AddDays(-days))));
    }

    public void AddAlertEvent(DateTimeOffset at, string? machine, string reason, string title, string status, string? detail)
    {
        using var c = Open();
        Execute(c, "INSERT INTO alert_events(created_at, machine, reason, title, status, detail) VALUES($at, $m, $r, $t, $s, $d);",
            ("$at", Iso(at)), ("$m", machine), ("$r", reason), ("$t", title), ("$s", status), ("$d", detail));
        Execute(c, "DELETE FROM alert_events WHERE id NOT IN (SELECT id FROM alert_events ORDER BY id DESC LIMIT $keep);", ("$keep", AlertEventsKept));
    }

    public List<AlertEventRecord> GetAlertEvents(int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, created_at, machine, reason, title, status, detail FROM alert_events ORDER BY id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, AlertEventsKept));
        using var r = cmd.ExecuteReader();
        var list = new List<AlertEventRecord>();
        while (r.Read())
            list.Add(new AlertEventRecord(r.GetInt64(0), ParseIso(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }
}
