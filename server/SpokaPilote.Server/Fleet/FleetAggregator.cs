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
    public DateTimeOffset? AgentSeen { get; init; }
    /// <summary>Tâches à distance en attente ou en cours pour ce poste (renseigné par l'API).</summary>
    public int ActiveJobs { get; set; }
    /// <summary>Adresse IP d'envoi du dernier rapport (vue par la console).</summary>
    public string? LastIp { get; init; }
    /// <summary>Groupes dont le poste fait partie (identifiants ; renseigné par l'API).</summary>
    public List<long> Groups { get; set; } = new();
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
}

/// <summary>Calculs de synthèse du parc, sans état (testables indépendamment du stockage).</summary>
public static class FleetAggregator
{
    public static MachineRow ToRow(StoredMachine m, DateTimeOffset now, int staleDays)
    {
        var r = m.Report;
        var s = r.System;
        return new MachineRow
        {
            Name = m.Name,
            MachineId = m.MachineId ?? s.MachineId,
            Model = s.DisplayModel,
            SerialNumber = s.SerialNumber,
            Os = $"{s.OsCaption} {s.OsDisplayVersion}".Trim(),
            OsVersion = s.OsVersion,
            Architecture = s.Architecture,
            AppVersion = r.AppVersion,
            LastReport = r.Timestamp,
            LastSeen = m.LastSeen,
            ReportCount = m.ReportCount,
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
            Stale = IsStale(m.LastSeen, now, staleDays),
            HealthScore = r.Health?.Score,
            HealthGrade = r.Health?.Grade,
            HealthCritical = r.Health?.Critical ?? 0,
            HealthWarnings = r.Health?.Warnings ?? 0,
            DangerousDriver = r.Health?.HasDangerousDriver ?? false,
            AgentSeen = m.AgentSeen,
            LastIp = m.LastIp,
        };
    }

    public static bool IsStale(DateTimeOffset lastSeen, DateTimeOffset now, int staleDays) =>
        staleDays > 0 && now - lastSeen > TimeSpan.FromDays(staleDays);

    /// <summary>Postes à traiter d'abord (échecs, pilotes en erreur), puis par nom.</summary>
    public static List<MachineRow> Rows(IEnumerable<StoredMachine> machines, DateTimeOffset now, int staleDays) =>
        machines.Select(m => ToRow(m, now, staleDays))
            .OrderBy(r => r.Healthy)
            .ThenByDescending(r => r.Failed)
            .ThenByDescending(r => r.DriverProblems)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<MissingHardware> MissingDrivers(IEnumerable<StoredMachine> machines) =>
        machines
            .SelectMany(m => m.Report.ProblemDevices
                .Where(d => !d.IsHardwareFault && !d.HasCandidate)
                .Select(d => (Machine: m, Device: d)))
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
                    Models = g.Select(x => x.Machine.Report.System.DisplayModel).Distinct().OrderBy(n => n).ToList(),
                    BlockedOn = g.Where(x => x.Device.OnlyBlockedCandidates).Select(x => x.Machine.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                };
            })
            .OrderByDescending(h => h.Count)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static FleetSummary Summarize(IReadOnlyCollection<StoredMachine> machines, DateTimeOffset now, int staleDays)
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
