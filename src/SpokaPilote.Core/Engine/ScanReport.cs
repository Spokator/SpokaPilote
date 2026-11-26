using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;
using SpokaPilote.Core.Policy;

namespace SpokaPilote.Core.Engine;

public sealed class ReportCandidate
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public DateTime? Date { get; set; }
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Category { get; set; }
    public long? SizeBytes { get; set; }
    public List<string> Devices { get; set; } = new();
    public List<string> DeviceIds { get; set; } = new();
    public string? MatchedHardwareId { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InfoUrl { get; set; }
    /// <summary>Motif de blocage par la politique de pilotes (null = autorisé).</summary>
    public string? BlockedReason { get; set; }
}

/// <summary>Politique de pilotes appliquée lors de l'analyse.</summary>
public sealed class ReportPolicy
{
    /// <summary>Nom de la politique de l'organisation (null = aucune).</summary>
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? UpdatedAt { get; set; }
    /// <summary>Chemin ou URL d'origine.</summary>
    public string? Origin { get; set; }
    public bool FromCache { get; set; }
    public int OrganizationRules { get; set; }
    /// <summary>Anneau de déploiement du poste indiqué par la console 4.0 (null : pas de diffusion par anneaux).</summary>
    public string? RolloutRing { get; set; }
    /// <summary>Pilotes retenus par la diffusion par anneaux (compris dans BlockedCandidates).</summary>
    public int RolloutHeldCandidates { get; set; }
    public string? ReferenceVersion { get; set; }
    public int ReferenceRules { get; set; }
    public int BlockedCandidates { get; set; }
    /// <summary>Candidats retirés de la liste par une règle « hide ».</summary>
    public List<string> HiddenCandidates { get; set; } = new();
    public List<string> Messages { get; set; } = new();
}

public sealed class ReportOutcome
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public bool RebootRequired { get; set; }
}

/// <summary>
/// Rapport d'analyse au format JSON, lisible par les humains, les scripts… et par SpokaPilote lui-même :
/// il sert à préparer un pack de pilotes hors ligne pour un autre poste et à consolider un rapport de parc.
/// </summary>
public sealed class ScanReport
{
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;
    public string GeneratedBy { get; set; } = "SpokaPilote";
    public string? AppVersion { get; set; }
    public DateTime Timestamp { get; set; }
    public double DurationSeconds { get; set; }
    public SystemInfo System { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public Dictionary<string, string> SourceErrors { get; set; } = new();
    public List<ReportCandidate> Candidates { get; set; } = new();
    public ReportPolicy? Policy { get; set; }
    /// <summary>Santé des pilotes (facultatif) : { score, grade, findings: [ { severity, code, title, detail, deviceId } ] }.</summary>
    public HealthReport? Health { get; set; }
    /// <summary>
    /// État du BIOS et des firmwares (facultatif, absent des rapports antérieurs et des postes sans catalogue
    /// constructeur) : { bios: { vendor, state, installedVersion, latestVersion, importance, downloadUrl, infoUrl... },
    /// components: [ ... ], notes: [ ... ] }.
    /// </summary>
    public FirmwareStatus? Firmware { get; set; }
    public List<ReportOutcome>? Installation { get; set; }
    public List<DeviceInfo> Devices { get; set; } = new();

    /// <summary>Périphériques présents en erreur (calculé ; ignoré à la relecture).</summary>
    public List<string> ProblemDevices => Devices.Where(d => d.IsPresent && d.HasProblem).Select(d => d.Name).ToList();

    [JsonIgnore]
    public IEnumerable<DeviceInfo> UnresolvedDeviceInfos =>
        Devices.Where(d => d.IsPresent && d.HasProblem &&
                           !Candidates.Any(c => c.DeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)));

    public List<string> UnresolvedDevices => UnresolvedDeviceInfos.Select(d => d.Name).ToList();

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ScanReport From(ScanResult scan, IReadOnlyList<InstallOutcome>? outcomes = null) => new()
    {
        AppVersion = UpdateChecker.CurrentVersion.ToString(3),
        Timestamp = scan.Timestamp,
        DurationSeconds = Math.Round(scan.Duration.TotalSeconds),
        System = scan.System,
        Sources = scan.SourcesUsed,
        SourceErrors = scan.SourceErrors,
        Devices = scan.Devices,
        Health = scan.Health,
        Firmware = scan.Firmware,
        Candidates = scan.Candidates.Select(c => new ReportCandidate
        {
            Title = c.Title,
            Version = c.Version,
            Date = c.Date,
            Source = c.SourceLabel,
            Kind = c.KindLabel,
            Category = c.Category,
            SizeBytes = c.SizeBytes,
            Devices = c.TargetDeviceNames.Distinct().ToList(),
            DeviceIds = c.TargetDeviceIds.ToList(),
            MatchedHardwareId = c.MatchedHardwareId,
            DownloadUrl = c.DownloadUrl,
            InfoUrl = c.InfoUrl,
            BlockedReason = c.BlockedReason,
        }).ToList(),
        Policy = scan.Policy is not { } p ? null : new ReportPolicy
        {
            Name = p.Organization?.Name,
            Version = p.Organization?.Version,
            UpdatedAt = p.Organization?.UpdatedAt,
            Origin = p.OrganizationOrigin,
            FromCache = p.FromCache,
            OrganizationRules = p.Organization?.Rules.Count(r => !r.IsRollout) ?? 0,
            RolloutRing = p.Organization?.Rollout is { Enabled: true } ro ? ro.Ring ?? "(anneau le plus prudent)" : null,
            RolloutHeldCandidates = scan.Candidates.Count(c => c.HeldByRollout),
            ReferenceVersion = p.Reference?.Version,
            ReferenceRules = p.ReferenceRuleCount,
            BlockedCandidates = scan.Candidates.Count(c => c.BlockedReason is not null),
            HiddenCandidates = scan.PolicyDecisions.Where(d => d.Decision.Verdict == PolicyVerdict.Hidden)
                .Select(d => $"{d.Candidate.Title} {d.Candidate.Version} — {d.Decision.Reason}".Trim()).ToList(),
            Messages = p.Messages.ToList(),
        },
        Installation = outcomes?.Select(o => new ReportOutcome
        {
            Title = o.Candidate.Title,
            Version = o.Candidate.Version,
            Status = o.Status.ToString(),
            Message = o.Message,
            RebootRequired = o.RebootRequired,
        }).ToList(),
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ScanReport Load(string path)
    {
        var report = JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(path), Json)
                     ?? throw new InvalidDataException($"Rapport vide : {path}");
        if (report.Schema < CurrentSchema || report.Devices.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} n'est pas un rapport SpokaPilote complet (format {report.Schema}). " +
                                           "Régénérez-le avec « spokapilote scan --json ».");
        return report;
    }
}
