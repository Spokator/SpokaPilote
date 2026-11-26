using System.Security.Cryptography;
using System.Text;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Security;

/// <summary>
/// Sessions de la console pour les comptes nominatifs : cookie aléatoire de 256 bits (HttpOnly, SameSite=Strict, Secure
/// en HTTPS, sans date d'expiration : effacé à la fermeture du navigateur), dont seule l'empreinte SHA-256 est conservée
/// en base. Expiration glissante après une période d'inactivité (SPOKAPILOTE_SESSION_HOURS, 8 h par défaut) et durée
/// maximale de 7 jours. Chaque session a son jeton anti-CSRF, exigé dans l'en-tête X-SpokaPilote-CSRF des requêtes qui
/// modifient quelque chose.
/// </summary>
public sealed class SessionService(ServerStore store, ServerOptions options, TimeProvider clock)
{
    public const string CookieName = "SpokaPilote.Session";
    public const string CsrfHeader = "X-SpokaPilote-CSRF";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private TimeSpan Idle => TimeSpan.FromHours(options.SessionIdleHours);
    private TimeSpan Lifetime => Idle > MaximumLifetime ? Idle : MaximumLifetime;

    /// <summary>Ouvre une session pour ce compte et dépose le cookie. Renvoie la session (avec son jeton anti-CSRF).</summary>
    public SessionRecord Open(UserRecord user, HttpContext http)
    {
        var token = TokenService.Generate();
        var csrf = TokenService.Generate();
        var now = clock.GetUtcNow();
        var hash = HashId(token);
        store.CreateSession(hash, user.Id, csrf, now, Min(now + Idle, now + Lifetime), http.Connection.RemoteIpAddress?.ToString());
        http.Response.Cookies.Append(CookieName, token, CookieOptions(http));
        return store.GetSession(hash)!;
    }

    /// <summary>Session valide correspondant au cookie (null si inconnue, expirée ou compte désactivé), prolongée si active.</summary>
    public SessionRecord? Resolve(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Length > 200) return null;
        var hash = HashId(cookie);
        var session = store.GetSession(hash);
        if (session is null) return null;
        var now = clock.GetUtcNow();
        if (now >= session.ExpiresAt || now >= session.CreatedAt + Lifetime || !session.User.Enabled)
        {
            store.DeleteSession(hash);
            return null;
        }
        if (now - session.LastSeen >= TouchInterval)
        {
            var expires = Min(now + Idle, session.CreatedAt + Lifetime);
            store.TouchSession(hash, now, expires);
            session = session with { LastSeen = now, ExpiresAt = expires };
        }
        return session;
    }

    /// <summary>Ferme la session et efface le cookie.</summary>
    public void Close(HttpContext http, SessionRecord? session)
    {
        if (session is not null) store.DeleteSession(session.IdHash);
        http.Response.Cookies.Delete(CookieName, CookieOptions(http));
    }

    /// <summary>Le jeton anti-CSRF présenté correspond-il à celui de la session ? (comparaison en temps constant)</summary>
    public static bool CsrfMatches(SessionRecord session, string? presented) =>
        presented is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(session.Csrf));

    public static string HashId(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static CookieOptions CookieOptions(HttpContext http) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        // Derrière un reverse proxy HTTPS, SPOKAPILOTE_BEHIND_PROXY=true fait voir la requête comme HTTPS (X-Forwarded-Proto).
        Secure = http.Request.IsHttps,
        Path = "/",
        IsEssential = true,
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
