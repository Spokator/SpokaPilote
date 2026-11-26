using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Rollout;

/// <summary>Politique effective prête à servir (texte et forme compressée, calculés une fois par anneau).</summary>
public sealed class ServedPolicy
{
    public required string? Json { get; init; }
    public required byte[]? Utf8 { get; init; }
    public required byte[]? Gzip { get; init; }
    public required PolicyTarget Target { get; init; }
    public required RolloutRules Rules { get; init; }
    /// <summary>Rien n'est ajouté à la politique de l'organisation : le document enregistré est servi tel quel (comportement 3.x).</summary>
    public bool Passthrough { get; init; }
}

/// <summary>
/// Diffusion par anneaux : état courant (calculé depuis la base au plus une fois toutes les quelques secondes, et tout de
/// suite après une suspension, une reprise ou une modification des anneaux), politiques effectives en cache par anneau
/// (chaque poste d'un même anneau reçoit exactement le même document : une requête de poste ne coûte qu'une recherche
/// de son anneau), disjoncteur évalué à la réception des rapports qui signalent un échec.
/// </summary>
public sealed class RolloutService(ServerStore store, AlertService alerts, AuditService audit, TimeProvider clock, ILogger<RolloutService> log)
{
    public const string BreakerActor = "Disjoncteur automatique";
    public const string RolloutHeader = "X-SpokaPilote-Rollout";
    /// <summary>Poste 4.0 qui demande ses retenues proposition par proposition (POST /api/rollout/decide) plutôt que dans sa politique.</summary>
    public const string DecisionsHeader = "X-SpokaPilote-Rollout-Decisions";

    /// <summary>Âge maximal de l'état (délais qui s'écoulent, versions vues entre-temps).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    /// <summary>Après un rapport qui change quelque chose (nouvelle version, installation), l'état est recalculé au plus tôt après ce délai.</summary>
    public TimeSpan MinRebuildInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Fuseau des dates affichées dans les motifs (celui des planifications : fuseau du serveur).</summary>
    public TimeZoneInfo Zone { get; set; } = clock.LocalTimeZone;

    /// <summary>État calculé : instantané de la base, plans de chaque version, politiques servies (remplies à la demande).</summary>
    public sealed class View
    {
        public required RolloutSnapshot Snapshot { get; init; }
        public required List<VersionPlan> Plans { get; init; }
        public required Dictionary<long, VersionPlan> ById { get; init; }
        public required string? OrganizationJson { get; init; }
        public required DateTimeOffset BuiltAt { get; init; }
        public DateTimeOffset? NextChange { get; init; }
        public long Changes { get; init; }
        public int Suspended { get; init; }
        public int Attention { get; init; }
        public int InRollout { get; init; }
        public ConcurrentDictionary<(long Target, bool Aware, bool Known, bool Decisions), ServedPolicy> Policies { get; } = new();
        /// <summary>Index des décisions par proposition (construit à la première demande d'un poste 4.0).</summary>
        public required Lazy<RolloutIndex> Index { get; init; }
        /// <summary>Listes de versions filtrées et triées de la page « Déploiement » (une par filtre).</summary>
        public ConcurrentDictionary<string, List<VersionPlan>> Lists { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private volatile View? _view;
    private long _changes;
    private volatile bool _urgent;
    private long _builds;

    public long Builds => Interlocked.Read(ref _builds);

    /// <summary>Signale un changement : urgent = recalcul immédiat (suspension, reprise, anneaux, politique de l'organisation).</summary>
    public void Changed(bool urgent = false)
    {
        Interlocked.Increment(ref _changes);
        if (urgent) _urgent = true;
    }

    public View Current()
    {
        var now = clock.GetUtcNow();
        var v = _view;
        if (v is not null && !NeedsRebuild(v, now)) return v;
        lock (_gate)
        {
            v = _view;
            if (v is not null && !NeedsRebuild(v, now)) return v;
            v = Build(now);
            _view = v;
            return v;
        }
    }

    private bool NeedsRebuild(View v, DateTimeOffset now) =>
        _urgent || now - v.BuiltAt >= MaxAge || now < v.BuiltAt || (v.NextChange is { } next && now >= next) ||
        (Interlocked.Read(ref _changes) != v.Changes && now - v.BuiltAt >= MinRebuildInterval);

    private View Build(DateTimeOffset now)
    {
        _urgent = false;
        var changes = Interlocked.Read(ref _changes);
        var snapshot = store.LoadRolloutSnapshot(now);
        var org = store.GetDocument(ServerStore.PolicyKey)?.Json;
        var plans = RolloutPlanner.Plan(snapshot, now);
        Interlocked.Increment(ref _builds);
        return new View
        {
            Snapshot = snapshot,
            Plans = plans,
            ById = plans.ToDictionary(p => p.Version.Id),
            OrganizationJson = org,
            BuiltAt = now,
            NextChange = snapshot.RingsActive ? RolloutPlanner.NextChange(plans, now) : null,
            Changes = changes,
            Suspended = plans.Count(p => p.Version.Suspended),
            Attention = plans.Count(p => p.Version.NeedsAttention),
            InRollout = snapshot.RingsActive ? plans.Count(p => p.Status == "rollout") : 0,
            Index = new Lazy<RolloutIndex>(() => new RolloutIndex(snapshot, plans, now), LazyThreadSafetyMode.ExecutionAndPublication),
        };
    }

    // ------------------------------------------------------------------ politique effective d'un poste

    /// <summary>
    /// Politique servie à un poste (jeton d'enrôlement). Sans anneau actif ni suspension : la politique de l'organisation
    /// telle quelle (comportement 3.x). Sinon : poste identifié (en-têtes X-SpokaPilote-Machine-Id / -Machine, envoyés par
    /// les postes 4.0) → politique de son anneau ; poste non identifié (3.x) → anneau le plus prudent.
    /// </summary>
    /// <param name="decisions">Poste 4.0 qui demande ses décisions proposition par proposition (en-tête
    /// X-SpokaPilote-Rollout-Decisions) : le document ne contient que les suspensions, la section « rollout » l'indique.</param>
    public ServedPolicy Serve(string? machineId, string? machineName, bool rolloutAware, bool decisions = false)
    {
        var view = Current();
        var (target, known) = Resolve(view, machineId, machineName);
        var aware = rolloutAware && view.Snapshot.RingsActive;
        var decide = aware && decisions;
        return view.Policies.GetOrAdd((target.CacheKey, aware, aware && known, decide), _ => Compose(view, target, aware, known, decide));
    }

    /// <summary>Destinataire d'une demande de poste : non identifié → anneau le plus prudent ; identifié → son anneau.</summary>
    private (PolicyTarget Target, bool Known) Resolve(View view, string? machineId, string? machineName)
    {
        var s = view.Snapshot;
        if (!s.RingsActive) return (PolicyTarget.SuspensionsOnly, false);
        if (string.IsNullOrWhiteSpace(machineId) && string.IsNullOrWhiteSpace(machineName)) return (new PolicyTarget(null, true), false);
        var (isKnown, ringId) = store.ResolveMachineRing(machineId, machineName);
        return (new PolicyTarget(s.Rings.FirstOrDefault(r => r.Id == ringId) ?? s.DefaultRing, false), isKnown);
    }

    /// <summary>Plafond des règles de diffusion des politiques servies (postes 3.x, postes 4.0 sans décisions).</summary>
    public RolloutLimits Limits { get; set; } = RolloutLimits.Default;

    /// <summary>Propositions au plus par demande de décisions.</summary>
    public const int MaxDecisionCandidates = 1000;

    /// <summary>Décisions de diffusion d'un poste 4.0, dans l'ordre des propositions.</summary>
    public sealed record DecisionBatch(PolicyTarget Target, bool Known, JsonObject Section, IReadOnlyList<RolloutDecision> Decisions);

    /// <summary>
    /// Décisions pour les propositions d'un poste (POST /api/rollout/decide) : anneau résolu comme pour sa politique, coût
    /// proportionnel au nombre de propositions (index en mémoire, aucune écriture en base).
    /// </summary>
    public DecisionBatch Decide(string? machineId, string? machineName, IReadOnlyList<CandidateQuery> candidates)
    {
        var view = Current();
        var (target, known) = Resolve(view, machineId, machineName);
        var index = view.Index.Value;
        var decisions = candidates.Select(c => index.Decide(c, target, Zone, view.BuiltAt)).ToList();
        var section = RolloutPolicyBuilder.RolloutSection(view.Snapshot, target, new RolloutRules(), known, view.BuiltAt, decisions: true);
        section.Remove("availability");
        return new DecisionBatch(target, known, section, decisions);
    }

    private ServedPolicy Compose(View view, PolicyTarget target, bool aware, bool known, bool decide)
    {
        var now = view.BuiltAt;
        var reserved = (view.OrganizationJson?.Length ?? 0) + (aware ? 600 : 0);
        var rules = view.Snapshot.RingsActive || view.Suspended > 0
            ? RolloutPolicyBuilder.Build(view.Snapshot, view.Plans, target, now, Zone, Limits, suspensionsOnly: decide, reservedBytes: reserved)
            : new RolloutRules();
        var section = aware ? RolloutPolicyBuilder.RolloutSection(view.Snapshot, target, rules, known, now, decisions: decide) : null;
        var json = RolloutPolicyBuilder.Compose(view.OrganizationJson, rules, section);
        var utf8 = json is null ? null : Encoding.UTF8.GetBytes(json);
        return new ServedPolicy
        {
            Json = json, Utf8 = utf8, Gzip = utf8 is { Length: > 4096 } ? Gzip(utf8) : null, Target = target, Rules = rules,
            Passthrough = rules.Rules.Count == 0 && section is null,
        };
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 4 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>Politique servie aux postes 3.x (non identifiés : anneau le plus prudent), pour l'aperçu de la console.</summary>
    public ServedPolicy ServeLegacy() => Serve(null, null, rolloutAware: false);

    // ------------------------------------------------------------------ disjoncteur

    /// <summary>Après l'enregistrement d'un rapport : état à recalculer, puis disjoncteur pour les versions en échec.</summary>
    public void OnReportSaved(MachineReport report, SaveResult saved)
    {
        if (saved.Rollout is not { } track || (!track.Changed && track.FailedVersions.Count == 0)) return;
        Changed();
        if (track.FailedVersions.Count == 0) return;
        var settings = store.GetRolloutSettings();
        if (!settings.Breaker.Enabled) return;
        var now = clock.GetUtcNow();
        foreach (var id in track.FailedVersions) Evaluate(id, settings, now);
    }

    /// <summary>Évalue le disjoncteur pour une version : renvoie le motif si elle vient d'être suspendue.</summary>
    public string? Evaluate(long versionId, RolloutSettings settings, DateTimeOffset now)
    {
        var v = store.GetRolloutVersion(versionId);
        if (v is null || v.Suspended) return null;
        var b = settings.Breaker;
        var cutoff = now.AddHours(-b.WindowHours);
        // Après une reprise, seuls les échecs postérieurs comptent (sinon la version serait aussitôt suspendue à nouveau).
        if (v.ResumedAt is { } resumed && resumed >= cutoff) cutoff = resumed.AddTicks(1);
        var rings = store.GetRings();
        var trip = store.GetBreakerWindows(versionId, cutoff)
            .Where(w => w.Failures >= b.MinFailures && w.Failures * 100L >= (long)b.MinFailureRatePercent * w.Attempts)
            .OrderBy(w => rings.FirstOrDefault(r => r.Id == w.RingId)?.Position ?? int.MaxValue)
            .FirstOrDefault();
        if (trip is null) return null;
        var ringName = RingName(rings, trip.RingId);
        var reason = $"suspendu automatiquement : {RolloutPlanner.Plural(trip.Failures, "échec", "échecs")} sur " +
                     $"{RolloutPlanner.Plural(trip.Attempts, "installation", "installations")} {ringName} en {b.WindowHours} h";
        if (!store.SuspendVersion(versionId, automatic: true, reason, BreakerActor, now)) return null;
        Changed(urgent: true);
        log.LogWarning("Pilote {Driver} ({Source}) {Reason}.", v.DisplayName, v.Source, reason);
        audit.Record(BreakerActor, null, AuditActions.RolloutAutoSuspend, v.DisplayName, reason, null);
        try
        {
            alerts.OnRolloutSuspended(versionId, v.DisplayName, RolloutKeys.SourceLabel(v.Source), ringName.Replace("dans l'anneau ", ""),
                trip.Failures, trip.Attempts, b.WindowHours, $"{v.DisplayName} : {reason}", now);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Alerte de suspension impossible pour {Driver}.", v.DisplayName);
        }
        return reason;
    }

    /// <summary>« dans l'anneau « Pilote » » (ou « hors anneau » pour les installations faites sans anneau configuré).</summary>
    public static string RingName(IReadOnlyList<RingRecord> rings, long ringId) =>
        ringId == 0 ? "hors anneau (aucun anneau configuré)"
        : rings.FirstOrDefault(r => r.Id == ringId) is { } ring ? $"dans l'anneau « {ring.Name} »" : "dans un anneau supprimé";
}
