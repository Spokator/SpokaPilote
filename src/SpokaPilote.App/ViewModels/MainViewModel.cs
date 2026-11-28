using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using SpokaPilote.App.Localization;

namespace SpokaPilote.App.ViewModels;

/// <summary>Nature de l'opération en cours (libellés de l'Accueil).</summary>
public enum BusyKind { None, Scan, Install, Other }

/// <summary>
/// Modèle de la fenêtre principale : état commun à toutes les pages (analyse, opération en cours, droits, mise à jour,
/// mode portable, navigation) et une vue-modèle par page (<see cref="Home"/>, <see cref="Drivers"/>...).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>Noms stables des pages, dans l'ordre de la navigation (préférences, captures d'écran).</summary>
    public static readonly string[] PageKeys = { "accueil", "pilotes", "peripheriques", "historique", "outils", "parametres" };

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? _cts;

    /// <param name="displayAlias">Nom affiché à la place du nom de l'ordinateur (captures destinées à être publiées).</param>
    public MainViewModel(string? displayAlias = null)
    {
        DisplayAlias = string.IsNullOrWhiteSpace(displayAlias) ? null : displayAlias.Trim();
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
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RelaunchAsAdminCommand = new RelayCommand(RelaunchAsAdmin, () => !IsAdmin);
        RestoreQuarantineCommand = new RelayCommand(RestoreQuarantine, () => !IsBusy);
        OpenUrlCommand = new RelayCommand(p => { if (p is string url) OpenPath(url); return Task.CompletedTask; });
        NavigateCommand = new RelayCommand(p => { if (p is string key) Navigate(key); return Task.CompletedTask; });
        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync, () => !IsBusy && AvailableUpdate is not null && !IsPortable);
        DownloadPortableUpdateCommand = new RelayCommand(DownloadPortableUpdateAsync, () => !IsBusy && AvailableUpdate is not null && IsPortable);
        OpenReleasePageCommand = new RelayCommand(() => { OpenPath(AvailableUpdate?.ReleaseUrl ?? Core.Online.UpdateChecker.ReleasesPage); return Task.CompletedTask; });
        RestartCommand = new RelayCommand(RestartComputer, () => !IsBusy);

        Home = new HomeViewModel(this);
        Drivers = new DriversViewModel(this);
        Devices = new DevicesViewModel(this);
        History = new HistoryViewModel(this);
        Tools = new ToolsViewModel(this);
        SettingsPage = new SettingsViewModel(this);

        History.Refresh();
        _ = LoadMachineAsync();
    }

    // ------------------------------------------------------------------ pages

    public HomeViewModel Home { get; }
    public DriversViewModel Drivers { get; }
    public DevicesViewModel Devices { get; }
    public HistoryViewModel History { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel SettingsPage { get; }

    private int _pageIndex;
    /// <summary>Page affichée (index dans <see cref="PageKeys"/>).</summary>
    public int PageIndex { get => _pageIndex; set => Set(ref _pageIndex, Math.Clamp(value, 0, PageKeys.Length - 1)); }
    public RelayCommand NavigateCommand { get; }

    public void Navigate(string key)
    {
        var index = Array.IndexOf(PageKeys, key);
        if (index >= 0) PageIndex = index;
    }

    // ------------------------------------------------------------------ poste et analyse

    public Settings Settings { get; }
    public bool IsAdmin { get; }
    public bool IsNotAdmin => !IsAdmin;
    public ObservableCollection<string> RepositoryPaths { get; } = new();

    /// <summary>Nom affiché à la place du nom de l'ordinateur (mode capture « --alias »).</summary>
    public string? DisplayAlias { get; }
    public string DisplayComputerName => DisplayAlias ?? Machine?.ComputerName ?? Environment.MachineName;

    private SystemInfo? _machine;
    /// <summary>Identité du poste : lue au démarrage (WMI, sans rien modifier), puis remplacée par celle de l'analyse.</summary>
    public SystemInfo? Machine { get => _machine; private set { if (Set(ref _machine, value)) Home.Refresh(); } }

    private ScanResult? _lastScan;
    public ScanResult? LastScan
    {
        get => _lastScan;
        private set
        {
            Set(ref _lastScan, value);
            OnPropertyChanged(nameof(HasScan));
            if (value is not null) Machine = value.System;
            Home.Refresh();
        }
    }
    public bool HasScan => LastScan is not null;

    private bool _rebootPending;
    public bool RebootPending { get => _rebootPending; set { if (Set(ref _rebootPending, value)) Home.Refresh(); } }

    // Options de sécurité par défaut (Paramètres), reprises par chaque installation (Accueil et Pilotes).
    private bool _createRestorePoint;
    public bool CreateRestorePoint { get => _createRestorePoint; set => Set(ref _createRestorePoint, value); }
    private bool _backupDrivers;
    public bool BackupDrivers { get => _backupDrivers; set => Set(ref _backupDrivers, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand RestartCommand { get; }

    private async Task LoadMachineAsync()
    {
        try
        {
            var info = await Task.Run(Core.Inventory.SystemInfoProvider.Collect);
            Machine ??= info;
        }
        catch (Exception ex)
        {
            Log.Debug($"Identité du poste non lue au démarrage : {ex.Message}");
        }
    }

    private async Task ScanAsync()
    {
        Home.ClearMessage();
        var ok = await RunBusyAsync(Loc.T("Analyse en cours..."), async ct =>
        {
            var engine = new DriverScanEngine(Settings);
            var scan = await engine.ScanAsync(new ScanOptions { DeepScan = Drivers.DeepScan },
                new Progress<ScanProgress>(p =>
                {
                    Status = p.Message;
                    if (p.Percent is { } pct) { ProgressIndeterminate = false; Progress = pct; }
                    else ProgressIndeterminate = true;
                }), ct);

            Drivers.Load(scan);
            Devices.Load(scan);
            LastScan = scan;
            Status = Loc.F("Analyse terminée en {0:0} s — {1}", scan.Duration.TotalSeconds, Home.Headline);
            await UploadToServerAsync(scan, null, ct);
        }, BusyKind.Scan);
        if (!ok)
        {
            Home.ShowMessage(LastFailure is null ? "info" : "error", LastFailure is null
                ? Loc.T("Analyse annulée. Rien n'a été modifié.")
                : Loc.F("L'analyse n'a pas abouti : {0}\nVérifiez la connexion Internet, puis relancez l'analyse (F5). Le détail est dans Outils › Journal technique.", LastFailure));
        }
    }

    /// <summary>Envoie le rapport à la console centrale si elle est configurée (jamais bloquant).</summary>
    internal async Task UploadToServerAsync(ScanResult scan, IReadOnlyList<InstallOutcome>? outcomes, CancellationToken ct)
    {
        if (!Core.Online.ServerClient.IsConfigured(Settings)) return;
        try
        {
            var client = new Core.Online.ServerClient(Settings);
            await client.UploadReportAsync(ScanReport.From(scan, outcomes), ct);
            Log.Info($"Rapport envoyé à la console centrale ({client.BaseUri.Authority}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Rapport non envoyé à la console centrale : {ex.Message}");
        }
    }

    private Task RestartComputer()
    {
        if (MessageBox.Show(Loc.T("Redémarrer le poste maintenant ?\n\nEnregistrez d'abord les documents ouverts."), "SpokaPilote",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Status = Loc.F("Redémarrage impossible : {0}", ex.Message);
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ mise à jour de SpokaPilote

    public string AppVersionLabel => $"SpokaPilote {Core.Online.UpdateChecker.CurrentVersion.ToString(3)}";

    private Core.Online.UpdateInfo? _availableUpdate;
    public Core.Online.UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            Set(ref _availableUpdate, value);
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(HasInstallerUpdate));
            OnPropertyChanged(nameof(HasPortableUpdate));
            OnPropertyChanged(nameof(UpdateLabel));
        }
    }
    public bool HasUpdate => AvailableUpdate is not null;
    /// <summary>Version installée : mise à jour par l'installeur (bandeau « Mettre à jour »).</summary>
    public bool HasInstallerUpdate => HasUpdate && !IsPortable;
    /// <summary>Mode portable : jamais d'installeur, téléchargement vérifié de l'archive portable sur la clé.</summary>
    public bool HasPortableUpdate => HasUpdate && IsPortable;
    public string UpdateLabel => AvailableUpdate is null ? "" : Loc.F("SpokaPilote {0} est disponible.", AvailableUpdate.Version.ToString(3));
    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DownloadPortableUpdateCommand { get; }
    public RelayCommand OpenReleasePageCommand { get; }

    /// <summary>Recherche une nouvelle version ; renvoie false si la vérification a échoué (hors ligne...).</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            AvailableUpdate = await Core.Online.UpdateChecker.CheckAsync(Core.Online.UpdateChecker.CurrentVersion);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Vérification de mise à jour impossible : {ex.Message}");
            return false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is null) return;
        await RunBusyAsync(Loc.F("Téléchargement de SpokaPilote {0}...", AvailableUpdate.Version.ToString(3)), async ct =>
        {
            var file = await Core.Online.UpdateChecker.DownloadAsync(AvailableUpdate,
                new Progress<double>(p => { ProgressIndeterminate = false; Progress = p; }), ct);
            Status = Loc.T("Installation de la mise à jour...");
            Core.Online.UpdateChecker.LaunchInstaller(AvailableUpdate, file, relaunch: true);
            Application.Current.Shutdown();
        });
    }

    private async Task DownloadPortableUpdateAsync()
    {
        if (AvailableUpdate is not { } update) return;
        await RunBusyAsync(Loc.F("Téléchargement de SpokaPilote {0} (archive portable)...", update.Version.ToString(3)), async ct =>
        {
            try
            {
                var file = await Core.Online.UpdateChecker.DownloadPortableAsync(update, AppPaths.Updates,
                    new Progress<double>(p => { ProgressIndeterminate = false; Progress = p; }), ct);
                Status = Loc.F("Archive vérifiée (SHA-256) : {0}. Fermez SpokaPilote puis extrayez-la à la racine de la clé, par-dessus l'ancienne version (Data et Depot sont conservés).", file);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            }
            catch (Core.Online.UpdateIntegrityException ex)
            {
                Status = ex.Message;
                OpenPath(update.ReleaseUrl);
            }
        });
    }

    // ------------------------------------------------------------------ mode portable

    /// <summary>Copie portable (clé USB) : fichier témoin SpokaPilote.portable à côté de l'exécutable.</summary>
    public bool IsPortable { get; } = AppPaths.IsPortable;
    public bool IsNotPortable => !IsPortable;
    public string PortableLabel => IsPortable ? Loc.F("Mode portable — données sur {0}", AppPaths.Root) : "";
    public string PortableDetails => !IsPortable ? "" :
        Loc.F("Données de ce poste : {0}", AppPaths.MachineRoot) + "\n" +
        (AppPaths.DefaultRepository is { } depot
            ? Loc.F("Dépôt de la clé (utilisé automatiquement) : {0}", depot)
            : Loc.T("Dépôt de la clé : absent"));

    // ------------------------------------------------------------------ paramètres partagés

    /// <summary>Configuration « parc » saisie dans l'assistant de premier lancement.</summary>
    public void ApplyFleetSetup(string repository, string serverUrl, string serverToken)
    {
        if (repository.Length > 0 && !RepositoryPaths.Contains(repository, StringComparer.OrdinalIgnoreCase))
            RepositoryPaths.Add(repository);
        if (serverUrl.Length > 0) Settings.ServerUrl = serverUrl;
        if (serverToken.Length > 0) Settings.ServerToken = serverToken;
        OnPropertyChanged(nameof(Settings));
        SaveSettings();
    }

    internal bool SaveSettings()
    {
        Settings.RepositoryPaths = RepositoryPaths.ToList();
        Settings.CreateRestorePoint = CreateRestorePoint;
        Settings.BackupDriversBeforeInstall = BackupDrivers;
        var ok = Settings.Save();
        Status = ok
            ? Loc.F("Paramètres enregistrés ({0}).", AppPaths.SettingsFile)
            : IsAdmin
                ? Loc.F("Paramètres non enregistrés ({0}) : voir le journal.", AppPaths.SettingsFile)
                : Loc.F("Paramètres non enregistrés : {0} ne se modifie qu'en administrateur (il pilote les tâches planifiées du poste).", AppPaths.SettingsFile);
        return ok;
    }

    internal static string CliPath => Path.Combine(AppContext.BaseDirectory, "spokapilote.exe");

    // ------------------------------------------------------------------ droits administrateur

    public RelayCommand RelaunchAsAdminCommand { get; }

    /// <summary>Propose de relancer en administrateur ; « action » complète la phrase « … nécessite les droits administrateur ».</summary>
    internal void AskRelaunch(string action)
    {
        if (MessageBox.Show(Loc.F("{0} nécessite les droits administrateur.\n\nRelancer SpokaPilote en administrateur ?", action),
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            RelaunchAsAdmin();
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

    // ------------------------------------------------------------------ configuration mise en quarantaine

    private IReadOnlyList<string> _quarantineNotices = ConfigQuarantine.Notices();
    /// <summary>settings.json ou jeton mis en quarantaine (fichier créé par un compte non administrateur) : bandeau de l'accueil et des paramètres.</summary>
    public string QuarantineNotice => string.Join(Environment.NewLine, _quarantineNotices);
    public bool HasQuarantineNotice => _quarantineNotices.Count > 0;
    public RelayCommand RestoreQuarantineCommand { get; }

    /// <summary>Affiche la configuration mise en quarantaine (jeton masqué) et la restaure après confirmation (administrateur).</summary>
    private void RestoreQuarantine()
    {
        if (!IsAdmin)
        {
            AskRelaunch(Loc.T("La restauration de la configuration"));
            return;
        }
        QuarantineCandidate candidate;
        try
        {
            candidate = ConfigQuarantine.Prepare();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(Loc.F("Configuration non restaurable : {0}", ex.Message), "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var content = candidate.Display?.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) ?? Loc.T("Jeton de la console (masqué) : il remplacera le jeton enregistré.");
        var warning = candidate.TrustExtendingSettings.Count > 0
            ? "\n\n" + Loc.F("Attention : réglages qui étendent la confiance des tâches SYSTEM ({0}) : vérifiez qu'ils viennent bien de votre organisation.",
                string.Join(", ", candidate.TrustExtendingSettings))
            : "";
        if (MessageBox.Show(Loc.F("Restaurer ce fichier mis en quarantaine ?\n\n{0}\n\n{1}", candidate.FullPath, content) + warning, "SpokaPilote",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            ConfigQuarantine.Restore(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(Loc.F("Restauration impossible : {0}", ex.Message), "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _quarantineNotices = ConfigQuarantine.Notices();
        OnPropertyChanged(nameof(QuarantineNotice));
        OnPropertyChanged(nameof(HasQuarantineNotice));
        MessageBox.Show(Loc.T("Configuration restaurée. Redémarrez SpokaPilote pour l'appliquer."), "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ opération en cours

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            Home.Refresh();
        }
    }
    public bool IsIdle => !IsBusy;

    private BusyKind _busyKind;
    public BusyKind BusyKind { get => _busyKind; private set => Set(ref _busyKind, value); }

    private string _status = Loc.T("Prêt. Lancez une analyse pour inventorier ce poste.");
    public string Status { get => _status; internal set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; internal set => Set(ref _progress, value); }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate { get => _progressIndeterminate; internal set => Set(ref _progressIndeterminate, value); }

    /// <summary>Message d'erreur de la dernière opération (null : réussie ou annulée).</summary>
    public string? LastFailure { get; private set; }

    public ObservableCollection<string> LogLines { get; } = new();
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenUrlCommand { get; }

    /// <summary>Exécute une opération longue (une seule à la fois) ; renvoie false si elle a échoué ou a été annulée.</summary>
    internal async Task<bool> RunBusyAsync(string message, Func<CancellationToken, Task> work, BusyKind kind = BusyKind.Other)
    {
        _cts = new CancellationTokenSource();
        LastFailure = null;
        BusyKind = kind;
        IsBusy = true;
        Status = message;
        ProgressIndeterminate = true;
        try
        {
            await work(_cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = Loc.T("Opération annulée.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Erreur", ex);
            LastFailure = ex.Message;
            Status = Loc.F("Erreur : {0}", ex.Message);
            return false;
        }
        finally
        {
            BusyKind = BusyKind.None;
            IsBusy = false;
            ProgressIndeterminate = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    internal static void OpenPath(string path)
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
