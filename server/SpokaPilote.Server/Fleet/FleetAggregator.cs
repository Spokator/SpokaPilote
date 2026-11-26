namespace SpokaPilote.Server.Fleet;

/// <summary>Poste connu de la console : dernier rapport reçu et date de dernier contact.</summary>
/// <param name="Id">Clé interne du poste (stable même si le poste change de nom).</param>
/// <param name="MachineId">Identifiant Windows du poste (MachineGuid), null pour un poste qui n'a envoyé que des rapports antérieurs à la 3.0.</param>
/// <param name="AgentSeen">Dernière interrogation de la file de tâches par l'agent du poste (null = agent jamais vu).</param>
/// <param name="LastIp">Adresse IP d'envoi du dernier rapport, vue par la console (règles de groupe « sous-réseau »).</param>
public sealed record StoredMachine(string Name, DateTimeOffset LastSeen, int ReportCount, MachineReport Report,
    long Id = 0, string? MachineId = null, DateTimeOffset? AgentSeen = null, string? LastIp = null);

/// <summary>Ligne de la liste des postes (indicateurs uniquement, sans le détail des périphériques).</summary>
public sealed class MachineRow
{
    public string Name { get; init; } = "";
    public string? MachineId { get; init; }
    public string Model { get; init; } = "";
    public string? SerialNumber { get; init; }
    public string? Os { get; init; }
    public string? OsVersion { get; init; }
    public string? Architecture { get; init; }
    public string? AppVersion { get; init; }
    public DateTimeOffset LastReport { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public int ReportCount { get; init; }
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
    /// <summary>Poste qui n'a pas envoyé de rapport depuis le délai configuré (7 jours par défaut).</summary>
    public bool Stale { get; init; }
    /// <summary>Score de santé 0-100 (null = rapport sans objet « health »).</summary>
    public int? HealthScore { get; init; }
    public string? HealthGrade { get; init; }
    public int HealthCritical { get; init; }
    public int HealthWarnings { get; init; }
    /// <summary>Un pilote connu pour être dangereux (constat critique « known-bad-driver ») est présent.</summary>
    public bool DangerousDriver { get; init; }
    /// <summary>Dernière interrogation de la file de tâches par l'agent (null = agent jamais vu).</summary>
    public DateTimeOffset? AgentSeen { get; set; }
    /// <summary>Tâches à distance en attente ou en cours pour ce poste (renseigné par l'API).</summary>
    public int ActiveJobs { get; set; }
    /// <summary>Adresse IP d'envoi du dernier rapport (vue par la console).</summary>
    public string? LastIp { get; init; }
    /// <summary>Groupes dont le poste fait partie (identifiants ; renseigné par l'API).</summary>
    public List<long> Groups { get; set; } = new();

    /// <summary>Copie superficielle (les synthèses en cache ne sont jamais modifiées).</summary>
    public MachineRow Copy() => (MachineRow)MemberwiseClone();
}

/// <summary>Matériel sans pilote trouvé, agrégé par ID matériel sur tout le parc : la liste de ce qu'il reste à trouver.</summary>
public sealed class MissingHardware
{
    public string HardwareId { get; init; } = "";
    public string Name { get; init; } = "";
    public int ErrorCode { get; init; }
    public string? StatusText { get; init; }
    public string? Hint { get; init; }
    public List<string> Machines { get; init; } = new();
    public List<string> Models { get; init; } = new();
    /// <summary>Nombre de postes où un pilote existe mais est bloqué par la politique.</summary>
    public int BlockedOn { get; init; }
    public int Count => Machines.Count;
}

public sealed class ModelRow
{
    public string Model { get; init; } = "";
    public int Count { get; init; }
    public int ToProcess { get; init; }
}

public sealed class FleetSummary
{
    public DateTimeOffset GeneratedAt { get; init; }
    public int StaleDays { get; init; }
    public int MachineCount { get; init; }
    public int Healthy { get; init; }
    public int ToProcess { get; init; }
    public int Stale { get; init; }
    public int ModelCount { get; init; }
    public int DriverProblems { get; init; }
    public int HardwareFaults { get; init; }
    public int Unresolved { get; init; }
    public int Proposed { get; init; }
    public int Blocked { get; init; }
    public int Installed { get; init; }
    public int Failed { get; init; }
    public int RebootRequired { get; init; }
    /// <summary>Santé moyenne des postes qui la transmettent (null = aucun).</summary>
    public double? AverageHealth { get; init; }
    public int HealthMachines { get; init; }
    public int DangerousDrivers { get; init; }
    /// <summary>Tâches à distance (renseigné par l'API).</summary>
    public int PendingJobs { get; set; }
    public int RunningJobs { get; set; }
    public int FailedJobs24h { get; set; }
    public List<MachineRow> ToProcessMachines { get; init; } = new();
    public List<MachineRow> StaleMachines { get; init; } = new();
    public List<MissingHardware> MissingDrivers { get; init; } = new();
    public List<ModelRow> Models { get; init; } = new();
    /// <summary>Nombre de types de matériel sans pilote (3.2 : la liste peut être tronquée par ?top=).</summary>
    public int MissingDriverCount => MissingDriverKinds ?? MissingDrivers.Count;

    /// <summary>
    /// Copie pour une réponse : listes éventuellement limitées aux <paramref name="top"/> premiers éléments (la liste
    /// complète reste la valeur par défaut de l'API), lignes recopiées puis complétées par <paramref name="row"/>. La
    /// synthèse mise en cache n'est jamais modifiée.
    /// </summary>
    public FleetSummary Copy(int? top, Action<MachineRow> row)
    {
        List<T> Take<T>(List<T> list) => top is { } n && list.Count > n ? list.GetRange(0, n) : list;
        List<MachineRow> Rows(List<MachineRow> list) => Take(list).Select(r => { var copy = r.Copy(); row(copy); return copy; }).ToList();
        return new FleetSummary
        {
            GeneratedAt = GeneratedAt, StaleDays = StaleDays, MachineCount = MachineCount, Healthy = Healthy, ToProcess = ToProcess, Stale = Stale,
            ModelCount = ModelCount, DriverProblems = DriverProblems, HardwareFaults = HardwareFaults, Unresolved = Unresolved, Proposed = Proposed,
            Blocked = Blocked, Installed = Installed, Failed = Failed, RebootRequired = RebootRequired, AverageHealth = AverageHealth,
            HealthMachines = HealthMachines, DangerousDrivers = DangerousDrivers, PendingJobs = PendingJobs, RunningJobs = RunningJobs,
            FailedJobs24h = FailedJobs24h,
            ToProcessMachines = Rows(ToProcessMachines),
            StaleMachines = Rows(StaleMachines),
            MissingDrivers = top is null ? MissingDrivers : Take(MissingDrivers),
            MissingDriverKinds = MissingDrivers.Count,
            Models = Models,
        };
    }

    /// <summary>Nombre total de types de matériel sans pilote quand la liste est tronquée (null : liste complète).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? MissingDriverKinds { get; private init; }
}

/// <summary>
/// Calculs de synthèse du parc, sans état (testables indépendamment du stockage). Ils portent sur les indicateurs
/// précalculés des postes (MachineSummary) ; les surcharges StoredMachine (rapport complet) donnent le même résultat.
/// </summary>
public static class FleetAggregator
{
    public static MachineRow ToRow(StoredMachine m, DateTimeOffset now, int staleDays) => ToRow(MachineSummary.From(m), now, staleDays);

    public static MachineRow ToRow(MachineSummary m, DateTimeOffset now, int staleDays)
    {
        var s = m.System;
        return new MachineRow
        {
            Name = m.Name,
            MachineId = m.MachineId,
            Model = m.DisplayModel,
            SerialNumber = s.SerialNumber,
            Os = m.Os,
            OsVersion = s.OsVersion,
            Architecture = s.Architecture,
            AppVersion = m.AppVersion,
            LastReport = m.LastReport,
            LastSeen = m.LastSeen,
            ReportCount = m.ReportCount,
            DeviceCount = m.DeviceCount,
            DriverProblems = m.DriverProblems,
            HardwareFaults = m.HardwareFaults,
            Unresolved = m.Unresolved,
            Proposed = m.Proposed,
            Blocked = m.Blocked,
            Installed = m.Installed,
            Failed = m.Failed,
            SourceErrors = m.SourceErrors,
            AnalysisOnly = m.AnalysisOnly,
            RebootRequired = m.RebootRequired,
            Healthy = m.Healthy,
            Stale = IsStale(m.LastSeen, now, staleDays),
            HealthScore = m.HealthScore,
            HealthGrade = m.HealthGrade,
            HealthCritical = m.HealthCritical,
            HealthWarnings = m.HealthWarnings,
            DangerousDriver = m.DangerousDriver,
            AgentSeen = m.AgentSeen,
            LastIp = m.LastIp,
        };
    }

    public static bool IsStale(DateTimeOffset lastSeen, DateTimeOffset now, int staleDays) =>
        staleDays > 0 && now - lastSeen > TimeSpan.FromDays(staleDays);

    /// <summary>Ordre historique de la liste des postes : à traiter d'abord (échecs, pilotes en erreur), puis par nom.</summary>
    public static IOrderedEnumerable<MachineRow> DefaultOrder(IEnumerable<MachineRow> rows) =>
        rows.OrderBy(r => r.Healthy)
            .ThenByDescending(r => r.Failed)
            .ThenByDescending(r => r.DriverProblems)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Postes à traiter d'abord (échecs, pilotes en erreur), puis par nom.</summary>
    public static List<MachineRow> Rows(IEnumerable<StoredMachine> machines, DateTimeOffset now, int staleDays) =>
        Rows(machines.Select(MachineSummary.From), now, staleDays);

    public static List<MachineRow> Rows(IEnumerable<MachineSummary> machines, DateTimeOffset now, int staleDays) =>
        DefaultOrder(machines.Select(m => ToRow(m, now, staleDays))).ToList();

    public static List<MissingHardware> MissingDrivers(IEnumerable<StoredMachine> machines) => MissingDrivers(machines.Select(MachineSummary.From));

    /// <summary>Matériel sans pilote agrégé par ID matériel (ordre des postes = ordre reçu, par nom pour le stockage).</summary>
    public static List<MissingHardware> MissingDrivers(IEnumerable<MachineSummary> machines) =>
        machines
            .SelectMany(m => m.Missing.Select(d => (Machine: m, Device: d)))
            .GroupBy(x => x.Device.AggregationKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First().Device;
                return new MissingHardware
                {
                    HardwareId = g.Key,
                    Name = first.Name,
                    ErrorCode = first.ErrorCode,
                    StatusText = first.StatusText,
                    Hint = g.Select(x => x.Device.Hint).FirstOrDefault(h => h is not null),
                    Machines = g.Select(x => x.Machine.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(),
                    Models = g.Select(x => x.Machine.DisplayModel).Distinct().OrderBy(n => n).ToList(),
                    BlockedOn = g.Where(x => x.Device.OnlyBlockedCandidates).Select(x => x.Machine.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                };
            })
            .OrderByDescending(h => h.Count)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static FleetSummary Summarize(IReadOnlyCollection<StoredMachine> machines, DateTimeOffset now, int staleDays) =>
        SummarizeFleet(machines.Select(MachineSummary.From).ToList(), now, staleDays);

    public static FleetSummary SummarizeFleet(IReadOnlyCollection<MachineSummary> machines, DateTimeOffset now, int staleDays)
    {
        var rows = Rows(machines, now, staleDays);
        var scored = rows.Where(r => r.HealthScore is not null).ToList();
        return new FleetSummary
        {
            GeneratedAt = now,
            StaleDays = staleDays,
            MachineCount = rows.Count,
            Healthy = rows.Count(r => r.Healthy),
            ToProcess = rows.Count(r => !r.Healthy),
            Stale = rows.Count(r => r.Stale),
            ModelCount = rows.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            DriverProblems = rows.Sum(r => r.DriverProblems),
            HardwareFaults = rows.Sum(r => r.HardwareFaults),
            Unresolved = rows.Sum(r => r.Unresolved),
            Proposed = rows.Sum(r => r.Proposed),
            Blocked = rows.Sum(r => r.Blocked),
            Installed = rows.Sum(r => r.Installed),
            Failed = rows.Sum(r => r.Failed),
            RebootRequired = rows.Count(r => r.RebootRequired),
            AverageHealth = scored.Count == 0 ? null : Math.Round(scored.Average(r => r.HealthScore!.Value), 1),
            HealthMachines = scored.Count,
            DangerousDrivers = rows.Count(r => r.DangerousDriver),
            ToProcessMachines = rows.Where(r => !r.Healthy).ToList(),
            StaleMachines = rows.Where(r => r.Stale).OrderBy(r => r.LastSeen).ToList(),
            MissingDrivers = MissingDrivers(machines),
            Models = rows.GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ModelRow { Model = g.First().Model, Count = g.Count(), ToProcess = g.Count(r => !r.Healthy) })
                .OrderByDescending(m => m.Count).ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
