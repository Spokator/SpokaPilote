using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SpokaPilote.App.Localization;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.App.ViewModels;

/// <summary>
/// Outils (usage avancé) : dépôt de pilotes et packs constructeur, pack hors ligne, rapport de parc, nettoyage du
/// magasin, journal technique, lien vers SpokaPrint. La maintenance planifiée et l'agent sont dans <see cref="Maintenance"/>.
/// </summary>
public sealed class ToolsViewModel : ObservableObject
{
    public ToolsViewModel(MainViewModel shell)
    {
        Shell = shell;
        Maintenance = new MaintenanceViewModel(shell);
        AddRepositoryCommand = new RelayCommand(AddRepository);
        RemoveRepositoryCommand = new RelayCommand(RemoveRepository, () => SelectedRepository is not null);
        ExportDriversCommand = new RelayCommand(ExportDriversAsync, () => !Shell.IsBusy);
        ImportDriversCommand = new RelayCommand(ImportDriversAsync, () => !Shell.IsBusy && (Shell.RepositoryPaths.Count > 0 || AppPaths.DefaultRepository is not null));
        SearchDriverPacksCommand = new RelayCommand(SearchDriverPacksAsync, () => !Shell.IsBusy);
        DownloadDriverPackCommand = new RelayCommand(DownloadDriverPackAsync, () => !Shell.IsBusy && SelectedDriverPack is not null);
        BuildPackCommand = new RelayCommand(BuildPackAsync, () => !Shell.IsBusy);
        FleetReportCommand = new RelayCommand(BuildFleetReport, () => !Shell.IsBusy);
        AnalyzeDriverStoreCommand = new RelayCommand(AnalyzeDriverStoreAsync, () => !Shell.IsBusy);
        CleanDriverStoreCommand = new RelayCommand(CleanDriverStoreAsync, () => !Shell.IsBusy && _storeAnalysis is { Candidates.Count: > 0 });
        OpenLogsCommand = new RelayCommand(() => MainViewModel.OpenPath(AppPaths.Logs));
        OpenSpokaPrintCommand = new RelayCommand(OpenSpokaPrint);
    }

    public MainViewModel Shell { get; }
    public MaintenanceViewModel Maintenance { get; }

    // ------------------------------------------------------------------ dépôt de pilotes

    public ObservableCollection<string> RepositoryPaths => Shell.RepositoryPaths;
    private string? _selectedRepository;
    public string? SelectedRepository { get => _selectedRepository; set => Set(ref _selectedRepository, value); }
    private string _repositoryResult = "";
    public string RepositoryResult { get => _repositoryResult; set => Set(ref _repositoryResult, value); }
    /// <summary>Mode portable : dépôt de la clé, utilisé automatiquement en plus de la liste.</summary>
    public string DefaultRepositoryNote => AppPaths.DefaultRepository is { } depot
        ? Loc.F("Également utilisé automatiquement : {0}", depot)
        : "";

    public RelayCommand AddRepositoryCommand { get; }
    public RelayCommand RemoveRepositoryCommand { get; }
    public RelayCommand ExportDriversCommand { get; }
    public RelayCommand ImportDriversCommand { get; }

    private void AddRepository()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Dossier ou partage réseau contenant des pilotes (.inf)") };
        if (dialog.ShowDialog() != true) return;
        if (!RepositoryPaths.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) RepositoryPaths.Add(dialog.FolderName);
        Shell.SaveSettings();
    }

    private void RemoveRepository()
    {
        if (SelectedRepository is null) return;
        RepositoryPaths.Remove(SelectedRepository);
        Shell.SaveSettings();
    }

    private async Task ExportDriversAsync()
    {
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("L'export des pilotes")); return; }
        var dialog = new OpenFolderDialog { Title = Loc.T("Dépôt de destination (ex. \\\\serveur\\pilotes)") };
        if ((SelectedRepository ?? AppPaths.DefaultRepository) is { } initial) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog() != true) return;
        await Shell.RunBusyAsync(Loc.T("Export des pilotes de ce poste..."), async ct =>
        {
            var (_, _, message) = await DriverExporter.ExportAsync(dialog.FolderName, ct);
            RepositoryResult = message;
            Shell.Status = message;
        });
    }

    private async Task ImportDriversAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Pilote téléchargé (archive .zip/.cab ou fichier .inf d'un dossier extrait)"),
            Filter = Loc.T("Pilotes (*.zip;*.cab;*.inf)|*.zip;*.cab;*.inf"),
        };
        if (dialog.ShowDialog() != true) return;
        var source = dialog.FileName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dialog.FileName)! : dialog.FileName;
        var repo = SelectedRepository ?? RepositoryPaths.FirstOrDefault() ?? AppPaths.DefaultRepository!;
        await Shell.RunBusyAsync(Loc.T("Import dans le dépôt..."), async ct =>
        {
            var (_, message) = await DriverExporter.ImportAsync(source, repo, ct);
            RepositoryResult = message;
            Shell.Status = message;
        });
    }

    // ------------------------------------------------------------------ packs officiels des constructeurs

    private string _packModel = "";
    public string PackModel { get => _packModel; set => Set(ref _packModel, value); }
    public string[] PackOsChoices { get; } = { Loc.T("Windows 11"), Loc.T("Windows 10") };
    private int _packOs;
    public int PackOs { get => _packOs; set => Set(ref _packOs, value); }
    public ObservableCollection<DriverPackMatch> DriverPacks { get; } = new();
    private DriverPackMatch? _selectedDriverPack;
    public DriverPackMatch? SelectedDriverPack { get => _selectedDriverPack; set => Set(ref _selectedDriverPack, value); }
    private string _packResult = "";
    public string PackResult { get => _packResult; set => Set(ref _packResult, value); }
    public RelayCommand SearchDriverPacksCommand { get; }
    public RelayCommand DownloadDriverPackCommand { get; }

    private async Task SearchDriverPacksAsync()
    {
        await Shell.RunBusyAsync(Loc.T("Recherche dans les catalogues de packs Dell, HP et Lenovo..."), async ct =>
        {
            DriverPackQuery query;
            if (string.IsNullOrWhiteSpace(PackModel))
            {
                var system = Shell.LastScan?.System ?? Shell.Machine ?? await Task.Run(Core.Inventory.SystemInfoProvider.Collect, ct);
                query = DriverPackQuery.ForSystem(system);
            }
            else
            {
                query = new DriverPackQuery { Text = PackModel.Trim() };
            }
            query.Os = PackOs == 1 ? "win10" : "win11";
            query.Architecture ??= Core.Online.UpdateChecker.HostArchitecture;
            var matches = await OemDriverPacks.SearchAsync(query, Shell.Settings, new Progress<string>(m => Shell.Status = m), ct);
            DriverPacks.Clear();
            foreach (var m in matches) DriverPacks.Add(m);
            SelectedDriverPack = DriverPacks.FirstOrDefault();
            PackResult = matches.Count == 0
                ? Loc.F("Aucun pack officiel pour {0} ({1}). Seuls Dell, HP, Lenovo et Microsoft Surface publient des packs de déploiement.", query.Describe(), PackOsChoices[PackOs])
                : Loc.F("{0} pack(s) trouvé(s) pour {1}.", matches.Count, query.Describe());
            Shell.Status = PackResult;
        });
    }

    private async Task DownloadDriverPackAsync()
    {
        if (SelectedDriverPack is null) return;
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("Le téléchargement et l'extraction d'un pack constructeur")); return; }
        var repo = SelectedRepository ?? RepositoryPaths.FirstOrDefault() ?? AppPaths.DefaultRepository;
        if (repo is null)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Dépôt de pilotes où ranger le pack (ex. \\\\serveur\\pilotes)") };
            if (dialog.ShowDialog() != true) return;
            repo = dialog.FolderName;
            if (!RepositoryPaths.Contains(repo, StringComparer.OrdinalIgnoreCase)) { RepositoryPaths.Add(repo); Shell.SaveSettings(); }
        }
        var match = SelectedDriverPack;
        await Shell.RunBusyAsync(Loc.F("Téléchargement du pack {0} ({1})...", match.ModelLabel, match.Pack.SizeLabel), async ct =>
        {
            var result = await OemDriverPackDownloader.DownloadAsync(match, repo, Shell.Settings, null, new Progress<string>(m => Shell.Status = m), ct);
            PackResult = result.Message + (result.Success
                ? "\n" + Loc.F("{0}/{1} pilotes signés, rangés dans {2}. Les postes de ce modèle s'installent désormais depuis le dépôt.", result.SignedInfCount, result.InfCount, result.Folder)
                : "");
            Shell.Status = result.Success ? Loc.T("Pack constructeur prêt dans le dépôt.") : Loc.T("Échec du pack constructeur.");
        });
    }

    // ------------------------------------------------------------------ pack hors ligne et rapport de parc

    private bool _packAllHardware;
    public bool PackAllHardware { get => _packAllHardware; set => Set(ref _packAllHardware, value); }
    private string _offlineResult = "";
    public string OfflineResult { get => _offlineResult; set => Set(ref _offlineResult, value); }
    private string _fleetResult = "";
    public string FleetResult { get => _fleetResult; set => Set(ref _fleetResult, value); }
    public RelayCommand BuildPackCommand { get; }
    public RelayCommand FleetReportCommand { get; }

    private async Task BuildPackAsync()
    {
        var open = new OpenFileDialog
        {
            Title = Loc.T("Rapport JSON du poste sans réseau (spokapilote scan --json), ou Annuler pour ce poste"),
            Filter = Loc.T("Rapport SpokaPilote (*.json)|*.json"),
        };
        var reportPath = open.ShowDialog() == true ? open.FileName : null;
        var dest = new OpenFolderDialog { Title = Loc.T("Dossier du pack (ex. clé USB)") };
        if (dest.ShowDialog() != true) return;

        await Shell.RunBusyAsync(Loc.T("Préparation du pack hors ligne..."), async ct =>
        {
            SystemInfo system;
            IReadOnlyList<DeviceInfo> devices;
            if (reportPath is not null)
            {
                var report = ScanReport.Load(reportPath);
                (system, devices) = (report.System, report.Devices);
            }
            else if (Shell.LastScan is { } scan)
            {
                (system, devices) = (scan.System, scan.Devices);
            }
            else
            {
                system = await Task.Run(Core.Inventory.SystemInfoProvider.Collect, ct);
                devices = await Task.Run(Core.Inventory.DeviceInventory.Collect, ct);
            }
            var result = await OfflinePackBuilder.BuildAsync(system, devices, dest.FolderName, PackAllHardware, Shell.Settings,
                new Progress<string>(m => Shell.Status = m), ct);
            OfflineResult = result.Summary + "\n" + Loc.F("Sur le poste cible : spokapilote install --repo \"{0}\" --yes (ou ajoutez ce dossier comme dépôt dans Outils).", dest.FolderName);
            Shell.Status = result.Summary;
            MainViewModel.OpenPath(dest.FolderName);
        });
    }

    private Task BuildFleetReport()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Dossier contenant les rapports JSON des postes (ex. \\\\serveur\\rapports)") };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var machines = FleetReport.Load(dialog.FolderName);
        if (machines.Count == 0)
        {
            FleetResult = Loc.T("Aucun rapport SpokaPilote dans ce dossier. Sur chaque poste : spokapilote install --yes --output-dir \\\\serveur\\rapports");
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
        FleetResult = Loc.F("{0} poste(s), {1} à traiter : {2}", machines.Count, machines.Count(m => !m.Healthy), output);
        MainViewModel.OpenPath(output);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ nettoyage du magasin de pilotes

    private DriverStoreAnalysis? _storeAnalysis;
    private string _storeText = "";
    public string DriverStoreText { get => _storeText; private set => Set(ref _storeText, value); }
    public RelayCommand AnalyzeDriverStoreCommand { get; }
    public RelayCommand CleanDriverStoreCommand { get; }

    private async Task AnalyzeDriverStoreAsync()
    {
        await Shell.RunBusyAsync(Loc.T("Analyse du magasin de pilotes..."), async ct =>
        {
            _storeAnalysis = await Task.Run(() => DriverStoreCleaner.AnalyzeThisPc(new Progress<string>(m => Shell.Status = m)), ct);
            DriverStoreText = _storeAnalysis.Summary +
                (_storeAnalysis.Candidates.Count > 0
                    ? "\n\n" + string.Join("\n", _storeAnalysis.Candidates.Take(12).Select(c => $"• {c.Package.DisplayName} {c.Package.Version} — {c.Package.SizeLabel}"))
                      + (_storeAnalysis.Candidates.Count > 12 ? $"\n… (+{_storeAnalysis.Candidates.Count - 12})" : "")
                    : "");
            Shell.Status = _storeAnalysis.Summary;
        });
    }

    private async Task CleanDriverStoreAsync()
    {
        if (_storeAnalysis is null) return;
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("Le nettoyage du magasin de pilotes")); return; }
        var candidates = _storeAnalysis.Candidates;
        if (MessageBox.Show(Loc.F("Supprimer {0} ancienne(s) version(s) de pilotes inutilisée(s) ({1}) ?\n\nChaque paquet est sauvegardé avant suppression et l'opération apparaît dans l'Historique.",
                candidates.Count, DriverStoreCleaner.FormatSize(_storeAnalysis.ReclaimableBytes)),
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Shell.RunBusyAsync(Loc.T("Nettoyage du magasin de pilotes..."), async ct =>
        {
            var result = await DriverStoreCleaner.CleanAsync(candidates, new DriverStoreCleanOptions { Backup = true },
                new Progress<string>(m => Shell.Status = m), ct);
            DriverStoreText = Loc.F("{0} paquet(s) supprimé(s), {1} libéré(s).", result.Removals.Count(r => r.Success),
                DriverStoreCleaner.FormatSize(result.FreedBytes))
                + (result.Removals.Any(r => !r.Success) ? "\n" + string.Join("\n", result.Removals.Where(r => !r.Success).Select(r => $"✗ {r.Package.DisplayName} : {r.Message}")) : "");
            Shell.Status = DriverStoreText.Split('\n')[0];
            _storeAnalysis = null;
            Shell.History.Refresh();
        });
    }

    // ------------------------------------------------------------------ journal technique

    public ObservableCollection<string> LogLines => Shell.LogLines;
    public string LogsFolder => AppPaths.Logs;
    public RelayCommand OpenLogsCommand { get; }

    // ------------------------------------------------------------------ imprimantes → SpokaPrint

    /// <summary>Page de téléchargement de SpokaPrint (produit distinct qui reprend le déploiement des imprimantes).</summary>
    public const string SpokaPrintReleasesUrl = "https://github.com/Spokator/SpokaPrint/releases/latest";

    private static string SpokaPrintExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SpokaPrint", "SpokaPrint.App.exe");

    public bool IsSpokaPrintInstalled => File.Exists(SpokaPrintExe);
    public string SpokaPrintButtonLabel => IsSpokaPrintInstalled ? Loc.T("Ouvrir SpokaPrint") : Loc.T("Télécharger SpokaPrint");
    public RelayCommand OpenSpokaPrintCommand { get; }

    private Task OpenSpokaPrint()
    {
        MainViewModel.OpenPath(IsSpokaPrintInstalled ? SpokaPrintExe : SpokaPrintReleasesUrl);
        OnPropertyChanged(nameof(SpokaPrintButtonLabel));
        return Task.CompletedTask;
    }
}
