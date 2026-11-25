using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.App.ViewModels;

public sealed class CandidateItem : ObservableObject
{
    private bool _isSelected;
    private string _status = "";
    private string _statusDetail = "";

    public CandidateItem(DriverCandidate candidate)
    {
        Candidate = candidate;
        // Sélection par défaut : pilotes manquants et mises à jour ciblées ; optionnels et firmware à cocher manuellement.
        // Un pilote bloqué par la politique n'est jamais sélectionnable.
        _isSelected = candidate.BlockedReason is null && candidate.Kind is CandidateKind.MissingDriver or CandidateKind.Update;
        if (candidate.BlockedReason is not null)
        {
            _status = "⛔ Bloqué par la politique";
            _statusDetail = candidate.BlockedReason;
        }
    }

    public bool IsBlocked => Candidate.BlockedReason is not null;
    public bool IsSelectable => !IsBlocked;

    public DriverCandidate Candidate { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value && !IsBlocked); }
    public string Kind => Candidate.KindLabel;
    public string Title => Candidate.Title;
    public string Version => Candidate.Version ?? "";
    public string Date => Candidate.Date?.ToString("dd/MM/yyyy") ?? "";
    public string Source => Candidate.SourceLabel;
    public string Size => Candidate.SizeLabel;
    public string Devices => Candidate.TargetDeviceNames.Count == 0 ? "—" : string.Join(", ", Candidate.TargetDeviceNames.Distinct());
    public bool IsMissing => Candidate.Kind == CandidateKind.MissingDriver;
    public bool IsFirmware => Candidate.Kind == CandidateKind.Firmware;

    public string Status { get => _status; set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }

    public void Apply(InstallOutcome outcome)
    {
        Status = outcome.Status switch
        {
            InstallStatus.Installed => outcome.RebootRequired ? "✓ Installé (redémarrage)" : "✓ Installé",
            InstallStatus.NotApplicable => "= Déjà à jour",
            InstallStatus.Skipped => "· Vérifié (simulation)",
            InstallStatus.Blocked => "⛔ Bloqué",
            _ => "✗ Échec",
        };
        StatusDetail = outcome.Message;
    }
}

public sealed class DeviceItem
{
    public DeviceItem(DeviceInfo device) => Device = device;
    public DeviceInfo Device { get; }
    public string Name => Device.Name;
    public string Class => Device.ClassName ?? "";
    public string Status => Device.StatusText;
    public string? Hint => Device.Hint;
    public bool HasProblem => Device.HasProblem;
    public string DriverVersion => Device.DriverVersion ?? "";
    public string DriverDate => Device.DriverDate?.ToString("dd/MM/yyyy") ?? "";
    public string Provider => Device.DriverProvider ?? "";
    public string HardwareId => Device.HardwareIds.FirstOrDefault() ?? Device.InstanceId;
    public string Icon => Device.HasProblem ? "⚠" : Device.Health == DeviceHealth.Disabled ? "⏸" : "✓";
}

public sealed class PrinterItem : ObservableObject
{
    private string _driverSummary = "";

    public PrinterItem(DiscoveredPrinter printer) => Printer = printer;
    public DiscoveredPrinter Printer { get; }
    public string Address => Printer.Address;
    public string Name => Printer.DisplayName;
    public string Details
    {
        get
        {
            var parts = new List<string> { Printer.Ports };
            if (Printer.IdentifiedBy.Count > 0) parts.Add("via " + string.Join("+", Printer.IdentifiedBy));
            if (Printer.SerialNumber is not null) parts.Add("S/N " + Printer.SerialNumber);
            if (Printer.Location is not null) parts.Add(Printer.Location);
            return string.Join(" · ", parts);
        }
    }
    public string InstalledAs => Printer.InstalledAs is null ? "" : $"Déjà installée : {Printer.InstalledAs}";
    public string DriverSummary { get => _driverSummary; set => Set(ref _driverSummary, value); }
}
