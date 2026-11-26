using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SpokaPilote.App.Localization;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.App.ViewModels;

/// <summary>Pilotes : liste filtrable des pilotes proposés, sélection, simulation et installation.</summary>
public sealed class DriversViewModel : ObservableObject
{
    public DriversViewModel(MainViewModel shell)
    {
        Shell = shell;
        View = CollectionViewSource.GetDefaultView(Candidates);
        View.Filter = o => o is CandidateItem c && Matches(c, Filter);
        InstallCommand = new RelayCommand(() => InstallAsync(dryRun: false), () => !Shell.IsBusy && SelectedCount > 0);
        SimulateCommand = new RelayCommand(() => InstallAsync(dryRun: true), () => !Shell.IsBusy && SelectedCount > 0);
        ExportReportCommand = new RelayCommand(ExportReport, () => Shell.LastScan is not null);
    }

    public MainViewModel Shell { get; }
    private ScanResult? Scan => Shell.LastScan;

    public ObservableCollection<CandidateItem> Candidates { get; } = new();
    public ICollectionView View { get; }

    private bool _deepScan;
    /// <summary>Analyse approfondie : cherche aussi des mises à jour pour les composants qui fonctionnent.</summary>
    public bool DeepScan { get => _deepScan; set => Set(ref _deepScan, value); }

    // ------------------------------------------------------------------ filtres

    private string _filter = "all";
    /// <summary>all, missing, updates, blocked.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            View.Refresh();
            RaiseListState();
        }
    }

    private static bool Matches(CandidateItem c, string filter) => filter switch
    {
        "missing" => !c.IsBlocked && c.KindValue == CandidateKind.MissingDriver,
        "updates" => !c.IsBlocked && c.KindValue != CandidateKind.MissingDriver,
        "blocked" => c.IsBlocked,
        _ => true,
    };

    public int MissingCount => Candidates.Count(c => Matches(c, "missing"));
    public int UpdateCount => Candidates.Count(c => Matches(c, "updates"));
    public int BlockedCount => Candidates.Count(c => c.IsBlocked);
    public int TotalCount => Candidates.Count;
    /// <summary>Pilotes manquants et mises à jour (pastille de la navigation).</summary>
    public int ActionableCount => Candidates.Count(c => c.IsSelectable && c.KindValue is CandidateKind.MissingDriver or CandidateKind.Update);

    public int SelectedCount => Candidates.Count(c => c.IsSelected);

    /// <summary>
    /// Case « tout cocher » de l'en-tête de liste (pilotes affichés et installables) : cochée, décochée ou partielle (null).
    /// Un clic sur l'état partiel coche tout.
    /// </summary>
    public bool? AllSelected
    {
        get
        {
            var items = View.Cast<CandidateItem>().Where(c => c.IsSelectable).ToList();
            var selected = items.Count(c => c.IsSelected);
            return items.Count == 0 || selected == 0 ? false : selected == items.Count ? true : null;
        }
        set
        {
            var selectAll = value == true || AllSelected is null;
            foreach (var c in View.Cast<CandidateItem>()) c.IsSelected = selectAll;
            OnPropertyChanged();
        }
    }
    public string InstallLabel => SelectedCount == 0 ? Loc.T("Installer la sélection") : Loc.F("Installer la sélection ({0})", SelectedCount);

    // ------------------------------------------------------------------ états vides

    public bool IsListEmpty => View.IsEmpty;
    public string EmptyTitle =>
        Scan is null ? Loc.T("Aucune analyse pour l'instant")
        : Candidates.Count == 0 ? Loc.T("Aucun pilote à installer")
        : Loc.T("Rien dans cette catégorie");
    public string EmptyText =>
        Scan is null ? Loc.T("Lancez « Analyser ce poste » (F5) : la liste des pilotes manquants et des mises à jour apparaîtra ici.")
        : Candidates.Count == 0 ? Loc.T("Les sources officielles ne proposent rien de plus pour ce poste. Cochez « Chercher aussi les mises à jour » puis analysez à nouveau pour aller plus loin.")
        : Loc.T("Choisissez « Tout » pour voir l'ensemble des pilotes proposés.");

    // ------------------------------------------------------------------ politique et sources

    public string PolicySummary => Scan?.Policy is { } p
        ? p.Summary + (p.Messages.Count > 0 ? "\n" + string.Join("\n", p.Messages) : "")
        : Loc.T("Politique : chargée à la prochaine analyse.");
    public bool HasSourceWarnings => Scan?.SourceErrors.Count > 0;
    public string SourceWarnings => Scan is null ? "" : string.Join("\n", Scan.SourceErrors.Select(e => $"{e.Key} : {e.Value}"));
    public string SourcesUsed => Scan is null || Scan.SourcesUsed.Count == 0 ? "" : Loc.F("Sources interrogées : {0}.", string.Join(", ", Scan.SourcesUsed));

    // ------------------------------------------------------------------ chargement

    public void Load(ScanResult scan)
    {
        foreach (var c in Candidates) c.PropertyChanged -= OnCandidateChanged;
        Candidates.Clear();
        foreach (var c in scan.Candidates)
        {
            var item = new CandidateItem(c);
            item.PropertyChanged += OnCandidateChanged;
            Candidates.Add(item);
        }
        // Après une analyse, la liste complète.
        _filter = "all";
        View.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CandidateItem.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(AllSelected));
        Shell.Home.Refresh();
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    // ------------------------------------------------------------------ installation

    public RelayCommand InstallCommand { get; }
    public RelayCommand SimulateCommand { get; }
    public RelayCommand ExportReportCommand { get; }

    private async Task InstallAsync(bool dryRun)
    {
        if (Scan is not { } scan) return;
        if (!dryRun && !Shell.IsAdmin)
        {
            Shell.AskRelaunch(Loc.T("L'installation de pilotes"));
            return;
        }

        var selected = Candidates.Where(c => c.IsSelected).ToList();
        if (!dryRun && selected.Any(c => c.IsFirmware) &&
            MessageBox.Show(Loc.T("La sélection contient une mise à jour de BIOS/firmware.\nBranchez le secteur, suspendez BitLocker (Suspend-BitLocker -MountPoint C: -RebootCount 1 : SpokaPilote ne le fait pas lui-même) et ne coupez pas l'alimentation pendant l'opération.\n\nContinuer ?"),
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Shell.Home.ClearMessage();
        foreach (var c in selected) { c.Status = Loc.T("En attente"); c.StatusDetail = ""; }
        IReadOnlyList<InstallOutcome>? outcomes = null;
        var ok = await Shell.RunBusyAsync(dryRun ? Loc.T("Simulation...") : Loc.T("Installation..."), async ct =>
        {
            var installer = new DriverInstaller(Shell.Settings, scan.System);
            outcomes = await installer.InstallAsync(selected.Select(s => s.Candidate).ToList(), scan.Devices,
                new InstallOptions { DryRun = dryRun, CreateRestorePoint = Shell.CreateRestorePoint, BackupDrivers = Shell.BackupDrivers },
                new Progress<InstallProgress>(p =>
                {
                    Shell.Status = p.Index == 0 ? p.Step : Loc.F("Pilote {0} sur {1} — {2} : {3}", p.Index, p.Total, p.Title, p.Step);
                    Shell.ProgressIndeterminate = false;
                    Shell.Progress = p.Total == 0 ? 0 : 100.0 * Math.Max(0, p.Index - 1) / p.Total;
                    var item = p.Index >= 1 && p.Index <= selected.Count ? selected[p.Index - 1] : null;
                    if (item is not null) item.Status = p.Step;
                }), ct);

            foreach (var o in outcomes)
                selected.FirstOrDefault(s => s.Candidate == o.Candidate)?.Apply(o);
            if (!dryRun)
            {
                // Ce qui est installé (ou déjà à jour) n'est plus à installer : l'Accueil ne le propose plus.
                foreach (var o in outcomes.Where(o => o.Succeeded || o.Status == InstallStatus.NotApplicable))
                    if (selected.FirstOrDefault(s => s.Candidate == o.Candidate) is { } done) done.IsSelected = false;
                Shell.History.Refresh();
                await Shell.UploadToServerAsync(scan, outcomes, ct);
            }
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.Reports, $"{scan.System.ComputerName}-{DateTime.Now:yyyyMMdd-HHmmss}.html"),
                    ReportWriter.ToHtml(scan, outcomes));
            }
            catch { /* historique facultatif */ }
        }, BusyKind.Install);

        if (outcomes is null)
        {
            Shell.Home.ShowMessage(Shell.LastFailure is null ? "info" : "error", Shell.LastFailure is null
                ? Loc.T("Installation annulée. Les pilotes déjà installés sont listés dans l'Historique.")
                : Loc.F("L'installation n'a pas abouti : {0}\nLe détail est dans Outils › Journal technique.", Shell.LastFailure));
            return;
        }

        var succeeded = outcomes.Count(o => o.Succeeded);
        var failed = outcomes.Count(o => o.Status is InstallStatus.Failed or InstallStatus.Blocked);
        if (!dryRun) Shell.RebootPending |= outcomes.Any(o => o.RebootRequired);
        string message;
        string kind;
        if (dryRun)
        {
            kind = failed == 0 ? "info" : "warning";
            message = Loc.F("Simulation terminée : {0} pilote(s) téléchargé(s) et vérifié(s), {1} problème(s). Rien n'a été installé.",
                outcomes.Count(o => o.Status == InstallStatus.Skipped), failed);
        }
        else if (failed == 0)
        {
            kind = "success";
            message = Loc.F("Installation terminée : {0} pilote(s) installé(s).", succeeded);
        }
        else
        {
            kind = succeeded == 0 ? "error" : "warning";
            message = Loc.F("Installation terminée : {0} réussi(s), {1} échec(s). Les pilotes en échec restent cochés ; la raison est indiquée dans la page Pilotes.", succeeded, failed);
        }
        Shell.Status = message;
        Shell.Home.ShowMessage(kind, message);
        Shell.Home.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private Task ExportReport()
    {
        if (Scan is null) return Task.CompletedTask;
        var dialog = new SaveFileDialog
        {
            FileName = $"SpokaPilote-{Scan.System.ComputerName}-{DateTime.Now:yyyyMMdd}.html",
            Filter = Loc.T("Rapport HTML (*.html)|*.html|Rapport JSON (*.json)|*.json"),
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var content = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ReportWriter.ToJson(Scan)
            : ReportWriter.ToHtml(Scan);
        File.WriteAllText(dialog.FileName, content);
        MainViewModel.OpenPath(dialog.FileName);
        return Task.CompletedTask;
    }
}
