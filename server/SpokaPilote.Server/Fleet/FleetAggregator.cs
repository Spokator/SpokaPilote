namespace SpokaPilote.Server.Fleet;

/// <summary>Poste connu de la console : dernier rapport reçu et date de dernier contact.</summary>
public sealed record StoredMachine(string Name, DateTimeOffset LastSeen, int ReportCount, MachineReport Report);

/// <summary>Ligne de la liste des postes (indicateurs uniquement, sans le détail des périphériques).</summary>
public sealed class MachineRow
{
    public string Name { get; init; } = "";
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
