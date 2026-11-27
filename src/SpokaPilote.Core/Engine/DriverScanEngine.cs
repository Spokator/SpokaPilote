using System.Diagnostics;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;
using SpokaPilote.Core.Sources;
using SpokaPilote.Core.Sources.Gpu;
using SpokaPilote.Core.Sources.Oem;

namespace SpokaPilote.Core.Engine;

public sealed class ScanResult
{
    public required SystemInfo System { get; init; }
    public required List<DeviceInfo> Devices { get; init; }
    public required List<DriverCandidate> Candidates { get; init; }
    public Dictionary<string, string> SourceErrors { get; init; } = new();
    public List<string> SourcesUsed { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    /// <summary>Politique de pilotes appliquée (organisation + liste de référence), avec son origine.</summary>
    public LoadedPolicy? Policy { get; init; }
    /// <summary>Décision de la politique pour chaque candidat proposé, y compris ceux retirés par une règle « hide ».</summary>
    public List<CandidateDecision> PolicyDecisions { get; init; } = new();
    /// <summary>Santé des pilotes (score, note, constats), calculée en fin d'analyse ; null si le calcul a échoué.</summary>
    public HealthReport? Health { get; init; }
    /// <summary>État du BIOS et des firmwares (catalogue du constructeur) ; null si aucune source constructeur n'a été interrogée.</summary>
    public FirmwareStatus? Firmware { get; init; }

    public IEnumerable<DeviceInfo> ProblemDevices => Devices.Where(d => d.IsPresent && d.HasProblem);

    /// <summary>Périphériques en erreur pour lesquels aucune source n'a trouvé de pilote.</summary>
    public IEnumerable<DeviceInfo> UnresolvedDevices =>
        ProblemDevices.Where(d => !Candidates.Any(c => c.TargetDeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)));
}

/// <summary>
/// Orchestrateur : inventaire → interrogation de toutes les sources de confiance en parallèle → fusion.
/// Priorité en cas de doublon : dépôt interne &gt; constructeur du PC ou fabricant de la carte graphique
/// &gt; Windows Update &gt; catalogue Microsoft.
/// La politique de pilotes est appliquée avant la fusion : les sources non autorisées ne sont pas interrogées,
/// les candidats bloqués restent visibles (BlockedReason renseigné) sauf règle « hide », et un candidat autorisé
/// d'une autre source passe devant un candidat bloqué pour le même périphérique.
/// </summary>
public sealed class DriverScanEngine
{
    private readonly Settings _settings;
    private readonly List<IDriverSource> _sources;
    private readonly PolicyLoader? _policyLoader;

    public DriverScanEngine(Settings settings, IEnumerable<IDriverSource>? sources = null, PolicyLoader? policyLoader = null)
    {
        _settings = settings;
        _policyLoader = policyLoader;
        _sources = sources?.ToList() ?? new List<IDriverSource>
        {
            new LocalRepositorySource(),
            new DellSource(),
            new HpSource(),
            new LenovoSource(),
            new NvidiaSource(),
            new IntelGraphicsSource(),
            new AmdSource(),
            new WindowsUpdateSource(),
            new MicrosoftCatalogSource(),
        };
    }

    public static int Priority(DriverSourceKind kind) => kind switch
    {
        DriverSourceKind.LocalRepository => 0,
        DriverSourceKind.Dell or DriverSourceKind.HP or DriverSourceKind.Lenovo => 1,
        DriverSourceKind.Nvidia or DriverSourceKind.Intel or DriverSourceKind.Amd => 1,
        DriverSourceKind.WindowsUpdate => 2,
        DriverSourceKind.MicrosoftCatalog => 3,
        _ => 9,
    };

    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Chargement de la politique en parallèle de l'inventaire (ne lève jamais d'exception hors annulation).
        var loader = _policyLoader ?? new PolicyLoader(_settings);
        var policyTask = loader.LoadAsync(ct);
        progress?.Report(new ScanProgress("Inventaire", "Identification de la machine..."));
        var system = await Task.Run(SystemInfoProvider.Collect, ct).ConfigureAwait(false);
        Log.Info($"Machine : {system.Manufacturer} {system.DisplayModel} — {system.OsCaption} {system.OsDisplayVersion} ({system.OsVersion}, {system.Architecture})");

        progress?.Report(new ScanProgress("Inventaire", "Inventaire des composants..."));
        var devices = await Task.Run(DeviceInventory.Collect, ct).ConfigureAwait(false);
        Log.Info($"{devices.Count} périphérique(s), dont {devices.Count(d => d.IsPresent && d.HasProblem)} en erreur.");

        var context = new ScanContext
        {
            System = system,
            Devices = devices,
            Settings = _settings,
            Options = options,
            Progress = progress,
        };

        progress?.Report(new ScanProgress("Politique", "Chargement de la politique de pilotes..."));
        var policy = await policyTask.ConfigureAwait(false);
        foreach (var source in _sources.Where(s => !policy.Effective.AllowsSource(s.Kind)))
            Log.Info($"Source {source.Name} non autorisée par la politique de pilotes : non interrogée.");

        var active = _sources
            .Where(s => options.OnlySources is null || options.OnlySources.Contains(s.Kind))
            .Where(s => policy.Effective.AllowsSource(s.Kind))
            .Where(s => s.IsEnabled(context))
            .ToList();

        var errors = new Dictionary<string, string>();
        var all = new List<DriverCandidate>();
        var tasks = active.Select(async source =>
        {
            try
            {
                var found = await source.FindAsync(context, ct).ConfigureAwait(false);
                lock (all) all.AddRange(found);
                progress?.Report(new ScanProgress(source.Name, $"{source.Name} : {found.Count} résultat(s)"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Source {source.Name} en échec", ex);
                lock (errors) errors[source.Name] = ex.Message;
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (source, warning) in context.Warnings) errors.TryAdd(source, warning);

        if (policy.Organization?.Rollout is { Decisions: true })
            progress?.Report(new ScanProgress("Politique", "Diffusion par anneaux : décisions de la console..."));
        var (merged, decisions) = await ApplyPolicyAsync(loader, policy, all, devices, ct).ConfigureAwait(false);
        var firmware = context.BuildFirmwareStatus();
        var health = ComputeHealth(devices, merged, policy.Effective, firmware);
        sw.Stop();
        var blocked = merged.Count(c => c.BlockedReason is not null);
        var hidden = decisions.Count(d => d.Decision.Verdict == PolicyVerdict.Hidden);
        Log.Info($"Analyse terminée en {sw.Elapsed.TotalSeconds:0} s : {merged.Count} pilote(s) proposé(s)" +
                 (blocked + hidden > 0 ? $", dont {blocked} bloqué(s) par la politique ({hidden} autre(s) masqué(s))." : "."));

        return new ScanResult
        {
            System = system,
            Devices = devices,
            Candidates = merged,
            SourceErrors = errors,
            SourcesUsed = active.Select(s => s.Name).ToList(),
            Duration = sw.Elapsed,
            Policy = policy,
            PolicyDecisions = decisions,
            Health = health,
            Firmware = firmware,
        };
    }

    /// <summary>Santé des pilotes du poste : ne fait jamais échouer l'analyse.</summary>
    private static HealthReport? ComputeHealth(IReadOnlyList<DeviceInfo> devices, IReadOnlyList<DriverCandidate> candidates, DriverPolicy policy, FirmwareStatus? firmware)
    {
        try
        {
            return DriverHealth.Compute(devices, candidates, policy, new HealthOptions
            {
                OriginalInfNames = DriverStoreCleaner.ReadOriginalInfNames(),
                Interventions = InterventionHistory.List(),
                Firmware = firmware,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Calcul de la santé des pilotes impossible : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Politique de l'analyse appliquée aux propositions trouvées. Diffusion par anneaux (console 4.0 en mode décisions) :
    /// une décision par proposition, demandée à la console avant d'appliquer la politique — coût proportionnel aux
    /// propositions de CE poste, pas au catalogue de pilotes du parc ; console injoignable → dernières décisions connues, et
    /// retenue prudente des autres. Sinon (fichier, partage, console antérieure) : <see cref="ApplyPolicy"/> seul.
    /// </summary>
    public static async Task<(List<DriverCandidate> Candidates, List<CandidateDecision> Decisions)> ApplyPolicyAsync(PolicyLoader loader,
        LoadedPolicy policy, IEnumerable<DriverCandidate> candidates, IReadOnlyList<DeviceInfo> devices, CancellationToken ct, DateTime? now = null)
    {
        var list = candidates.ToList();
        if (policy.Organization?.Rollout is { Decisions: true })
            policy.Effective.Decisions = await loader.DecideAsync(policy, list, ct).ConfigureAwait(false);
        return ApplyPolicy(list, devices, policy.Effective, now);
    }

    /// <summary>
    /// Applique la politique (BlockedReason, retrait des candidats « hide ») puis fusionne les sources.
    /// Les décisions retournées concernent les candidats retenus et ceux masqués par la politique.
    /// </summary>
    public static (List<DriverCandidate> Candidates, List<CandidateDecision> Decisions) ApplyPolicy(
        IEnumerable<DriverCandidate> candidates, IReadOnlyList<DeviceInfo> devices, DriverPolicy policy, DateTime? now = null)
    {
        var list = candidates.ToList();
        var decisions = policy.Apply(list, devices, now);
        var merged = Merge(list);
        var kept = new HashSet<DriverCandidate>(merged, ReferenceEqualityComparer.Instance);
        return (merged, decisions.Where(d => d.Decision.Verdict == PolicyVerdict.Hidden || kept.Contains(d.Candidate)).ToList());
    }

    /// <summary>
    /// Un périphérique n'est servi que par la source la plus prioritaire qui le couvre. Un candidat bloqué par la
    /// politique ne passe qu'après les candidats autorisés (il reste affiché si rien d'autre ne couvre le périphérique).
    /// </summary>
    public static List<DriverCandidate> Merge(IEnumerable<DriverCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(c => c.BlockedReason is null ? 0 : 1)
            .ThenBy(c => Priority(c.Source))
            .ThenBy(c => c.MatchRank)
            .ThenByDescending(c => c.Date)
            .ToList();

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DriverCandidate>();
        foreach (var c in ordered)
        {
            if (c.TargetDeviceIds.Count == 0)
            {
                result.Add(c);
                continue;
            }
            var free = c.TargetDeviceIds.Where(id => !claimed.Contains(id)).ToList();
            if (free.Count == 0) continue;
            if (free.Count != c.TargetDeviceIds.Count)
            {
                // Ne garder que les périphériques non encore couverts.
                var keep = c.TargetDeviceIds.Select((id, i) => (id, i)).Where(x => free.Contains(x.id)).ToList();
                var names = keep.Select(x => x.i < c.TargetDeviceNames.Count ? c.TargetDeviceNames[x.i] : x.id).ToList();
                c.TargetDeviceIds.Clear();
                c.TargetDeviceIds.AddRange(keep.Select(x => x.id));
                c.TargetDeviceNames.Clear();
                c.TargetDeviceNames.AddRange(names);
            }
            foreach (var id in c.TargetDeviceIds) claimed.Add(id);
            result.Add(c);
        }

        return result
            .OrderBy(c => c.Kind)
            .ThenBy(c => Priority(c.Source))
            .ThenBy(c => c.Title)
            .ToList();
    }
}
