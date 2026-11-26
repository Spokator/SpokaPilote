using System.Collections.Concurrent;
using System.Net;

namespace SpokaPilote.Server.Security;

/// <summary>
/// Limite les tentatives d'authentification refusées par adresse IP (par défaut 10 par minute) : au-delà, toute requête
/// authentifiée de cette adresse reçoit 429 jusqu'à la fin de la minute, même avec un jeton valide, pour rendre inutile
/// la recherche de jetons par essais successifs. Fenêtre fixe d'une minute, en mémoire (remise à zéro au redémarrage).
/// Derrière un reverse proxy, activez ASPNETCORE_FORWARDEDHEADERS_ENABLED pour que l'adresse réelle du poste soit utilisée.
/// </summary>
public sealed class AuthFailureLimiter(ServerOptions options, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<IPAddress, Counter> _counters = new();
    private long _lastCleanup;

    private sealed class Counter
    {
        public DateTimeOffset Start;
        public int Failures;
    }

    public bool Enabled => options.AuthFailuresPerMinute > 0;

    /// <summary>Adresse bloquée ? Renvoie le délai avant déblocage (null si autorisée).</summary>
    public TimeSpan? BlockedFor(IPAddress? ip)
    {
        if (!Enabled || ip is null || !_counters.TryGetValue(Normalize(ip), out var c)) return null;
        lock (c)
        {
            var now = clock.GetUtcNow();
            if (now - c.Start >= Window) return null;
            return c.Failures >= options.AuthFailuresPerMinute ? c.Start + Window - now : null;
        }
    }

    /// <summary>Compte un jeton refusé pour cette adresse.</summary>
    public void RecordFailure(IPAddress? ip)
    {
        if (!Enabled || ip is null) return;
        var now = clock.GetUtcNow();
        var c = _counters.GetOrAdd(Normalize(ip), _ => new Counter { Start = now });
        lock (c)
        {
            if (now - c.Start >= Window)
            {
                c.Start = now;
                c.Failures = 0;
            }
            c.Failures++;
        }
        Cleanup(now);
    }

    private void Cleanup(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastCleanup);
        if (now.UtcTicks - last < Window.Ticks * 5 || Interlocked.CompareExchange(ref _lastCleanup, now.UtcTicks, last) != last) return;
        foreach (var (ip, c) in _counters)
            if (now - c.Start >= Window) _counters.TryRemove(ip, out _);
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
