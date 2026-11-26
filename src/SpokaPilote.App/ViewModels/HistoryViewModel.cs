using System.Collections.ObjectModel;
using System.Windows;
using SpokaPilote.App.Localization;
using SpokaPilote.Core.Install;

namespace SpokaPilote.App.ViewModels;

/// <summary>Historique : interventions enregistrées, annulation, réinstallation d'une sauvegarde.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    public HistoryViewModel(MainViewModel shell)
    {
        Shell = shell;
        RollbackCommand = new RelayCommand(RollbackAsync, () => !Shell.IsBusy && SelectedIntervention?.CanRollback == true);
        RestoreBackupCommand = new RelayCommand(RestoreBackupAsync, () => !Shell.IsBusy && SelectedBackup is not null);
        RefreshCommand = new RelayCommand(Refresh);
    }

    public MainViewModel Shell { get; }
    public ObservableCollection<Intervention> Interventions { get; } = new();
    public ObservableCollection<string> Backups { get; } = new();
    public bool IsEmpty => Interventions.Count == 0;
    public bool HasBackups => Backups.Count > 0;

    private Intervention? _selectedIntervention;
    public Intervention? SelectedIntervention
    {
        get => _selectedIntervention;
        set
        {
            Set(ref _selectedIntervention, value);
            OnPropertyChanged(nameof(SelectedInterventionDetails));
            OnPropertyChanged(nameof(RollbackHint));
        }
    }

    public string SelectedInterventionDetails => SelectedIntervention is null ? Loc.T("Sélectionnez une intervention dans la liste.")
        : string.Join("\n", SelectedIntervention.Items.Select(i => $"{StatusIcon(i.Status)} {i.Title} {i.Version} — {i.Message}"))
          + (SelectedIntervention.AddedDriverPackages.Count > 0 ? "\n\n" + Loc.F("Paquets ajoutés : {0}", string.Join(", ", SelectedIntervention.AddedDriverPackages)) : "")
          + (SelectedIntervention.BackupFolder is null ? "" : "\n" + Loc.F("Sauvegarde : {0}", SelectedIntervention.BackupFolder))
          + (SelectedIntervention.RestorePointCreated ? "\n" + Loc.T("Point de restauration créé.") : "");

    /// <summary>Pourquoi le bouton d'annulation est (in)actif, en clair.</summary>
    public string RollbackHint => SelectedIntervention switch
    {
        null => "",
        { RolledBackAt: { } at } => Loc.F("Intervention déjà annulée le {0:dd/MM/yyyy HH:mm}.", at),
        { CanRollback: true } => Loc.T("L'annulation retire les pilotes ajoutés : chaque périphérique revient à son pilote précédent."),
        _ => Loc.T("Rien à annuler : cette intervention n'a ajouté aucun pilote."),
    };

    private string? _selectedBackup;
    public string? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    public RelayCommand RollbackCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand RefreshCommand { get; }

    private static string StatusIcon(string status) => status switch
    {
        "Installed" => "✓",
        "NotApplicable" => "=",
        "Skipped" => "·",
        _ => "✗",
    };

    public void Refresh()
    {
        Interventions.Clear();
        foreach (var i in InterventionHistory.List()) Interventions.Add(i);
        Backups.Clear();
        try { foreach (var b in InterventionHistory.Backups()) Backups.Add(b); } catch { /* aucun dossier */ }
        SelectedIntervention = Interventions.FirstOrDefault();
        SelectedBackup = Backups.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasBackups));
    }

    private async Task RollbackAsync()
    {
        if (SelectedIntervention is null) return;
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("L'annulation d'une intervention")); return; }
        if (MessageBox.Show(Loc.F("Annuler l'intervention du {0:dd/MM/yyyy HH:mm} ?\n\nLes paquets {1} seront retirés et les périphériques reviendront à leur pilote précédent.",
                    SelectedIntervention.Timestamp, string.Join(", ", SelectedIntervention.AddedDriverPackages)),
                "SpokaPilote", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var target = SelectedIntervention;
        await Shell.RunBusyAsync(Loc.T("Annulation de l'intervention..."), async ct =>
        {
            var (_, message) = await InterventionHistory.RollbackAsync(target, new Progress<string>(m => Shell.Status = m), ct);
            Shell.Status = message;
            Refresh();
        });
    }

    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is null) return;
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("La restauration d'une sauvegarde")); return; }
        var folder = SelectedBackup;
        await Shell.RunBusyAsync(Loc.T("Restauration de la sauvegarde..."), async ct =>
        {
            var (_, message) = await InterventionHistory.RestoreBackupAsync(folder, ct);
            Shell.Status = message;
        });
    }
}
