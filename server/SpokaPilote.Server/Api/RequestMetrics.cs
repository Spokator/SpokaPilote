using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpokaPilote.Server.Api;

/// <summary>
/// Temps de réponse de l'API par route (gabarit, ex. « GET /api/machines/{name} ») : nombre de requêtes et d'erreurs
/// depuis le démarrage, centiles sur les 2 048 dernières requêtes de chaque route. En mémoire, coût négligeable ;
/// exposé par GET /api/metrics.
/// </summary>
public sealed class RequestMetrics
{
    private const int Window = 2048;
    private readonly ConcurrentDictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private long _inFlight;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public long InFlight => Interlocked.Read(ref _inFlight);

    public sealed record RouteStats(string Route, long Count, long Errors, double P50, double P95, double P99, double Max);

    private sealed class Route
    {
        public readonly double[] Samples = new double[Window];
        public long Count;
        public long Errors;
        public readonly Lock Lock = new();
    }

    public void Record(string route, double milliseconds, int status)
    {
        var r = _routes.GetOrAdd(route, _ => new Route());
        lock (r.Lock)
        {
            r.Samples[r.Count % Window] = milliseconds;
            r.Count++;
            if (status >= 500) r.Errors++;
        }
    }

    public List<RouteStats> Snapshot() => _routes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
    {
        double[] samples;
        long count, errors;
        lock (kv.Value.Lock)
        {
            count = kv.Value.Count;
            errors = kv.Value.Errors;
            samples = kv.Value.Samples.Take((int)Math.Min(count, Window)).ToArray();
        }
        Array.Sort(samples);
        double P(double q) => samples.Length == 0 ? 0 : samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(q * samples.Length) - 1)];
        return new RouteStats(kv.Key, count, errors, Math.Round(P(0.50), 2), Math.Round(P(0.95), 2), Math.Round(P(0.99), 2),
            samples.Length == 0 ? 0 : Math.Round(samples[^1], 2));
    }).ToList();

    /// <summary>Intergiciel : mesure chaque requête de l'API (les fichiers de la console ne sont pas comptés).</summary>
    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }
        Interlocked.Increment(ref _inFlight);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            var route = context.GetEndpoint() is RouteEndpoint e ? e.RoutePattern.RawText ?? context.Request.Path.Value : "(inconnue)";
            Record($"{context.Request.Method} {route}", Stopwatch.GetElapsedTime(start).TotalMilliseconds, context.Response.StatusCode);
        }
    }
}
