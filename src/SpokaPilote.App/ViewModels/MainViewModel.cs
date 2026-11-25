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
