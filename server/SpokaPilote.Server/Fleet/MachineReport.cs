namespace SpokaPilote.Server.Fleet;

/// <summary>Identité matérielle et logicielle du poste, telle que déclarée dans son rapport.</summary>
public sealed class SystemSnapshot
{
    public string ComputerName { get; set; } = "";
    /// <summary>
    /// Identifiant stable du poste (MachineGuid de Windows, en minuscules), absent des rapports antérieurs à la 3.0.
    /// Il survit à un changement de nom : la console s'en sert en priorité pour reconnaître le poste.
    /// </summary>
    public string? MachineId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? FriendlyModel { get; set; }
    public string? SerialNumber { get; set; }
    public string? BiosVersion { get; set; }
    public string? OsCaption { get; set; }
    public string? OsDisplayVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }

    /// <summary>« Constructeur Modèle » lisible (nom commercial si connu, ex. « LENOVO ThinkPad T14 Gen 2 (20W0) »).</summary>
    public string DisplayModel
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(FriendlyModel) ? Model ?? "" : $"{FriendlyModel} ({Model})";
            // « HP HP EliteDesk 800 » → « HP EliteDesk 800 » : certains constructeurs répètent leur nom dans le modèle.
            var brand = Manufacturer?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (brand is not null && model.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase)) return model.Trim();
            return $"{Manufacturer} {model}".Trim();
        }
    }
}

/// <summary>Périphérique présent en erreur (les périphériques sains ne sont pas conservés, seulement comptés).</summary>
public sealed class DeviceEntry
{
    public string Name { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public List<string> HardwareIds { get; set; } = new();
    public int ErrorCode { get; set; }
    public string? StatusText { get; set; }
    public string? Hint { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverProvider { get; set; }
    /// <summary>Panne matérielle (USB qui ne répond pas...) : aucun pilote ne peut la corriger.</summary>
    public bool IsHardwareFault { get; set; }
    /// <summary>Un pilote autorisé (non bloqué) est proposé pour ce périphérique.</summary>
    public bool HasCandidate { get; set; }
    /// <summary>Seuls des pilotes bloqués par la politique sont proposés pour ce périphérique.</summary>
    public bool OnlyBlockedCandidates { get; set; }

    /// <summary>Clé d'agrégation sur le parc : premier ID matériel (le plus précis), à défaut le nom.</summary>
    public string AggregationKey => HardwareIds.FirstOrDefault() ?? Name;
}

public sealed class CandidateEntry
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string? Source { get; set; }
    public string? Kind { get; set; }
    public string? Category { get; set; }
    public List<string> Devices { get; set; } = new();
    public List<string> DeviceIds { get; set; } = new();
    /// <summary>Motif de blocage par la politique de pilotes (null = autorisé).</summary>
    public string? BlockedReason { get; set; }
    public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockedReason);
}

public sealed class OutcomeEntry
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public bool RebootRequired { get; set; }
    public bool IsFailure => Status is "Failed" or "Blocked";
}

/// <summary>Constat de l'analyse de santé du poste (objet facultatif « health » des rapports).</summary>
public sealed class HealthFinding
{
    /// <summary>« critical », « warning » ou « info ».</summary>
    public string Severity { get; set; } = "info";
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string? DeviceId { get; set; }

    public const string KnownBadDriverCode = "known-bad-driver";

    /// <summary>Ordre d'affichage : critique d'abord.</summary>
    public int Rank => Severity switch { "critical" => 0, "warning" => 1, _ => 2 };
    public bool IsDangerousDriver => Severity == "critical" && Code.Equals(KnownBadDriverCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Santé du poste, calculée par le poste (contrat partagé : score 0-100, note A à E, constats). Facultative : sans elle,
/// la console se comporte exactement comme avant.
/// </summary>
public sealed class HealthInfo
{
    public int? Score { get; set; }
    public string? Grade { get; set; }
    public List<HealthFinding> Findings { get; set; } = new();

    public int Critical => Findings.Count(f => f.Severity == "critical");
    public int Warnings => Findings.Count(f => f.Severity == "warning");
    public bool HasDangerousDriver => Findings.Any(f => f.IsDangerousDriver);
}

/// <summary>
/// Rapport normalisé d'un poste : uniquement ce qui sert à la console (quelques Ko au lieu des ~200 Ko du rapport brut),
/// avec les indicateurs déjà calculés.
/// </summary>
public sealed class MachineReport
{
    public string Name { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string? AppVersion { get; set; }
    public double DurationSeconds { get; set; }
    public SystemSnapshot System { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public Dictionary<string, string> SourceErrors { get; set; } = new();
    /// <summary>Nombre de périphériques présents sur le poste.</summary>
    public int DeviceCount { get; set; }
    public List<DeviceEntry> ProblemDevices { get; set; } = new();
    public List<CandidateEntry> Candidates { get; set; } = new();
    /// <summary>Résultats d'installation (null = analyse seule).</summary>
    public List<OutcomeEntry>? Installation { get; set; }
    /// <summary>Santé du poste (null = rapport sans objet « health »).</summary>
    public HealthInfo? Health { get; set; }

    /// <summary>Périphériques en erreur à cause d'un pilote (hors pannes matérielles).</summary>
    public int DriverProblems => ProblemDevices.Count(d => !d.IsHardwareFault);
    public int HardwareFaults => ProblemDevices.Count(d => d.IsHardwareFault);
    /// <summary>Périphériques en erreur pour lesquels aucun pilote autorisé n'a été trouvé (hors pannes matérielles).</summary>
    public int Unresolved => ProblemDevices.Count(d => !d.IsHardwareFault && !d.HasCandidate);
    public int Proposed => Candidates.Count(c => !c.IsBlocked);
    public int Blocked => Candidates.Count(c => c.IsBlocked);
    public int Installed => Installation?.Count(o => o.Status == "Installed") ?? 0;
    public int Failed => Installation?.Count(o => o.IsFailure) ?? 0;
    public bool RebootRequired => Installation?.Any(o => o.RebootRequired) ?? false;
    public bool AnalysisOnly => Installation is null;
    /// <summary>Même définition que « spokapilote fleet-report » : aucun pilote en erreur, aucun échec d'installation.</summary>
    public bool Healthy => DriverProblems == 0 && Failed == 0;
}
