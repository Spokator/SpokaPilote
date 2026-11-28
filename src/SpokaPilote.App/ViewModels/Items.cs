using SpokaPilote.App.Localization;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

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
        _isSelected = candidate.BlockedReason is null && !candidate.IsInformational && candidate.Kind is CandidateKind.MissingDriver or CandidateKind.Update;
        if (candidate.HeldByRollout)
        {
            // Retenu par la diffusion par anneaux de la console : affiché comme un pilote bloqué (non sélectionnable), avec la
            // date de disponibilité pour l'anneau du poste, à l'heure locale.
            _status = Loc.T("⏳ Retenu (diffusion par anneaux)");
            _statusDetail = HoldText(candidate);
        }
        else if (candidate.BlockedReason is not null)
        {
            _status = Loc.T("⛔ Bloqué par la politique");
            _statusDetail = candidate.BlockedReason;
        }
        else if (candidate.IsInformational)
        {
            _status = Loc.T("🔗 Site officiel (manuel)");
            _statusDetail = candidate.InfoUrl ?? "";
        }
    }

    /// <summary>
    /// Pilote devenu bloqué ou retenu depuis l'analyse (politique revérifiée juste avant l'installation) : décoché, non
    /// sélectionnable, motif affiché.
    /// </summary>
    public void MarkBlockedSinceScan()
    {
        IsSelected = false;
        Status = Candidate.HeldByRollout ? Loc.T("⏳ Retenu (diffusion par anneaux)") : Loc.T("⛔ Bloqué par la politique");
        StatusDetail = Candidate.HeldByRollout ? HoldText(Candidate) : Candidate.BlockedReason ?? "";
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsSelectable));
        OnPropertyChanged(nameof(Subtitle));
    }

    public bool IsBlocked => Candidate.BlockedReason is not null;
    public bool IsInformational => Candidate.IsInformational;
    public bool IsSelectable => !IsBlocked && !IsInformational;
    public string? InfoUrl => Candidate.InfoUrl;
    public bool HasInfoUrl => !string.IsNullOrEmpty(Candidate.InfoUrl);

    public DriverCandidate Candidate { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value && IsSelectable); }
    public CandidateKind KindValue => Candidate.Kind;
    public string Kind => Loc.T(Candidate.KindLabel);
    public string Title => Candidate.Title;
    public string Version => Candidate.Version ?? "";
    public string Date => Candidate.Date?.ToString("dd/MM/yyyy") ?? "";
    public string Source => Loc.T(Candidate.SourceLabel);
    public string Size => Candidate.SizeLabel;
    public string Devices => Candidate.TargetDeviceNames.Count == 0 ? "—" : string.Join(", ", Candidate.TargetDeviceNames.Distinct());
    /// <summary>Seconde ligne de la liste : le motif de blocage, sinon les périphériques concernés.</summary>
    public string Subtitle => Candidate.HeldByRollout ? HoldText(Candidate)
        : Candidate.BlockedReason ?? (Candidate.TargetDeviceNames.Count == 0 ? "" : Loc.F("Pour : {0}", Devices));

    /// <summary>Motif d'une retenue de la diffusion par anneaux (traduit quand la date et l'anneau sont connus).</summary>
    private static string HoldText(DriverCandidate c) => c.RolloutAvailableAt is { } at && c.RolloutRing is { } ring
        ? Loc.F("Disponible pour votre anneau « {0} » le {1}", ring, at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))
        : c.BlockedReason ?? "";
    public bool IsMissing => Candidate.Kind == CandidateKind.MissingDriver;
    public bool IsFirmware => Candidate.Kind == CandidateKind.Firmware;

    public string Status { get => _status; set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }

    public void Apply(InstallOutcome outcome)
    {
        Status = outcome.Status switch
        {
            InstallStatus.Installed => outcome.RebootRequired ? Loc.T("✓ Installé (redémarrage)") : Loc.T("✓ Installé"),
            InstallStatus.NotApplicable => Loc.T("= Déjà à jour"),
            InstallStatus.Skipped => Loc.T("· Vérifié (simulation)"),
            InstallStatus.Blocked => Loc.T("⛔ Bloqué"),
            _ => Loc.T("✗ Échec"),
        };
        StatusDetail = outcome.Message;
    }
}

public sealed class DeviceItem
{
    public DeviceItem(DeviceInfo device, bool isUnresolved = false)
    {
        Device = device;
        IsUnresolved = isUnresolved;
    }

    public DeviceInfo Device { get; }
    /// <summary>En erreur et aucune source n'a trouvé de pilote.</summary>
    public bool IsUnresolved { get; }
    public string Name => Device.Name;
    public string Class => Device.ClassName ?? "";
    public string Status => Loc.T(Device.StatusText);
    public string? Hint => Device.Hint;
    public bool HasHint => !string.IsNullOrWhiteSpace(Device.Hint);
    public bool HasProblem => Device.HasProblem;
    public string DriverVersion => Device.DriverVersion ?? "";
    public string DriverDate => Device.DriverDate?.ToString("dd/MM/yyyy") ?? "";
    public string Provider => Device.DriverProvider ?? "";
    public string DriverSummary => Device.DriverVersion is null ? Loc.T("Aucun pilote installé")
        : string.Join("  ·  ", new[] { Device.DriverVersion, DriverDate, Device.DriverProvider }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string HardwareId => Device.HardwareIds.FirstOrDefault() ?? Device.InstanceId;
    /// <summary>Identifiants techniques (copiables) : identifiants matériels et identifiant d'instance.</summary>
    public string TechnicalIds => string.Join("\n", Device.HardwareIds.Take(4).Append(Device.InstanceId).Distinct());
    /// <summary>ok, problem, disabled (icône de la liste).</summary>
    public string State => Device.HasProblem ? "problem" : Device.Health == DeviceHealth.Disabled ? "disabled" : "ok";
}

/// <summary>Intervention de l'Historique : quoi, quand, et si elle peut encore être annulée (en clair, traduit).</summary>
public sealed class InterventionRow
{
    public InterventionRow(Intervention intervention) => Intervention = intervention;
    public Intervention Intervention { get; }

    /// <summary>« 25/11/2026 22:34 · Installation de 2 pilotes ».</summary>
    public string Title
    {
        get
        {
            var i = Intervention;
            var what = i.Kind switch
            {
                InterventionKinds.DeviceRepair => Loc.F("Réparation : {0}", string.Join(", ", i.Items.Select(x => x.Title).Distinct())),
                InterventionKinds.DriverStoreClean => Loc.F("Nettoyage du magasin de pilotes ({0} paquet(s) retiré(s))", i.RemovedDriverPackages.Count),
                _ => i.Items.Count(x => x.Status == nameof(InstallStatus.Installed)) is var n && n == 1
                    ? Loc.T("Installation de 1 pilote")
                    : Loc.F("Installation de {0} pilotes", n),
            };
            return $"{i.Timestamp.ToString(Loc.IsEnglish ? "yyyy-MM-dd HH:mm" : "dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} · {what}";
        }
    }

    /// <summary>« Annulée le … », « Peut être annulée » ou « Rien à annuler ».</summary>
    public string State => Intervention switch
    {
        { RolledBackAt: { } at } => Loc.F("Annulée le {0:dd/MM/yyyy HH:mm}", at),
        { CanRollback: true } => Loc.T("Peut être annulée"),
        _ => Loc.T("Rien à annuler"),
    };

    public string AccessibleName => $"{Title}. {State}";
}

/// <summary>Sauvegarde de pilotes (dossier daté) : libellé lisible, chemin complet en infobulle.</summary>
public sealed class BackupRow
{
    public BackupRow(string path) => Path = path;
    public string Path { get; }
    public string Label => DateTime.TryParseExact(System.IO.Path.GetFileName(Path), "yyyyMMdd-HHmmss",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
        ? Loc.F("Sauvegarde du {0:dd/MM/yyyy} à {0:HH:mm}", date)
        : System.IO.Path.GetFileName(Path);
    public override string ToString() => Label;
}

/// <summary>Constat de santé affiché à l'Accueil (titre simple, détail à la demande).</summary>
public sealed class FindingItem
{
    /// <summary>Débuts de titre produits par le calcul de santé (traduits ; le nom du périphérique reste tel quel).</summary>
    private static readonly string[] TitlePrefixes =
    {
        "Pilote dangereux installé :", "Panne matérielle probable :", "Périphérique désactivé :", "Pilote manquant :",
        "Périphérique en erreur :", "Pilote non signé :", "Pilote ancien :", "Mise à jour BIOS / firmware disponible :",
        "Mise à jour disponible :",
    };

    public FindingItem(HealthFinding finding) => Finding = finding;
    public HealthFinding Finding { get; }
    public string Title
    {
        get
        {
            var prefix = Loc.IsEnglish ? TitlePrefixes.FirstOrDefault(p => Finding.Title.StartsWith(p, StringComparison.Ordinal)) : null;
            return prefix is null ? Finding.Title : Loc.T(prefix) + Finding.Title[prefix.Length..];
        }
    }
    public string Detail => Finding.Detail;
    /// <summary>error, warning, info (icône et couleur).</summary>
    public string Kind => Finding.Severity switch
    {
        HealthSeverity.Critical => "error",
        HealthSeverity.Warning => "warning",
        _ => "info",
    };
    public string SeverityName => Finding.Severity switch
    {
        HealthSeverity.Critical => Loc.T("Critique"),
        HealthSeverity.Warning => Loc.T("Attention"),
        _ => Loc.T("Information"),
    };
}
