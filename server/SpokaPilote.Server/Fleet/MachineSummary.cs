namespace SpokaPilote.Server.Fleet;

/// <summary>
/// Indicateurs d'un poste calculés une fois, à la réception de son dernier rapport, et conservés dans des colonnes de la
/// table « machines » (console 3.2) : listes, filtres, tris, synthèse du parc et règles de groupe s'en servent sans jamais
/// relire ni désérialiser les rapports.
/// </summary>
public sealed class MachineSummary
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Identifiant du poste enregistré par la console (colonne machine_id).</summary>
    public string? MachineId { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public DateTimeOffset? AgentSeen { get; set; }
    public string? LastIp { get; init; }
    public int ReportCount { get; init; }

    /// <summary>Identité du poste (constructeur, modèle, Windows...) telle que déclarée dans son dernier rapport.</summary>
    public SystemSnapshot System { get; init; } = new();
    public string DisplayModel { get; init; } = "";
    public DateTimeOffset LastReport { get; init; }
    public string? AppVersion { get; init; }
    public int DeviceCount { get; init; }
    public int DriverProblems { get; init; }
    public int HardwareFaults { get; init; }
    public int Unresolved { get; init; }
    public int Proposed { get; init; }
    public int Blocked { get; init; }
    public int Installed { get; init; }
    public int Failed { get; init; }
    public int SourceErrors { get; init; }
    public bool AnalysisOnly { get; init; }
    public bool RebootRequired { get; init; }
    public bool Healthy { get; init; }
    public int? HealthScore { get; init; }
    public string? HealthGrade { get; init; }
    public int HealthCritical { get; init; }
    public int HealthWarnings { get; init; }
    public bool DangerousDriver { get; init; }

    /// <summary>Périphériques sans pilote (en erreur, hors panne matérielle, sans pilote autorisé proposé), dans l'ordre du rapport.</summary>
    public IReadOnlyList<DeviceEntry> Missing { get; init; } = [];

    /// <summary>« Microsoft Windows 11 Professionnel 24H2 ».</summary>
    public string Os => $"{System.OsCaption} {System.OsDisplayVersion}".Trim();

    /// <summary>Indicateurs d'un rapport normalisé (mêmes définitions que MachineReport).</summary>
    public static MachineSummary FromReport(MachineReport r, long id = 0, string? name = null, string? machineId = null,
        DateTimeOffset lastSeen = default, int reportCount = 0, DateTimeOffset? agentSeen = null, string? lastIp = null) => new()
    {
        Id = id,
        Name = name ?? r.Name,
        MachineId = machineId ?? r.System.MachineId,
        LastSeen = lastSeen,
        AgentSeen = agentSeen,
        LastIp = lastIp,
        ReportCount = reportCount,
        System = r.System,
        DisplayModel = r.System.DisplayModel,
        LastReport = r.Timestamp,
        AppVersion = r.AppVersion,
        DeviceCount = r.DeviceCount,
        DriverProblems = r.DriverProblems,
        HardwareFaults = r.HardwareFaults,
        Unresolved = r.Unresolved,
        Proposed = r.Proposed,
        Blocked = r.Blocked,
        Installed = r.Installed,
        Failed = r.Failed,
        SourceErrors = r.SourceErrors.Count,
        AnalysisOnly = r.AnalysisOnly,
        RebootRequired = r.RebootRequired,
        Healthy = r.Healthy,
        HealthScore = r.Health?.Score,
        HealthGrade = r.Health?.Grade,
        HealthCritical = r.Health?.Critical ?? 0,
        HealthWarnings = r.Health?.Warnings ?? 0,
        DangerousDriver = r.Health?.HasDangerousDriver ?? false,
        Missing = MissingDevices(r),
    };

    public static MachineSummary From(StoredMachine m) =>
        FromReport(m.Report, m.Id, m.Name, m.MachineId ?? m.Report.System.MachineId, m.LastSeen, m.ReportCount, m.AgentSeen, m.LastIp);

    /// <summary>Périphériques comptés dans « matériel sans pilote » (même règle que FleetAggregator.MissingDrivers).</summary>
    public static List<DeviceEntry> MissingDevices(MachineReport r) =>
        r.ProblemDevices.Where(d => !d.IsHardwareFault && !d.HasCandidate).ToList();
}
