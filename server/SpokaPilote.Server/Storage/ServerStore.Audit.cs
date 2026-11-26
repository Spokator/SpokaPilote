using System.Globalization;

namespace SpokaPilote.Server.Storage;

/// <summary>Entrée du journal d'audit : qui (compte, jeton ou planificateur), quoi, sur quoi, quand, d'où.</summary>
public sealed record AuditEntry(long Id, DateTimeOffset At, string Actor, string? Role, string Action, string? Target, string? Detail,
    string? Ip, bool Success);

public sealed partial class ServerStore
{
    /// <summary>
    /// Ajoute une entrée au journal d'audit, puis applique la rétention : au plus <paramref name="maxEntries"/> entrées,
    /// aucune de plus de <paramref name="maxDays"/> jours.
    /// </summary>
    public long AddAudit(DateTimeOffset at, string actor, string? role, string action, string? target, string? detail, string? ip, bool success,
        int maxEntries, int maxDays)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var id = Convert.ToInt64(Scalar(c, """
            INSERT INTO audit(at, actor, role, action, target, detail, ip, success) VALUES($at, $a, $r, $act, $t, $d, $ip, $s) RETURNING id;
            """, ("$at", Iso(at)), ("$a", Truncate(actor, 120)), ("$r", role), ("$act", action), ("$t", Truncate(target, 300)),
            ("$d", Truncate(detail, 2000)), ("$ip", ip), ("$s", success ? 1 : 0)), CultureInfo.InvariantCulture);
        PurgeAudit(c, at, maxEntries, maxDays);
        tx.Commit();
        return id;
    }

    /// <summary>Applique la rétention du journal (nombre d'entrées et ancienneté). Renvoie le nombre d'entrées supprimées.</summary>
    public int PurgeAudit(DateTimeOffset now, int maxEntries, int maxDays)
    {
        using var c = Open();
        return PurgeAudit(c, now, maxEntries, maxDays);
    }

    private static int PurgeAudit(Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset now, int maxEntries, int maxDays) =>
        // Deux suppressions sur index (date, clé primaire) : « NOT IN (… LIMIT n) » relisait tout le journal à chaque entrée.
        Execute(c, "DELETE FROM audit WHERE at < $cutoff;", ("$cutoff", Iso(now.AddDays(-maxDays)))) +
        Execute(c, "DELETE FROM audit WHERE id <= (SELECT id FROM audit ORDER BY id DESC LIMIT 1 OFFSET $max);", ("$max", maxEntries));

    /// <summary>Entrées les plus récentes d'abord, filtrées par catégorie d'action (préfixe, ex. « login »), compte ou texte libre.</summary>
    public List<AuditEntry> GetAudit(string? category = null, string? actor = null, string? search = null, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(category))
        {
            where.Add("(action = $cat OR action LIKE $catPrefix ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$cat", category.Trim());
            cmd.Parameters.AddWithValue("$catPrefix", EscapeLike(category.Trim()) + ".%");
        }
        if (!string.IsNullOrWhiteSpace(actor))
        {
            where.Add("actor LIKE $actor ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$actor", "%" + EscapeLike(actor.Trim()) + "%");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(actor LIKE $q ESCAPE '\\' OR action LIKE $q ESCAPE '\\' OR target LIKE $q ESCAPE '\\' OR detail LIKE $q ESCAPE '\\' OR ip LIKE $q ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(search.Trim()) + "%");
        }
        cmd.CommandText = $"""
            SELECT id, at, actor, role, action, target, detail, ip, success FROM audit
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000_000));
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read())
            list.Add(new AuditEntry(r.GetInt64(0), ParseIso(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0));
        return list;
    }

    public int CountAudit()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM audit;"), CultureInfo.InvariantCulture);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
