using System.Globalization;
using System.Threading.RateLimiting;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Api;

/// <summary>
/// Contrôle d'admission des requêtes des postes (afflux de milliers de postes après une coupure de courant, une campagne
/// d'analyses, une mise à jour...) : un nombre borné de requêtes est traité en même temps, les suivantes attendent leur
/// tour dans une file bornée ; au-delà (ou après l'attente maximale), la console répond 503 avec un en-tête Retry-After
/// étalé aléatoirement, plutôt que de s'effondrer ou d'épuiser la mémoire. Les postes 3.2 réessaient après ce délai ; les
/// postes plus anciens renverront leur rapport au passage suivant (l'envoi n'est jamais bloquant).
/// </summary>
public sealed class AdmissionControl : IDisposable
{
    public AdmissionControl(ServerOptions options)
    {
        Options = options;
        Uploads = new Gate("reports", options.MaxConcurrentUploads, options.UploadQueueLimit, options.QueueTimeout);
        Agents = new Gate("agents", options.MaxConcurrentAgentRequests, options.AgentQueueLimit, options.QueueTimeout);
    }

    public ServerOptions Options { get; }
    public Gate Uploads { get; }
    public Gate Agents { get; }

    /// <summary>Réponse 503 « console occupée » avec un délai d'attente étalé (½ à 1,5 × SPOKAPILOTE_RETRY_AFTER_SECONDS).</summary>
    public IResult Busy(HttpContext http, string what)
    {
        var seconds = (int)Math.Round(Options.RetryAfterSeconds * (0.5 + Random.Shared.NextDouble()));
        http.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
        return Results.Json(new ApiError($"Console momentanément surchargée ({what}) : nouvel essai dans {seconds} s."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    public void Dispose()
    {
        Uploads.Dispose();
        Agents.Dispose();
    }

    /// <summary>File d'attente bornée devant un type de requête (compteurs exposés par /api/metrics).</summary>
    public sealed class Gate : IDisposable
    {
        private readonly ConcurrencyLimiter _limiter;
        private readonly TimeSpan _timeout;
        private long _accepted;
        private long _rejected;

        public Gate(string name, int permits, int queue, TimeSpan timeout)
        {
            Name = name;
            Permits = permits;
            QueueLimit = queue;
            _timeout = timeout;
            _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = permits,
                QueueLimit = queue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }

        public string Name { get; }
        public int Permits { get; }
        public int QueueLimit { get; }
        public long Accepted => Interlocked.Read(ref _accepted);
        public long Rejected => Interlocked.Read(ref _rejected);
        public long Available => _limiter.GetStatistics()?.CurrentAvailablePermits ?? 0;
        public long Queued => _limiter.GetStatistics()?.CurrentQueuedCount ?? 0;

        /// <summary>Attend une place (au plus le délai de file) ; null si la file est pleine ou l'attente trop longue.</summary>
        public async ValueTask<RateLimitLease?> EnterAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            RateLimitLease lease;
            try
            {
                lease = await _limiter.AcquireAsync(1, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _rejected);
                return null;
            }
            if (lease.IsAcquired)
            {
                Interlocked.Increment(ref _accepted);
                return lease;
            }
            lease.Dispose();
            Interlocked.Increment(ref _rejected);
            return null;
        }

        public void Dispose() => _limiter.Dispose();
    }
}
