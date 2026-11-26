using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SpokaPilote.Server.Storage;

/// <summary>Compte nominatif de la console (le mot de passe n'est conservé que sous forme d'empreinte PBKDF2).</summary>
public sealed record UserRecord(long Id, string Login, string? DisplayName, string Role, string PasswordHash, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset PasswordChangedAt, DateTimeOffset? LastLogin)
{
    /// <summary>« Jean Dupont (jdupont) » ou « jdupont ».</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Login : $"{DisplayName} ({Login})";
}

/// <summary>Session ouverte dans la console (seule l'empreinte SHA-256 du cookie est conservée).</summary>
public sealed record SessionRecord(string IdHash, string Csrf, DateTimeOffset CreatedAt, DateTimeOffset LastSeen, DateTimeOffset ExpiresAt, UserRecord User);

public sealed partial class ServerStore
{
    private const string UserColumns = "u.id, u.login, u.display_name, u.role, u.password_hash, u.enabled, u.created_at, u.updated_at, u.password_changed_at, u.last_login";

    public List<UserRecord> GetUsers()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM users u ORDER BY u.login;";
        using var r = cmd.ExecuteReader();
        var list = new List<UserRecord>();
        while (r.Read()) list.Add(ReadUser(r, 0));
        return list;
    }

    public UserRecord? GetUser(long id) => QueryUser("u.id = $v", id);

    public UserRecord? GetUserByLogin(string login) => QueryUser("u.login = $v", login.Trim());

    public int CountUsers()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM users;"), CultureInfo.InvariantCulture);
    }

    /// <summary>Crée un compte ; null si l'identifiant est déjà pris.</summary>
    public long? CreateUser(string login, string? displayName, string role, string passwordHash, bool enabled, DateTimeOffset now)
    {
        using var c = Open();
        try
        {
            return Convert.ToInt64(Scalar(c, """
                INSERT INTO users(login, display_name, role, password_hash, enabled, created_at, updated_at, password_changed_at)
                VALUES($l, $d, $r, $h, $e, $now, $now, $now) RETURNING id;
                """, ("$l", login), ("$d", displayName), ("$r", role), ("$h", passwordHash), ("$e", enabled ? 1 : 0), ("$now", Iso(now))),
                CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null;
        }
    }

    public bool UpdateUser(long id, string? displayName, string role, bool enabled, DateTimeOffset now)
    {
        using var c = Open();
        return Execute(c, "UPDATE users SET display_name = $d, role = $r, enabled = $e, updated_at = $now WHERE id = $id;",
            ("$id", id), ("$d", displayName), ("$r", role), ("$e", enabled ? 1 : 0), ("$now", Iso(now))) > 0;
    }

    public bool SetPassword(long id, string passwordHash, DateTimeOffset now)
    {
        using var c = Open();
        return Execute(c, "UPDATE users SET password_hash = $h, password_changed_at = $now, updated_at = $now WHERE id = $id;",
            ("$id", id), ("$h", passwordHash), ("$now", Iso(now))) > 0;
    }

    /// <summary>Remplace l'empreinte sans toucher aux dates (passage à un nombre d'itérations plus élevé à la connexion).</summary>
    public void UpgradePasswordHash(long id, string passwordHash)
    {
        using var c = Open();
        Execute(c, "UPDATE users SET password_hash = $h WHERE id = $id;", ("$id", id), ("$h", passwordHash));
    }

    public void SetLastLogin(long id, DateTimeOffset now)
    {
        using var c = Open();
        Execute(c, "UPDATE users SET last_login = $now WHERE id = $id;", ("$id", id), ("$now", Iso(now)));
    }

    public bool DeleteUser(long id)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM users WHERE id = $id;", ("$id", id)) > 0;
    }

    // ------------------------------------------------------------------ sessions

    public void CreateSession(string idHash, long userId, string csrf, DateTimeOffset now, DateTimeOffset expiresAt, string? ip)
    {
        using var c = Open();
        Execute(c, """
            INSERT INTO sessions(id_hash, user_id, csrf, created_at, last_seen, expires_at, ip) VALUES($h, $u, $csrf, $now, $now, $exp, $ip);
            """, ("$h", idHash), ("$u", userId), ("$csrf", csrf), ("$now", Iso(now)), ("$exp", Iso(expiresAt)), ("$ip", ip));
    }

    public SessionRecord? GetSession(string idHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.id_hash, s.csrf, s.created_at, s.last_seen, s.expires_at, {UserColumns}
            FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.id_hash = $h;
            """;
        cmd.Parameters.AddWithValue("$h", idHash);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new SessionRecord(r.GetString(0), r.GetString(1), ParseIso(r.GetString(2)), ParseIso(r.GetString(3)), ParseIso(r.GetString(4)), ReadUser(r, 5))
            : null;
    }

    /// <summary>Prolonge une session active (expiration glissante).</summary>
    public void TouchSession(string idHash, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        using var c = Open();
        Execute(c, "UPDATE sessions SET last_seen = $now, expires_at = $exp WHERE id_hash = $h;",
            ("$h", idHash), ("$now", Iso(now)), ("$exp", Iso(expiresAt)));
    }

    public bool DeleteSession(string idHash)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM sessions WHERE id_hash = $h;", ("$h", idHash)) > 0;
    }

    /// <summary>Ferme les sessions d'un compte (mot de passe changé, compte désactivé ou rôle modifié), sauf éventuellement la session courante.</summary>
    public int DeleteSessionsForUser(long userId, string? exceptIdHash = null)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM sessions WHERE user_id = $u AND ($except IS NULL OR id_hash <> $except);",
            ("$u", userId), ("$except", exceptIdHash));
    }

    public int PurgeSessions(DateTimeOffset now)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM sessions WHERE expires_at <= $now;", ("$now", Iso(now)));
    }

    private UserRecord? QueryUser(string where, object value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM users u WHERE {where};";
        cmd.Parameters.AddWithValue("$v", value);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r, 0) : null;
    }

    private static UserRecord ReadUser(SqliteDataReader r, int o) => new(
        r.GetInt64(o), r.GetString(o + 1), r.IsDBNull(o + 2) ? null : r.GetString(o + 2), r.GetString(o + 3), r.GetString(o + 4),
        r.GetInt64(o + 5) != 0, ParseIso(r.GetString(o + 6)), ParseIso(r.GetString(o + 7)), ParseIso(r.GetString(o + 8)),
        r.IsDBNull(o + 9) ? null : ParseIso(r.GetString(o + 9)));
}
