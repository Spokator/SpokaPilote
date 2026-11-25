using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Printers;
using SpokaPilote.Core.Repository;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        Settings = Settings.Load();
        IsAdmin = Elevation.IsAdministrator();
        foreach (var p in Settings.RepositoryPaths) RepositoryPaths.Add(p);
        _createRestorePoint = Settings.CreateRestorePoint;
        _backupDrivers = Settings.BackupDriversBeforeInstall;

        Log.Subscribe(entry => _dispatcher.BeginInvoke(() =>
        {
            LogLines.Add($"{entry.Timestamp:HH:mm:ss}  {Prefix(entry.Level)}{entry.Message}");
            while (LogLines.Count > 2000) LogLines.RemoveAt(0);
        }));

        ScanCommand = new RelayCommand(ScanAsync, () => !IsBusy);
        InstallCommand = new RelayCommand(() => InstallAsync(dryRun: false), () => !IsBusy && Candidates.Any(c => c.IsSelected));
        SimulateCommand = new RelayCommand(() => InstallAsync(dryRun: true), () => !IsBusy && Candidates.Any(c => c.IsSelected));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectAllCommand = new RelayCommand(() => { foreach (var c in Candidates) c.IsSelected = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var c in Candidates) c.IsSelected = false; });
        ExportHtmlCommand = new RelayCommand(ExportReport, () => LastScan is not null);
        RelaunchAsAdminCommand = new RelayCommand(RelaunchAsAdmin, () => !IsAdmin);
        DiscoverPrintersCommand = new RelayCommand(DiscoverPrintersAsync, () => !IsBusy);
        InstallPrinterCommand = new RelayCommand(InstallPrinterAsync, () => !IsBusy && SelectedPrinter is not null && SelectedPrinterDriver is not null);
        FindPrinterDriverCommand = new RelayCommand(FindPrinterDriverByModelAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ManualPrinterModel));
        AddRepositoryCommand = new RelayCommand(AddRepository);
        RemoveRepositoryCommand = new RelayCommand(RemoveRepository, () => SelectedRepository is not null);
        ExportDriversCommand = new RelayCommand(ExportDriversAsync, () => !IsBusy);
        ImportDriversCommand = new RelayCommand(ImportDriversAsync, () => !IsBusy && RepositoryPaths.Count > 0);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        OpenLogsCommand = new RelayCommand(() => OpenPath(AppPaths.Logs));
        OpenUrlCommand = new RelayCommand(p => { if (p is string url) OpenPath(url); return Task.CompletedTask; });

        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync, () => !IsBusy && AvailableUpdate is not null);
        RollbackCommand = new RelayCommand(RollbackAsync, () => !IsBusy && SelectedIntervention?.CanRollback == true);
        RestoreBackupCommand = new RelayCommand(RestoreBackupAsync, () => !IsBusy && SelectedBackup is not null);
        RefreshHistoryCommand = new RelayCommand(RefreshHistory);
        BuildPackCommand = new RelayCommand(BuildPackAsync, () => !IsBusy);
        FleetReportCommand = new RelayCommand(BuildFleetReport, () => !IsBusy);
        ExportPrinterProfileCommand = new RelayCommand(ExportPrinterProfile, () => !IsBusy);
        DeployPrinterProfileCommand = new RelayCommand(DeployPrinterProfileAsync, () => !IsBusy);
        EnableScheduleCommand = new RelayCommand(EnableScheduleAsync, () => !IsBusy);
        DisableScheduleCommand = new RelayCommand(DisableScheduleAsync, () => !IsBusy && ScheduleExists);
        RunScheduleNowCommand = new RelayCommand(RunScheduleNowAsync, () => !IsBusy && ScheduleExists);
        RefreshHistory();
        _ = RefreshScheduleStatusAsync();
    }

    // ------------------------------------------------------------------ politique de pilotes

    public string PolicySummary => LastScan?.Policy is { } p
        ? p.Summary + (p.Messages.Count > 0 ? "\n" + string.Join("\n", p.Messages) : "")
        : "Politique : chargée à la prochaine analyse.";
    public int BlockedCount => LastScan?.Candidates.Count(c => c.BlockedReason is not null) ?? 0;

    // ------------------------------------------------------------------ profils d'imprimantes

    private bool _printerDeployDryRun = true;
    public bool PrinterDeployDryRun { get => _printerDeployDryRun; set => Set(ref _printerDeployDryRun, value); }
    public RelayCommand ExportPrinterProfileCommand { get; }
    public RelayCommand DeployPrinterProfileCommand { get; }

    private Task ExportPrinterProfile()
    {
        var dialog = new SaveFileDialog { FileName = $"imprimantes-{Environment.MachineName}.json", Filter = "Profil d'imprimantes (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var (profile, skipped) = PrinterProfileDeployer.Export(PrinterInventory.Read(), Environment.MachineName);
        File.WriteAllText(dialog.FileName, profile.ToJson());
        PrinterResult = $"{profile.Printers.Count} imprimante(s) réseau exportée(s) dans {dialog.FileName}" +
                        (skipped.Count > 0 ? $"\nIgnorées : {string.Join(", ", skipped.Select(s => s.Name))}" : "") +
                        "\nAjoutez au besoin des conditions par site (sous-réseaux, noms de postes) : voir docs/IMPRIMANTES-ET-PLANIFICATION.md.";
        Status = $"Profil d'imprimantes exporté ({profile.Printers.Count}).";
        return Task.CompletedTask;
    }

    private async Task DeployPrinterProfileAsync()
    {
        string? source;
        var dialog = new OpenFileDialog { Title = "Profil d'imprimantes à appliquer (Annuler = profil de la console centrale)", Filter = "Profil d'imprimantes (*.json)|*.json" };
        if (dialog.ShowDialog() == true) source = dialog.FileName;
        else if (!string.IsNullOrWhiteSpace(Settings.ServerUrl)) source = null;
        else return;

        if (!PrinterDeployDryRun && !IsAdmin) { AskRelaunch("Le déploiement d'imprimantes"); return; }
        await RunBusyAsync("Déploiement du profil d'imprimantes...", async ct =>
        {
            var (profile, origin) = await PrinterProfileSource.LoadAsync(source, Settings, ct);
            if (profile is null)
            {
                PrinterResult = $"Aucun profil d'imprimantes publié ({origin}).";
                return;
            }
            var validation = profile.Validate();
            if (!validation.IsValid)
            {
                PrinterResult = "Profil invalide, aucun changement :\n" + string.Join("\n", validation.Errors.Select(e => "✗ " + e));
                return;
            }
            var report = await new PrinterProfileDeployer(Settings).DeployAsync(profile, PrinterProfileContext.Current(), PrinterDeployDryRun,
                new Progress<string>(m => Status = m), ct);
            PrinterResult = $"{(PrinterDeployDryRun ? "SIMULATION — " : "")}{profile.Name ?? "Profil"} ({origin}) :\n" +
                            string.Join("\n", report.Items.Select(i => $"{i.StatusLabel} · {i.Name}{(string.IsNullOrEmpty(i.Message) ? "" : " — " + i.Message)}")) +
                            (report.Warnings.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", report.Warnings) : "");
            Status = report.HasFailures ? "Profil appliqué avec des échecs." : "Profil d'imprimantes appliqué.";
        });
    }

    // ------------------------------------------------------------------ maintenance planifiée

    public string[] ScheduleTriggers { get; } = { "Chaque semaine", "Chaque jour", "Au démarrage du poste" };
    public string[] ScheduleDays { get; } = { "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi", "dimanche" };
    public string[] ScheduleModes { get; } = { "Pilotes manquants seulement", "Pilotes manquants + mises à jour", "Analyse seule (rapport)" };

    private int _scheduleTrigger;
    public int ScheduleTrigger { get => _scheduleTrigger; set => Set(ref _scheduleTrigger, value); }
    private int _scheduleDay;
    public int ScheduleDay { get => _scheduleDay; set => Set(ref _scheduleDay, value); }
    private string _scheduleTime = "12:30";
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    private int _scheduleMode;
    public int ScheduleMode { get => _scheduleMode; set => Set(ref _scheduleMode, value); }
    private string _scheduleOutputDir = "";
    public string ScheduleOutputDir { get => _scheduleOutputDir; set => Set(ref _scheduleOutputDir, value); }
    private string _scheduleStatus = "État : vérification...";
    public string ScheduleStatusText { get => _scheduleStatus; private set => Set(ref _scheduleStatus, value); }
    private bool _scheduleExists;
    public bool ScheduleExists { get => _scheduleExists; private set => Set(ref _scheduleExists, value); }

    public RelayCommand EnableScheduleCommand { get; }
    public RelayCommand DisableScheduleCommand { get; }
    public RelayCommand RunScheduleNowCommand { get; }

    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "spokapilote.exe");

    private async Task RefreshScheduleStatusAsync()
    {
        try
        {
            var s = await MaintenanceScheduler.StatusAsync();
            ScheduleExists = s.Exists;
            ScheduleStatusText = !s.Exists
                ? "État : maintenance automatique désactivée."
                : $"État : active — {s.TriggerSummary}" +
                  $"\nProchaine exécution : {(s.NextRun is { } n ? n.ToString("dddd dd/MM/yyyy HH:mm") : "—")}" +
                  $" · dernière : {(s.LastRun is { } l ? $"{l:dd/MM/yyyy HH:mm} (code {s.LastResult})" : "jamais")}";
        }
        catch (Exception ex)
        {
            ScheduleStatusText = $"État indisponible : {ex.Message}";
        }
    }

    private async Task EnableScheduleAsync()
    {
        if (!IsAdmin) { AskRelaunch("La planification de la maintenance"); return; }
        var problem = MaintenanceScheduler.ValidateExecutable(CliPath);
        if (problem is not null) { ScheduleStatusText = problem; return; }
        var time = MaintenanceScheduler.ParseTime(ScheduleTime);
        if (time is null) { ScheduleStatusText = "Heure invalide (format attendu : 12:30)."; return; }
        var options = new MaintenanceOptions
        {
            Trigger = ScheduleTrigger switch { 1 => MaintenanceTrigger.Daily, 2 => MaintenanceTrigger.AtStartup, _ => MaintenanceTrigger.Weekly },
            Day = ScheduleDay == 6 ? DayOfWeek.Sunday : (DayOfWeek)(ScheduleDay + 1),
            Time = time.Value,
            Mode = ScheduleMode switch { 1 => MaintenanceMode.Updates, 2 => MaintenanceMode.Scan, _ => MaintenanceMode.Missing },
            OutputDir = string.IsNullOrWhiteSpace(ScheduleOutputDir) ? null : ScheduleOutputDir.Trim(),
        };
        await RunBusyAsync("Création de la tâche planifiée...", async ct =>
        {
            var (_, message) = await MaintenanceScheduler.EnableAsync(options, CliPath, ct);
            Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    private async Task DisableScheduleAsync()
    {
        if (!IsAdmin) { AskRelaunch("La désactivation de la maintenance"); return; }
        await RunBusyAsync("Suppression de la tâche planifiée...", async ct =>
        {
            var (_, message) = await MaintenanceScheduler.DisableAsync(ct);
            Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    private async Task RunScheduleNowAsync()
    {
        if (!IsAdmin) { AskRelaunch("Le lancement de la maintenance"); return; }
        await RunBusyAsync("Lancement de la maintenance...", async ct =>
        {
            var (_, message) = await MaintenanceScheduler.RunNowAsync(ct);
            Status = message;
            await RefreshScheduleStatusAsync();
        });
    }

    // ------------------------------------------------------------------ mise à jour de SpokaPilote

    public string AppVersionLabel => $"SpokaPilote {Core.Online.UpdateChecker.CurrentVersion.ToString(3)}";

    private Core.Online.UpdateInfo? _availableUpdate;
    public Core.Online.UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set { Set(ref _availableUpdate, value); OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(UpdateLabel)); }
    }
    public bool HasUpdate => AvailableUpdate is not null;
    public string UpdateLabel => AvailableUpdate is null ? "" : $"SpokaPilote {AvailableUpdate.Version.ToString(3)} est disponible.";
    public RelayCommand InstallUpdateCommand { get; }

    public async Task CheckForUpdateAsync()
    {
        try
        {
            AvailableUpdate = await Core.Online.UpdateChecker.CheckAsync(Core.Online.UpdateChecker.CurrentVersion);
        }
        catch (Exception ex)
        {
            Log.Debug($"Vérification de mise à jour impossible : {ex.Message}");
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is null) return;
        await RunBusyAsync($"Téléchargement de SpokaPilote {AvailableUpdate.Version.ToString(3)}...", async ct =>
        {
            var file = await Core.Online.UpdateChecker.DownloadAsync(AvailableUpdate,
                new Progress<double>(p => { ProgressIndeterminate = false; Progress = p; }), ct);
            Status = "Installation de la mise à jour...";
            Core.Online.UpdateChecker.LaunchInstaller(file, relaunch: true);
            Application.Current.Shutdown();
        });
    }

    // ------------------------------------------------------------------ historique et retour arrière

    public ObservableCollection<Intervention> Interventions { get; } = new();
    public ObservableCollection<string> Backups { get; } = new();

    private Intervention? _selectedIntervention;
    public Intervention? SelectedIntervention
    {
        get => _selectedIntervention;
        set { Set(ref _selectedIntervention, value); OnPropertyChanged(nameof(SelectedInterventionDetails)); }
    }
    public string SelectedInterventionDetails => SelectedIntervention is null ? "Sélectionnez une intervention."
        : string.Join("\n", SelectedIntervention.Items.Select(i => $"{StatusIcon(i.Status)} {i.Title} {i.Version} — {i.Message}"))
          + (SelectedIntervention.AddedDriverPackages.Count > 0 ? $"\n\nPaquets ajoutés : {string.Join(", ", SelectedIntervention.AddedDriverPackages)}" : "")
          + (SelectedIntervention.BackupFolder is null ? "" : $"\nSauvegarde : {SelectedIntervention.BackupFolder}")
          + (SelectedIntervention.RestorePointCreated ? "\nPoint de restauration créé." : "");

    private string? _selectedBackup;
    public string? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    public RelayCommand RollbackCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }

    private static string StatusIcon(string status) => status switch
    {
        "Installed" => "✓",
        "NotApplicable" => "=",
        "Skipped" => "·",
        _ => "✗",
    };

    private void RefreshHistory()
    {
        Interventions.Clear();
        foreach (var i in InterventionHistory.List()) Interventions.Add(i);
        Backups.Clear();
        try { foreach (var b in InterventionHistory.Backups()) Backups.Add(b); } catch { /* aucun dossier */ }
        SelectedIntervention = Interventions.FirstOrDefault();
        SelectedBackup = Backups.FirstOrDefault();
    }

    private async Task RollbackAsync()
    {
        if (SelectedIntervention is null) return;
        if (!IsAdmin) { AskRelaunch("L'annulation d'une intervention"); return; }
        if (MessageBox.Show($"Annuler l'intervention du {SelectedIntervention.Timestamp:dd/MM/yyyy HH:mm} ?\n\n" +
                            $"Les paquets {string.Join(", ", SelectedIntervention.AddedDriverPackages)} seront retirés et les périphériques " +
                            "reviendront à leur pilote précédent.", "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var target = SelectedIntervention;
        await RunBusyAsync("Annulation de l'intervention...", async ct =>
        {
            var (_, message) = await InterventionHistory.RollbackAsync(target, new Progress<string>(m => Status = m), ct);
            Status = message;
            RefreshHistory();
        });
    }

    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is null) return;
        if (!IsAdmin) { AskRelaunch("La restauration d'une sauvegarde"); return; }
        var folder = SelectedBackup;
        await RunBusyAsync("Restauration de la sauvegarde...", async ct =>
        {
            var (_, message) = await InterventionHistory.RestoreBackupAsync(folder, ct);
            Status = message;
        });
    }

    // ------------------------------------------------------------------ pack hors ligne et parc

    private bool _packAllHardware;
    public bool PackAllHardware { get => _packAllHardware; set => Set(ref _packAllHardware, value); }
    public RelayCommand BuildPackCommand { get; }
    public RelayCommand FleetReportCommand { get; }

    private async Task BuildPackAsync()
    {
        var open = new OpenFileDialog
        {
            Title = "Rapport JSON du poste sans réseau (spokapilote scan --json), ou Annuler pour ce poste",
            Filter = "Rapport SpokaPilote (*.json)|*.json",
        };
        var reportPath = open.ShowDialog() == true ? open.FileName : null;
        var dest = new OpenFolderDialog { Title = "Dossier du pack (ex. clé USB)" };
        if (dest.ShowDialog() != true) return;

        await RunBusyAsync("Préparation du pack hors ligne...", async ct =>
        {
            SystemInfo system;
            IReadOnlyList<DeviceInfo> devices;
            if (reportPath is not null)
            {
                var report = ScanReport.Load(reportPath);
                (system, devices) = (report.System, report.Devices);
            }
            else if (LastScan is not null)
            {
                (system, devices) = (LastScan.System, LastScan.Devices);
            }
            else
            {
                system = await Task.Run(Core.Inventory.SystemInfoProvider.Collect, ct);
                devices = await Task.Run(Core.Inventory.DeviceInventory.Collect, ct);
            }
            var result = await OfflinePackBuilder.BuildAsync(system, devices, dest.FolderName, PackAllHardware, Settings,
                new Progress<string>(m => Status = m), ct);
            RepositoryResult = $"{result.Summary}\nSur le poste cible : spokapilote install --repo \"{dest.FolderName}\" --yes " +
                               "(ou ajoutez ce dossier comme dépôt dans l'interface).";
            Status = result.Summary;
            OpenPath(dest.FolderName);
        });
    }

    private Task BuildFleetReport()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier contenant les rapports JSON des postes (ex. \\\\serveur\\rapports)" };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var machines = FleetReport.Load(dialog.FolderName);
        if (machines.Count == 0)
        {
            RepositoryResult = "Aucun rapport SpokaPilote dans ce dossier. Sur chaque poste : spokapilote install --yes --output-dir \\\\serveur\\rapports";
            return Task.CompletedTask;
        }
        var output = Path.Combine(dialog.FolderName, "parc.html");
        try
        {
            File.WriteAllText(output, FleetReport.ToHtml(machines, dialog.FolderName));
        }
        catch (UnauthorizedAccessException)
        {
            output = Path.Combine(AppPaths.Reports, "parc.html");
            File.WriteAllText(output, FleetReport.ToHtml(machines, dialog.FolderName));
        }
        RepositoryResult = $"{machines.Count} poste(s), {machines.Count(m => !m.Healthy)} à traiter : {output}";
        OpenPath(output);
        return Task.CompletedTask;
    }

    private void AskRelaunch(string action)
    {
        if (MessageBox.Show($"{action} nécessite les droits administrateur.\n\nRelancer SpokaPilote en administrateur ?",
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            RelaunchAsAdmin();
    }

    public Settings Settings { get; }
    public bool IsAdmin { get; }
    public bool IsNotAdmin => !IsAdmin;

    // ------------------------------------------------------------------ état global

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;

    private string _status = "Prêt. Lancez une analyse pour inventorier ce poste.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }

    public ObservableCollection<string> LogLines { get; } = new();

    // ------------------------------------------------------------------ machine et analyse

    private ScanResult? _lastScan;
    public ScanResult? LastScan
    {
        get => _lastScan;
        private set
        {
            Set(ref _lastScan, value);
            OnPropertyChanged(nameof(HasScan));
            OnPropertyChanged(nameof(MachineTitle));
            OnPropertyChanged(nameof(MachineDetails));
            OnPropertyChanged(nameof(SummaryProblems));
            OnPropertyChanged(nameof(SummaryCandidates));
            OnPropertyChanged(nameof(SummaryDevices));
            OnPropertyChanged(nameof(SummaryUnresolved));
            OnPropertyChanged(nameof(SourceWarnings));
            OnPropertyChanged(nameof(HasSourceWarnings));
            OnPropertyChanged(nameof(HealthHeadline));
            OnPropertyChanged(nameof(PolicySummary));
            OnPropertyChanged(nameof(BlockedCount));
        }
    }

    public bool HasScan => LastScan is not null;
    public string MachineTitle => LastScan is null ? Environment.MachineName : $"{LastScan.System.ComputerName} — {LastScan.System.Manufacturer} {LastScan.System.DisplayModel}";
    public string MachineDetails
    {
        get
        {
            if (LastScan is null) return "Machine non analysée";
            var s = LastScan.System;
            var parts = new List<string> { $"{s.OsCaption} {s.OsDisplayVersion} ({s.OsVersion}, {s.Architecture})" };
            if (s.SerialNumber is not null) parts.Add($"N° de série {s.SerialNumber}");
            if (s.BiosVersion is not null) parts.Add($"BIOS {s.BiosVersion}");
            return string.Join("  ·  ", parts);
        }
    }

    public string SummaryDevices => LastScan is null ? "—" : LastScan.Devices.Count(d => d.IsPresent).ToString();
    public string SummaryProblems => LastScan is null ? "—" : LastScan.ProblemDevices.Count().ToString();
    public string SummaryCandidates => LastScan is null ? "—" : LastScan.Candidates.Count.ToString();
    public string SummaryUnresolved => LastScan is null ? "—" : LastScan.UnresolvedDevices.Count().ToString();
    public string SourceWarnings => LastScan is null ? "" : string.Join("\n", LastScan.SourceErrors.Select(e => $"{e.Key} : {e.Value}"));
    public bool HasSourceWarnings => LastScan?.SourceErrors.Count > 0;

    public string HealthHeadline
    {
        get
        {
            if (LastScan is null) return "Analysez ce poste pour savoir ce qui manque.";
            var missing = LastScan.Candidates.Count(c => c.Kind == CandidateKind.MissingDriver);
            var updates = LastScan.Candidates.Count(c => c.Kind == CandidateKind.Update);
            if (missing == 0 && updates == 0 && !LastScan.UnresolvedDevices.Any()) return "Tout est en ordre : aucun pilote manquant.";
            var parts = new List<string>();
            if (missing > 0) parts.Add($"{missing} pilote(s) manquant(s)");
            if (updates > 0) parts.Add($"{updates} mise(s) à jour");
            if (LastScan.UnresolvedDevices.Any()) parts.Add($"{LastScan.UnresolvedDevices.Count()} périphérique(s) sans solution automatique");
            return string.Join(", ", parts) + ".";
        }
    }

    private bool _deepScan;
    public bool DeepScan { get => _deepScan; set => Set(ref _deepScan, value); }

    private bool _showAllDevices;
    public bool ShowAllDevices { get => _showAllDevices; set { if (Set(ref _showAllDevices, value)) RefreshDevices(); } }

    public ObservableCollection<CandidateItem> Candidates { get; } = new();
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<DeviceItem> UnresolvedDevices { get; } = new();

    private bool _createRestorePoint;
    public bool CreateRestorePoint { get => _createRestorePoint; set => Set(ref _createRestorePoint, value); }
    private bool _backupDrivers;
    public bool BackupDrivers { get => _backupDrivers; set => Set(ref _backupDrivers, value); }

    private bool _rebootPending;
    public bool RebootPending { get => _rebootPending; private set => Set(ref _rebootPending, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand SimulateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand RelaunchAsAdminCommand { get; }

    private async Task ScanAsync()
    {
        await RunBusyAsync("Analyse en cours...", async ct =>
        {
            var engine = new DriverScanEngine(Settings);
            var scan = await engine.ScanAsync(new ScanOptions { DeepScan = DeepScan },
                new Progress<ScanProgress>(p =>
                {
                    Status = p.Message;
                    if (p.Percent is { } pct) { ProgressIndeterminate = false; Progress = pct; }
                    else ProgressIndeterminate = true;
                }), ct);

            LastScan = scan;
            Candidates.Clear();
            foreach (var c in scan.Candidates) Candidates.Add(new CandidateItem(c));
            RefreshDevices();
            Status = $"Analyse terminée en {scan.Duration.TotalSeconds:0} s — {HealthHeadline}";
        });
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        UnresolvedDevices.Clear();
        if (LastScan is null) return;
        foreach (var d in LastScan.Devices.Where(d => d.IsPresent && (ShowAllDevices || d.IsHardware || d.HasProblem)))
            Devices.Add(new DeviceItem(d));
        foreach (var d in LastScan.UnresolvedDevices) UnresolvedDevices.Add(new DeviceItem(d));
    }

    private async Task InstallAsync(bool dryRun)
    {
        if (LastScan is null) return;
        if (!dryRun && !IsAdmin)
        {
            if (MessageBox.Show("L'installation de pilotes nécessite les droits administrateur.\n\nRelancer SpokaPilote en administrateur ?",
                    "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                RelaunchAsAdmin();
            return;
        }

        var selected = Candidates.Where(c => c.IsSelected).ToList();
        if (!dryRun && selected.Any(c => c.IsFirmware) &&
            MessageBox.Show("La sélection contient une mise à jour de BIOS/firmware.\nBranchez le secteur et ne coupez pas l'alimentation pendant l'opération.\n\nContinuer ?",
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var c in selected) { c.Status = "En attente"; c.StatusDetail = ""; }
        await RunBusyAsync(dryRun ? "Simulation..." : "Installation...", async ct =>
        {
            var installer = new DriverInstaller(Settings, LastScan.System);
            var outcomes = await installer.InstallAsync(selected.Select(s => s.Candidate).ToList(), LastScan.Devices,
                new InstallOptions { DryRun = dryRun, CreateRestorePoint = CreateRestorePoint, BackupDrivers = BackupDrivers },
                new Progress<InstallProgress>(p =>
                {
                    Status = $"[{p.Index}/{p.Total}] {p.Title} — {p.Step}";
                    ProgressIndeterminate = false;
                    Progress = p.Total == 0 ? 0 : 100.0 * Math.Max(0, p.Index - 1) / p.Total;
                    var item = p.Index >= 1 && p.Index <= selected.Count ? selected[p.Index - 1] : null;
                    if (item is not null) item.Status = p.Step;
                }), ct);

            foreach (var o in outcomes)
                selected.FirstOrDefault(s => s.Candidate == o.Candidate)?.Apply(o);
            if (!dryRun) RefreshHistory();

            var ok = outcomes.Count(o => o.Succeeded);
            var failed = outcomes.Count(o => o.Status is InstallStatus.Failed or InstallStatus.Blocked);
            RebootPending |= outcomes.Any(o => o.RebootRequired);
            Status = dryRun
                ? $"Simulation terminée : {outcomes.Count(o => o.Status == InstallStatus.Skipped)} paquet(s) téléchargé(s) et vérifié(s), {failed} problème(s)."
                : $"Installation terminée : {ok} réussi(s), {failed} échec(s).{(RebootPending ? " Redémarrage nécessaire." : "")}";
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.Reports, $"{LastScan.System.ComputerName}-{DateTime.Now:yyyyMMdd-HHmmss}.html"),
                    ReportWriter.ToHtml(LastScan, outcomes));
            }
            catch { /* historique facultatif */ }
        });
    }

    private Task ExportReport()
    {
        if (LastScan is null) return Task.CompletedTask;
        var dialog = new SaveFileDialog
        {
            FileName = $"SpokaPilote-{LastScan.System.ComputerName}-{DateTime.Now:yyyyMMdd}.html",
            Filter = "Rapport HTML (*.html)|*.html|Rapport JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var content = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ReportWriter.ToJson(LastScan)
            : ReportWriter.ToHtml(LastScan);
        File.WriteAllText(dialog.FileName, content);
        OpenPath(dialog.FileName);
        return Task.CompletedTask;
    }

    private void RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // L'utilisateur a refusé l'élévation UAC.
        }
    }

    // ------------------------------------------------------------------ imprimantes

    private string _printerRange = "";
    public string PrinterRange { get => _printerRange; set => Set(ref _printerRange, value); }

    public ObservableCollection<PrinterItem> Printers { get; } = new();
    public ObservableCollection<PrinterDriverOption> PrinterDriverOptions { get; } = new();

    private PrinterItem? _selectedPrinter;
    public PrinterItem? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (!Set(ref _selectedPrinter, value)) return;
            OnPropertyChanged(nameof(HasSelectedPrinter));
            PrinterName = value is null ? "" : PrinterInstaller.DefaultName(value.Printer);
            PrinterLocation = value?.Printer.Location ?? "";
            _ = ResolvePrinterDriversAsync(value);
        }
    }
    public bool HasSelectedPrinter => SelectedPrinter is not null;

    private PrinterDriverOption? _selectedPrinterDriver;
    public PrinterDriverOption? SelectedPrinterDriver { get => _selectedPrinterDriver; set => Set(ref _selectedPrinterDriver, value); }

    private string _printerName = "";
    public string PrinterName { get => _printerName; set => Set(ref _printerName, value); }
    private string _printerLocation = "";
    public string PrinterLocation { get => _printerLocation; set => Set(ref _printerLocation, value); }
    private bool _printerSetDefault;
    public bool PrinterSetDefault { get => _printerSetDefault; set => Set(ref _printerSetDefault, value); }
    private string _manualPrinterModel = "";
    public string ManualPrinterModel { get => _manualPrinterModel; set => Set(ref _manualPrinterModel, value); }
    private string _printerResult = "";
    public string PrinterResult { get => _printerResult; set => Set(ref _printerResult, value); }

    public RelayCommand DiscoverPrintersCommand { get; }
    public RelayCommand InstallPrinterCommand { get; }
    public RelayCommand FindPrinterDriverCommand { get; }

    private async Task DiscoverPrintersAsync()
    {
        await RunBusyAsync("Recherche des imprimantes réseau...", async ct =>
        {
            await PrinterVendorCatalog.LoadOrganizationCatalogAsync(Settings, ct);
            Printers.Clear();
            var targets = string.IsNullOrWhiteSpace(PrinterRange) ? null : PrinterDiscovery.ParseTargets(PrinterRange);
            var found = await PrinterDiscovery.DiscoverAsync(targets, new Progress<string>(m => Status = m), ct);
            foreach (var p in found) Printers.Add(new PrinterItem(p));
            Status = found.Count == 0
                ? "Aucune imprimante trouvée. Indiquez une plage (ex. 10.0.1.0/24) si les imprimantes sont sur un autre réseau."
                : $"{found.Count} imprimante(s) trouvée(s). Sélectionnez-en une pour choisir le pilote.";
        });
    }

    private async Task FindPrinterDriverByModelAsync()
    {
        var model = ManualPrinterModel.Trim();
        var printer = new DiscoveredPrinter { Address = "0.0.0.0", Model = model, Manufacturer = PrinterVendorCatalog.FindVendor(model)?.Name };
        await RunBusyAsync($"Recherche d'un pilote pour « {model} »...", async ct =>
        {
            var options = await PrinterDriverResolver.ResolveAsync(printer, Settings, ct);
            PrinterDriverOptions.Clear();
            foreach (var o in options) PrinterDriverOptions.Add(o);
            PrinterResult = options.Count == 0 ? "Aucune piste trouvée pour ce modèle."
                : string.Join("\n", options.Select(o => $"• {o}"));
            Status = $"{options.Count} piste(s) pour « {model} ».";
        });
    }

    private async Task ResolvePrinterDriversAsync(PrinterItem? item)
    {
        PrinterDriverOptions.Clear();
        SelectedPrinterDriver = null;
        PrinterResult = "";
        if (item is null) return;
        await RunBusyAsync("Recherche du meilleur pilote...", async ct =>
        {
            var options = await PrinterDriverResolver.ResolveAsync(item.Printer, Settings, ct);
            foreach (var o in options) PrinterDriverOptions.Add(o);
            SelectedPrinterDriver = options.FirstOrDefault(o => o.Kind != PrinterDriverOptionKind.VendorPage);
            item.DriverSummary = SelectedPrinterDriver?.ToString() ?? "Aucun pilote automatique";
            Status = $"{options.Count} pilote(s) possible(s) pour {item.Name}.";
        });
    }

    private async Task InstallPrinterAsync()
    {
        if (SelectedPrinter is null || SelectedPrinterDriver is null) return;
        if (!IsAdmin)
        {
            if (MessageBox.Show("L'ajout d'une imprimante nécessite les droits administrateur.\n\nRelancer SpokaPilote en administrateur ?",
                    "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                RelaunchAsAdmin();
            return;
        }
        if (SelectedPrinterDriver.Kind == PrinterDriverOptionKind.VendorPage)
        {
            OpenPath(SelectedPrinterDriver.Url!);
            return;
        }
        await RunBusyAsync("Installation de l'imprimante...", async ct =>
        {
            var result = await new PrinterInstaller(Settings).InstallAsync(new PrinterInstallRequest
            {
                Printer = SelectedPrinter.Printer,
                Driver = SelectedPrinterDriver,
                PrinterName = PrinterName,
                Location = PrinterLocation,
                SetAsDefault = PrinterSetDefault,
            }, new Progress<string>(m => Status = m), ct);
            PrinterResult = result.Message;
            Status = result.Message;
        });
    }

    // ------------------------------------------------------------------ dépôt

    public ObservableCollection<string> RepositoryPaths { get; } = new();
    private string? _selectedRepository;
    public string? SelectedRepository { get => _selectedRepository; set => Set(ref _selectedRepository, value); }
    private string _repositoryResult = "";
    public string RepositoryResult { get => _repositoryResult; set => Set(ref _repositoryResult, value); }

    public RelayCommand AddRepositoryCommand { get; }
    public RelayCommand RemoveRepositoryCommand { get; }
    public RelayCommand ExportDriversCommand { get; }
    public RelayCommand ImportDriversCommand { get; }

    private void AddRepository()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier ou partage réseau contenant des pilotes (.inf)" };
        if (dialog.ShowDialog() != true) return;
        if (!RepositoryPaths.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) RepositoryPaths.Add(dialog.FolderName);
        SaveSettings();
    }

    private void RemoveRepository()
    {
        if (SelectedRepository is null) return;
        RepositoryPaths.Remove(SelectedRepository);
        SaveSettings();
    }

    private async Task ExportDriversAsync()
    {
        if (!IsAdmin)
        {
            MessageBox.Show("L'export des pilotes nécessite les droits administrateur.", "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Dépôt de destination (ex. \\\\serveur\\pilotes)" };
        if (SelectedRepository is not null) dialog.InitialDirectory = SelectedRepository;
        if (dialog.ShowDialog() != true) return;
        await RunBusyAsync("Export des pilotes de ce poste...", async ct =>
        {
            var (_, _, message) = await DriverExporter.ExportAsync(dialog.FolderName, ct);
            RepositoryResult = message;
            Status = message;
        });
    }

    private async Task ImportDriversAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pilote téléchargé (archive .zip/.cab ou fichier .inf d'un dossier extrait)",
            Filter = "Pilotes (*.zip;*.cab;*.inf)|*.zip;*.cab;*.inf",
        };
        if (dialog.ShowDialog() != true) return;
        var source = dialog.FileName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dialog.FileName)! : dialog.FileName;
        var repo = SelectedRepository ?? RepositoryPaths.First();
        await RunBusyAsync("Import dans le dépôt...", async ct =>
        {
            var (_, message) = await DriverExporter.ImportAsync(source, repo, ct);
            RepositoryResult = message;
            Status = message;
        });
    }

    // ------------------------------------------------------------------ paramètres

    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenUrlCommand { get; }

    private void SaveSettings()
    {
        Settings.RepositoryPaths = RepositoryPaths.ToList();
        Settings.CreateRestorePoint = CreateRestorePoint;
        Settings.BackupDriversBeforeInstall = BackupDrivers;
        Settings.Save();
        Status = $"Paramètres enregistrés ({AppPaths.SettingsFile}).";
    }

    // ------------------------------------------------------------------ utilitaires

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = message;
        ProgressIndeterminate = true;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Opération annulée.";
        }
        catch (Exception ex)
        {
            Log.Error("Erreur", ex);
            Status = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Ouverture impossible : {ex.Message}"); }
    }

    private static string Prefix(LogLevel level) => level switch
    {
        LogLevel.Error => "✗ ",
        LogLevel.Warning => "⚠ ",
        _ => "",
    };
}
