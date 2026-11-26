using System.Globalization;
using System.Net;
using System.Text;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Alerts;

/// <summary>Envoi d'une alerte sur tous les canaux configurés (e-mail, webhooks actifs).</summary>
public sealed class AlertSender(EmailSender email, IHttpClientFactory httpFactory, ILogger<AlertSender> log)
{
    public const string HttpClientName = "SpokaPilote.Webhooks";

    public async Task<List<ChannelResult>> SendAsync(Alert alert, AlertSettings settings, CancellationToken ct)
    {
        var results = new List<ChannelResult>();
        if (settings.Smtp.IsConfigured)
        {
            var label = $"E-mail ({string.Join(", ", settings.Smtp.To)})";
            try
            {
                await email.SendAsync(settings.Smtp, alert, ct).ConfigureAwait(false);
                results.Add(new ChannelResult(label, true, "Envoyé."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                results.Add(new ChannelResult(label, false, Describe(ex)));
            }
        }
        foreach (var hook in settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)))
        {
            var label = $"Webhook {hook.Name} ({hook.Format})";
            try
            {
                using var content = new StringContent(WebhookFormatter.Format(hook.Format, alert), Encoding.UTF8, "application/json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await httpFactory.CreateClient(HttpClientName).PostAsync(hook.Url, content, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) results.Add(new ChannelResult(label, true, $"Accepté ({(int)response.StatusCode})."));
                else
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    results.Add(new ChannelResult(label, false,
                        $"Refusé par le service ({(int)response.StatusCode}){(string.IsNullOrWhiteSpace(body) ? "" : " : " + (body.Length > 200 ? body[..200] + "…" : body))}" +
                        ((int)response.StatusCode is >= 300 and < 400 ? " — les redirections ne sont pas suivies, utilisez l'adresse finale." : "")));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                results.Add(new ChannelResult(label, false, Describe(ex)));
            }
        }
        foreach (var r in results.Where(r => !r.Success)) log.LogWarning("Alerte « {Title} » non transmise par {Channel} : {Message}", alert.Title, r.Channel, r.Message);
        return results;
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Délai dépassé (le service ne répond pas).",
        HttpRequestException h => $"Service injoignable : {h.Message}",
        _ => ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})",
    };
}

/// <summary>Consomme la file des alertes et les envoie ; chaque envoi est consigné (page Paramètres).</summary>
public sealed class AlertDispatcher(AlertQueue queue, AlertSender sender, AlertSettingsService settings, ServerStore store,
    TimeProvider clock, ILogger<AlertDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var results = await sender.SendAsync(alert, settings.Current(), stoppingToken).ConfigureAwait(false);
                var status = results.Count == 0 ? "aucun canal" : results.All(r => r.Success) ? "envoyée" : results.Any(r => r.Success) ? "partielle" : "échec";
                store.AddAlertEvent(clock.GetUtcNow(), alert.Machine, alert.Reason, alert.Title, status,
                    string.Join(" · ", results.Select(r => $"{r.Channel} : {r.Message}")));
                log.LogInformation("Alerte « {Title} » : {Status}.", alert.Title, status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Envoi de l'alerte « {Title} » impossible.", alert.Title);
            }
        }
    }
}

/// <summary>
/// Tâches périodiques du serveur (toutes les minutes) : expiration des tâches à distance, alertes « poste non vu »,
/// résumé quotidien et nettoyage (tâches terminées, sessions expirées, rétention du journal d'audit).
/// </summary>
public sealed class FleetMonitor(ServerStore store, ServerOptions options, AlertService alerts, AlertSettingsService settings, AuditService audit,
    TimeProvider clock, ILogger<FleetMonitor> log) : BackgroundService
{
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                RunOnce(clock.GetUtcNow());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Vérification périodique en échec.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Un passage complet (public pour les tests).</summary>
    public void RunOnce(DateTimeOffset now)
    {
        foreach (var job in store.ExpireJobs(now, options.JobExpiryDays, options.JobRunningTimeoutHours))
        {
            log.LogInformation("Tâche {Id} ({Type}) du poste {Machine} expirée : {Message}", job.Id, job.Type, job.Machine, job.Message);
            alerts.OnJobFinished(job);
        }

        if (now - _lastHousekeeping >= TimeSpan.FromMinutes(15))
        {
            _lastHousekeeping = now;
            var stale = alerts.CheckStaleMachines(now);
            if (stale > 0) log.LogInformation("{Count} poste(s) non vu(s) signalé(s).", stale);
            store.PurgeFinishedJobs(now);
            store.PurgeAlertLog(now);
            store.PurgeSessions(now);
            audit.Purge(now);
        }

        var s = settings.Current();
        if (s.HasChannel && AlertSettingsService.ParseTime(s.DailySummaryTime) is { } at)
        {
            var local = TimeZoneInfo.ConvertTime(now, clock.LocalTimeZone);
            var today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (local.TimeOfDay >= at && store.GetDocument(ServerStore.DailySummaryKey)?.Json != $"\"{today}\"")
            {
                store.SetDocument(ServerStore.DailySummaryKey, $"\"{today}\"", now);
                alerts.Enqueue(alerts.BuildDailySummary(now));
            }
        }
    }
}

/// <summary>Extension d'enregistrement des services d'alerte.</summary>
public static class AlertServiceCollectionExtensions
{
    public static IServiceCollection AddSpokaPiloteAlerts(this IServiceCollection services)
    {
        services.AddSingleton<AlertSettingsService>();
        services.AddSingleton<AlertQueue>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<EmailSender>();
        services.AddSingleton<AlertSender>();
        // Webhooks : jamais de redirection suivie (l'adresse contient souvent un jeton), pas de cookies.
        services.AddHttpClient(AlertSender.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("SpokaPilote-Server"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddHostedService<AlertDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<FleetMonitor>());
        services.AddSingleton<FleetMonitor>();
        return services;
    }
}
