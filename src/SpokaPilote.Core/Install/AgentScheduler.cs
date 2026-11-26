using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

/// <summary>
/// Tâche planifiée « \SpokaPilote\Agent » : exécutée en SYSTEM toutes les N minutes, elle lance « spokapilote agent run »,
/// qui récupère et exécute les tâches à distance demandées depuis la console centrale. Elle s'ajoute à la tâche de
/// maintenance (\SpokaPilote\Maintenance) sans la modifier, et réutilise le même socle (MaintenanceScheduler).
/// Sans console configurée, chaque exécution se termine aussitôt sans rien faire.
/// </summary>
public static class AgentScheduler
{
    public const string TaskName = @"\SpokaPilote\Agent";
    public const int DefaultIntervalMinutes = 15;
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static IReadOnlyList<string> Arguments => ["agent", "run", "--quiet"];

    /// <summary>
    /// Définition XML : déclencheur horaire répété indéfiniment toutes les <paramref name="everyMinutes"/> minutes
    /// (première exécution une période après la création), autorisé sur batterie (l'interrogation est légère ; les
    /// tâches lourdes ne s'exécutent que si l'IT les a demandées), 2 h au plus par exécution, instance unique.
    /// </summary>
    public static string BuildTaskXml(int everyMinutes, string executable, DateTime? now = null)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(everyMinutes, MinIntervalMinutes, MaxIntervalMinutes));
        var start = (now ?? DateTime.Now) + interval;
        var trigger = new XElement(Ns + "TimeTrigger",
            new XElement(Ns + "Repetition",
                new XElement(Ns + "Interval", XmlConvert.ToString(interval)),
                new XElement(Ns + "StopAtDurationEnd", "false")),
            new XElement(Ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(Ns + "Enabled", "true"),
            // Étale les interrogations d'un parc dont les tâches ont été créées au même moment.
            new XElement(Ns + "RandomDelay", "PT1M"));
        var description = $"Agent SpokaPilote : récupère et exécute les tâches à distance demandées depuis la console centrale " +
                          $"(toutes les {interval.TotalMinutes:0} min). Journal : %ProgramData%\\SpokaPilote\\Logs.";
        return MaintenanceScheduler.BuildSystemTaskXml(TaskName, description, trigger, runOnBattery: true,
            TimeSpan.FromHours(2), executable, Arguments);
    }

    public static async Task<(bool Ok, string Message)> EnableAsync(int everyMinutes, string executable, CancellationToken ct = default)
    {
        if (everyMinutes is < MinIntervalMinutes or > MaxIntervalMinutes)
            return (false, $"Intervalle invalide : {everyMinutes} min ({MinIntervalMinutes} à {MaxIntervalMinutes}).");
        if (MaintenanceScheduler.ValidateExecutable(executable) is { } error) return (false, error);
        if (!Elevation.IsAdministrator()) return (false, "La création de la tâche planifiée nécessite une console administrateur.");

        var (ok, message, file) = await MaintenanceScheduler.RegisterTaskAsync(TaskName, BuildTaskXml(everyMinutes, Path.GetFullPath(executable)),
            "agent-task.xml", ct).ConfigureAwait(false);
        if (!ok) return (false, message);
        Log.Info($"Tâche planifiée {TaskName} créée : toutes les {everyMinutes} min.");
        return (true, $"Tâche « {TaskName} » créée : agent toutes les {everyMinutes} min (première exécution dans {everyMinutes} min). Définition : {file}");
    }

    public static Task<(bool Ok, string Message)> DisableAsync(CancellationToken ct = default) => MaintenanceScheduler.DeleteTaskAsync(TaskName, ct);

    public static Task<(bool Ok, string Message)> RunNowAsync(CancellationToken ct = default) => MaintenanceScheduler.RunNowAsync(TaskName, ct);

    public static Task<MaintenanceStatus> StatusAsync(CancellationToken ct = default) => MaintenanceScheduler.StatusAsync(TaskName, ct);
}
