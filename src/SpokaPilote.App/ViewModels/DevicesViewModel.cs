using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SpokaPilote.App.Localization;
using SpokaPilote.Core.Engine;

namespace SpokaPilote.App.ViewModels;

/// <summary>Périphériques : inventaire, périphériques en erreur et réparation guidée.</summary>
public sealed class DevicesViewModel : ObservableObject
{
    public DevicesViewModel(MainViewModel shell)
    {
        Shell = shell;
        View = CollectionViewSource.GetDefaultView(Devices);
        View.Filter = o => o is DeviceItem d && Matches(d);
        ExecuteRepairCommand = new RelayCommand(ExecuteRepairAsync, () => !Shell.IsBusy && RepairPlan is not null && SelectedRepairAction is not null);
    }

    public MainViewModel Shell { get; }
    private ScanResult? Scan => Shell.LastScan;

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ICollectionView View { get; }

    // ------------------------------------------------------------------ filtres

    private string _filter = "problems";
    /// <summary>problems (en erreur), unresolved (sans pilote trouvé), all.</summary>
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

    private bool _showAllDevices;
    /// <summary>Affiche aussi les périphériques logiciels et virtuels.</summary>
    public bool ShowAllDevices { get => _showAllDevices; set { if (Set(ref _showAllDevices, value)) Reload(); } }

    private bool Matches(DeviceItem d) => Filter switch
    {
        "problems" => d.HasProblem,
        "unresolved" => d.IsUnresolved,
        _ => true,
    };

    public int ProblemCount => Devices.Count(d => d.HasProblem);
    public int UnresolvedCount => Devices.Count(d => d.IsUnresolved);
    public int AllCount => Devices.Count;

    public bool IsListEmpty => View.IsEmpty;
    public string EmptyTitle =>
        Scan is null ? Loc.T("Aucune analyse pour l'instant")
        : Filter == "all" ? Loc.T("Aucun périphérique")
        : Loc.T("Aucun périphérique en erreur");
    public string EmptyText =>
        Scan is null ? Loc.T("Lancez « Analyser ce poste » (F5) : l'inventaire du matériel apparaîtra ici.")
        : Filter == "all" ? Loc.T("Cochez « Afficher aussi les périphériques logiciels et virtuels » pour tout voir.")
        : Loc.T("Tout le matériel fonctionne. Choisissez « Tous » pour voir l'inventaire complet.");

    // ------------------------------------------------------------------ chargement

    public void Load(ScanResult scan)
    {
        Reload(scan);
        // Après une analyse : les périphériques en erreur s'il y en a, sinon tout l'inventaire.
        _filter = ProblemCount > 0 ? "problems" : "all";
        View.Refresh();
        // Premier périphérique en erreur sélectionné : son diagnostic et la réparation guidée s'affichent directement.
        SelectedDevice = Devices.FirstOrDefault(d => d.HasProblem);
        OnPropertyChanged(string.Empty);
    }

    private void Reload(ScanResult? scan = null)
    {
        scan ??= Scan;
        SelectedDevice = null;
        Devices.Clear();
        if (scan is null) return;
        var unresolved = scan.UnresolvedDevices.Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in scan.Devices.Where(d => d.IsPresent && (ShowAllDevices || d.IsHardware || d.HasProblem))
                                      .OrderByDescending(d => d.HasProblem).ThenBy(d => d.ClassName).ThenBy(d => d.Name))
            Devices.Add(new DeviceItem(d, unresolved.Contains(d.InstanceId)));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(AllCount));
        RaiseListState();
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    // ------------------------------------------------------------------ détail et réparation guidée

    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RepairResultText = "";
            RepairPlan = value is null || Scan is null ? null : Core.Repair.DeviceRepair.Plan(value.Device, Scan.Devices);
        }
    }
    public bool HasSelection => SelectedDevice is not null;

    private Core.Repair.RepairPlan? _repairPlan;
    public Core.Repair.RepairPlan? RepairPlan
    {
        get => _repairPlan;
        private set
        {
            Set(ref _repairPlan, value);
            OnPropertyChanged(nameof(HasRepairPlan));
            OnPropertyChanged(nameof(RepairPlanText));
            SelectedRepairAction = value?.Recommended;
        }
    }
    public bool HasRepairPlan => RepairPlan is not null && RepairPlan.Device.HasProblem;
    public string RepairPlanText => RepairPlan is null ? "" :
        RepairPlan.Diagnosis + (RepairPlan.Advice.Count > 0 ? "\n\n• " + string.Join("\n• ", RepairPlan.Advice) : "")
        + (RepairPlan.IsCritical ? "\n\n⚠ " + RepairPlan.CriticalReason : "");

    private Core.Repair.RepairAction? _selectedRepairAction;
    public Core.Repair.RepairAction? SelectedRepairAction { get => _selectedRepairAction; set => Set(ref _selectedRepairAction, value); }
    public RelayCommand ExecuteRepairCommand { get; }

    private string _repairResult = "";
    public string RepairResultText { get => _repairResult; private set => Set(ref _repairResult, value); }

    private async Task ExecuteRepairAsync()
    {
        if (RepairPlan is null || SelectedRepairAction is null) return;
        if (!Shell.IsAdmin) { Shell.AskRelaunch(Loc.T("La réparation d'un périphérique")); return; }
        var confirmCritical = false;
        if (RepairPlan.IsCritical)
        {
            if (MessageBox.Show($"{RepairPlan.CriticalReason}\n\n{Loc.T("Continuer quand même ?")}", "SpokaPilote",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            confirmCritical = true;
        }
        var plan = RepairPlan;
        var action = SelectedRepairAction;
        await Shell.RunBusyAsync($"{action.Label}...", async ct =>
        {
            var result = await Core.Repair.DeviceRepair.ExecuteAsync(plan, action.Kind,
                new Core.Repair.RepairOptions { ConfirmCritical = confirmCritical }, new Progress<string>(m => Shell.Status = m), ct);
            Shell.Status = result.Message;
            RepairResultText = result.Message + (result.RebootRequired ? "\n" + Loc.T("Redémarrage nécessaire") : "");
            if (result.RebootRequired) Shell.RebootPending = true;
            Shell.History.Refresh();
        });
        if (Shell.LastFailure is { } error) RepairResultText = Loc.F("La réparation n'a pas abouti : {0}", error);
    }
}
