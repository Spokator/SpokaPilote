using SpokaPilote.App.Localization;
using SpokaPilote.Core.Install;

namespace SpokaPilote.App.ViewModels;

/// <summary>
/// Outils › Maintenance planifiée et agent : tâche Windows d'entretien automatique, agent qui exécute les actions
/// demandées par la console centrale, et état de la connexion à la console.
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    public MaintenanceViewModel(MainViewModel shell)
    {
        Shell = shell;
        EnableScheduleCommand = new RelayCommand(EnableScheduleAsync, () => !Shell.IsBusy);
        DisableScheduleCommand = new RelayCommand(DisableScheduleAsync, () => !Shell.IsBusy && ScheduleExists);
        RunScheduleNowCommand = new RelayCommand(RunScheduleNowAsync, () => !Shell.IsBusy && ScheduleExists);
        EnableAgentCommand = new RelayCommand(EnableAgentAsync, () => !Shell.IsBusy);
        DisableAgentCommand = new RelayCommand(DisableAgentAsync, () => !Shell.IsBusy);
        OpenConsoleCommand = new RelayCommand(() => MainViewModel.OpenPath(Shell.Settings.ServerUrl!), () => IsConsoleConfigured);
        _ = RefreshScheduleStatusAsync();
        _ = RefreshAgentStatusAsync();
    }

    public MainViewModel Shell { get; }

    // ------------------------------------------------------------------ maintenance planifiée

    public string[] ScheduleTriggers { get; } = new[] { "Chaque semaine", "Chaque jour", "Au démarrage du poste" }.Select(Loc.T).ToArray();
    public string[] ScheduleDays { get; } = new[] { "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi", "dimanche" }.Select(Loc.T).ToArray();
    public string[] ScheduleModes { get; } = new[] { "Pilotes manquants seulement", "Pilotes manquants + mises à jour", "Analyse seule (rapport)" }.Select(Loc.T).ToArray();

    private int _scheduleTrigger;
    public int ScheduleTrigger { get => _scheduleTrigger; set { if (Set(ref _scheduleTrigger, value)) OnPropertyChanged(nameof(IsWeekly)); } }
    /// <summary>Le jour n'a de sens que pour une exécution hebdomadaire.</summary>
    public bool IsWeekly => ScheduleTrigger == 0;
    private int _scheduleDay;
    public int ScheduleDay { get => _scheduleDay; set => Set(ref _scheduleDay, value); }
    private string _scheduleTime = "12:30";
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    private int _scheduleMode;
    public int ScheduleMode { get => _scheduleMode; set => Set(ref _scheduleMode, value); }
    private string _scheduleOutputDir = "";
    public string ScheduleOutputDir { get => _scheduleOutputDir; set => Set(ref _scheduleOutputDir, value); }
    private string _scheduleStatus = Loc.T("État : vérification...");
    public string ScheduleStatusText { get => _scheduleStatus; private set => Set(ref _scheduleStatus, value); }
    private bool _scheduleExists;
    public bool ScheduleExists { get => _scheduleExists; private set => Set(ref _scheduleExists, value); }

    public RelayCommand EnableScheduleCommand { get; }
    public RelayCommand DisableScheduleCommand { get; }
    public RelayCommand RunScheduleNowCommand { get; }

    private async Task RefreshScheduleStatusAsync()
    {
        try
        {
            var s = await MaintenanceScheduler.StatusAsync();
            ScheduleExists = s.Exists;
            ScheduleStatusText = !s.Exists
                ? Loc.T("État : maintenance automatique désactivée.")
                : Loc.F("État : active — {0}", s.TriggerSummary) +
                  "\n" + Loc.F("Prochaine exécution : {0} · dernière : {1}",
                      s.NextRun is { } n ? n.ToString("dddd dd/MM/yyyy HH:mm") : "—",
                      s.LastRun is { } l ? Loc.F("{0:dd/MM/yyyy HH:mm} (code {1})", l, s.LastResult) : Loc.T("jamais"));
        }
        catch (Exception ex)
        {
            ScheduleStatusText = Loc.F("État indisponible : {0}", ex.Message);
        }
    }

    private async Task EnableScheduleAsync()
    {
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("La planification de la maintenance")); return; }
        var problem = MaintenanceScheduler.ValidateExecutable(MainViewModel.CliPath);
        if (problem is not null) { ScheduleStatusText = problem; return; }
        var time = MaintenanceScheduler.ParseTime(ScheduleTime);
        if (time is null) { ScheduleStatusText = Loc.T("Heure invalide (format attendu : 12:30)."); return; }
        var options = new MaintenanceOptions
        {
            Trigger = ScheduleTrigger switch { 1 => MaintenanceTrigger.Daily, 2 => MaintenanceTrigger.AtStartup, _ => MaintenanceTrigger.Weekly },
            Day = ScheduleDay == 6 ? DayOfWeek.Sunday : (DayOfWeek)(ScheduleDay + 1),
            Time = time.Value,
            Mode = ScheduleMode switch { 1 => MaintenanceMode.Updates, 2 => MaintenanceMode.Scan, _ => MaintenanceMode.Missing },
            OutputDir = string.IsNullOrWhiteSpace(ScheduleOutputDir) ? null : ScheduleOutputDir.Trim(),
        };
        await Shell.RunBusyAsync(Loc.T("Création de la tâche planifiée..."), async ct =>
        {
            var (_, message) = await MaintenanceScheduler.EnableAsync(options, MainViewModel.CliPath, ct);
            Shell.Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    private async Task DisableScheduleAsync()
    {
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("La désactivation de la maintenance")); return; }
        await Shell.RunBusyAsync(Loc.T("Suppression de la tâche planifiée..."), async ct =>
        {
            var (_, message) = await MaintenanceScheduler.DisableAsync(ct);
            Shell.Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    private async Task RunScheduleNowAsync()
    {
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("Le lancement de la maintenance")); return; }
        await Shell.RunBusyAsync(Loc.T("Lancement de la maintenance..."), async ct =>
        {
            var (_, message) = await MaintenanceScheduler.RunNowAsync(ct);
            Shell.Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    // ------------------------------------------------------------------ agent (actions demandées par la console)

    private string _agentStatus = "";
    public string AgentStatusText { get => _agentStatus; private set => Set(ref _agentStatus, value); }
    public RelayCommand EnableAgentCommand { get; }
    public RelayCommand DisableAgentCommand { get; }

    private async Task RefreshAgentStatusAsync()
    {
        try
        {
            var s = await AgentScheduler.StatusAsync();
            AgentStatusText = s.Exists
                ? Loc.F("Agent actif ({0}) · dernière vérification : {1}", s.TriggerSummary ?? "",
                    s.LastRun is { } l ? l.ToString("dd/MM/yyyy HH:mm") : Loc.T("jamais"))
                : Loc.T("Agent désactivé : la console ne peut pas lancer d'action sur ce poste.");
        }
        catch (Exception ex)
        {
            AgentStatusText = ex.Message;
        }
    }

    private async Task EnableAgentAsync()
    {
        if (!IsConsoleConfigured) { AgentStatusText = Loc.T("Renseignez d'abord l'adresse et le jeton de la console dans Paramètres, puis Enregistrer."); return; }
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("L'activation de l'agent")); return; }
        var problem = MaintenanceScheduler.ValidateExecutable(MainViewModel.CliPath);
        if (problem is not null) { AgentStatusText = problem; return; }
        Shell.SaveSettings();
        await Shell.RunBusyAsync(Loc.T("Activation de l'agent..."), async ct =>
        {
            var (_, message) = await AgentScheduler.EnableAsync(AgentScheduler.DefaultIntervalMinutes, MainViewModel.CliPath, ct);
            Shell.Status = message;
            await RefreshAgentStatusAsync();
        });
    }

    private async Task DisableAgentAsync()
    {
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("La désactivation de l'agent")); return; }
        await Shell.RunBusyAsync(Loc.T("Désactivation de l'agent..."), async ct =>
        {
            var (_, message) = await AgentScheduler.DisableAsync(ct);
            Shell.Status = message;
            await RefreshAgentStatusAsync();
        });
    }

    // ------------------------------------------------------------------ console centrale (état)

    public bool IsConsoleConfigured => Core.Online.ServerClient.IsConfigured(Shell.Settings);
    public string ConsoleStatusText => IsConsoleConfigured
        ? Loc.F("Ce poste envoie ses rapports à {0}.", Shell.Settings.ServerUrl)
        : Loc.T("Aucune console configurée : ce poste ne remonte rien. L'adresse et le jeton se règlent dans Paramètres.");
    public RelayCommand OpenConsoleCommand { get; }

    /// <summary>À appeler après l'enregistrement des paramètres (adresse de la console modifiée).</summary>
    public void RefreshConsole()
    {
        OnPropertyChanged(nameof(IsConsoleConfigured));
        OnPropertyChanged(nameof(ConsoleStatusText));
    }
}
