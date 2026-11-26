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
            List<StoredMachine>? machines = null;
            List<GroupRecord>? groups = null;
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
                    machines ??= store.GetMachines();
                    groups ??= store.GetGroups();
                    result = Enqueue(s, now, windowEnd, machines, groups);
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

    private string Enqueue(ScheduleRecord s, DateTimeOffset now, DateTimeOffset? windowEnd, List<StoredMachine> machines, List<GroupRecord> groups)
    {
        List<StoredMachine> targets;
        if (s.GroupId is { } groupId)
        {
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return "Groupe introuvable : aucune tâche créée.";
            targets = machines.Where(m => GroupMatcher.IsMember(group, m)).ToList();
        }
        else
        {
            targets = machines;
        }
        if (targets.Count == 0) return "Aucun poste visé : aucune tâche créée.";

        var by = $"Planification « {s.Name} »";
        int created = 0, skipped = 0;
        foreach (var m in targets)
        {
            var (_, isNew) = store.EnqueueScheduledJob(m.Id, s.JobType, new JobParameters(s.DryRun), by, now, windowEnd, s.Id);
            if (isNew) created++;
            else skipped++;
        }
        var text = created == 1 ? "1 tâche créée" : $"{created} tâches créées";
        if (skipped > 0) text += $", {skipped} poste{(skipped > 1 ? "s" : "")} ignoré{(skipped > 1 ? "s" : "")} (tâche identique déjà en attente)";
        return text + ".";
    }

    private string Local(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
