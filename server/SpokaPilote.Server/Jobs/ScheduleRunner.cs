using System.Globalization;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Jobs;

/// <summary>
/// Planificateur : toutes les 30 secondes, crée les tâches des planifications arrivées à échéance (file de tâches
/// existante, aucun nouveau type de tâche). Une tâche du même type déjà en attente pour un poste n'est pas dupliquée.
/// Avec une fenêtre de maintenance, les tâches ne sont remises aux postes que jusqu'à la fin de la fenêtre ; une échéance
/// manquée (console arrêtée) est rattrapée une fois au redémarrage, sauf si sa fenêtre est déjà terminée.
/// </summary>
public sealed class ScheduleRunner(ServerStore store, AuditService audit, TimeProvider clock, ILogger<ScheduleRunner> log) : BackgroundService
{
    private readonly Lock _gate = new();

    /// <summary>Fuseau horaire des planifications : celui du serveur (variable TZ du conteneur).</summary>
    public TimeZoneInfo Zone { get; set; } = clock.LocalTimeZone;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                RunDue(clock.GetUtcNow());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Planificateur en échec.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Prochaine échéance d'une planification à partir de maintenant.</summary>
    public DateTimeOffset NextRun(ScheduleRecord s, DateTimeOffset now) => ScheduleCalculator.NextRun(s, now, Zone);

    /// <summary>Exécute les planifications arrivées à échéance (public pour les tests). Renvoie le nombre de planifications traitées.</summary>
    public int RunDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var s in store.GetSchedules().Where(s => s.Enabled))
            {
                if (s.NextRun is null)
                {
                    store.RecordScheduleRun(s.Id, null, null, NextRun(s, now));
                    continue;
                }
                if (s.NextRun > now) continue;

                var due = s.NextRun.Value;
                DateTimeOffset? windowEnd = s.WindowMinutes is { } w ? due.AddMinutes(w) : null;
                string result;
                if (windowEnd is not null && now >= windowEnd)
                {
                    result = $"Échéance du {Local(due)} manquée : la console était arrêtée pendant toute la fenêtre de maintenance.";
                }
                else
                {
                    result = Enqueue(s, now, windowEnd);
                }
                var next = NextRun(s, now);
                store.RecordScheduleRun(s.Id, now, result, next);
                audit.Record(AuditService.SchedulerActor, null, AuditActions.ScheduleRun, $"{s.Name} (n° {s.Id})",
                    $"{JobTypes.Find(s.JobType)?.Label ?? s.JobType}{(s.DryRun ? " (simulation)" : "")} — {result}", null);
                log.LogInformation("Planification {Id} « {Name} » : {Result} Prochaine exécution : {Next}.", s.Id, s.Name, result, Local(next));
                count++;
            }
            return count;
        }
    }

    private string Enqueue(ScheduleRecord s, DateTimeOffset now, DateTimeOffset? windowEnd)
    {
        List<(long Id, string Name)> targets;
        if (s.GroupId is { } groupId)
        {
            if (!store.GroupExists(groupId)) return "Groupe introuvable : aucune tâche créée.";
            targets = store.GetMachineRefs(groupId);
        }
        else
        {
            targets = store.GetMachineRefs();
        }
        if (targets.Count == 0) return "Aucun poste visé : aucune tâche créée.";

        var by = $"Planification « {s.Name} »";
        // Par lots de 500 postes par transaction : le verrou d'écriture reste bref même sur tout un grand parc.
        var (created, skipped) = store.EnqueueScheduledJobs(targets.Select(t => t.Id).ToList(), s.JobType, new JobParameters(s.DryRun), by, now,
            windowEnd, s.Id);
        var text = created == 1 ? "1 tâche créée" : $"{created} tâches créées";
        if (skipped > 0) text += $", {skipped} poste{(skipped > 1 ? "s" : "")} ignoré{(skipped > 1 ? "s" : "")} (tâche identique déjà en attente)";
        return text + ".";
    }

    private string Local(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
