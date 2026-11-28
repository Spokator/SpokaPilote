using System.Collections.Concurrent;

namespace SpokaPilote.Server.Rollout;

/// <summary>Proposition soumise par un poste 4.0 (POST /api/rollout/decide) : ce qui suffit à l'identifier sur le parc.</summary>
/// <param name="Source">Nom de source du poste (WindowsUpdate, Dell...) ou libellé de source d'un rapport.</param>
/// <param name="Kind">Type de proposition (MissingDriver, Update, Optional, Firmware, ou libellé français d'un rapport).</param>
/// <param name="ItemId">Identité stable d'une proposition sans version lisible (postes 4.0.1), sinon null.</param>
/// <param name="HardwareVendor">Constructeur du matériel visé (« PCI\VEN_8086 », postes 4.0.1) : même pilote d'une autre source.</param>
/// <param name="DeviceClass">Classe du périphérique visé (« Display »).</param>
public sealed record CandidateQuery(string? Source, string? Title, string? Version, string? Kind, string? ItemId = null,
    string? HardwareVendor = null, string? DeviceClass = null);

/// <summary>Décision de diffusion pour une proposition.</summary>
public static class DecisionStatus
{
    /// <summary>Installable (version autorisée pour l'anneau, proposition non soumise aux anneaux, ou anneaux inactifs).</summary>
    public const string Allowed = "allowed";
    /// <summary>Version connue en cours de diffusion, pas encore autorisée pour l'anneau (date dans <see cref="RolloutDecision.AvailableAt"/> si connue).</summary>
    public const string Held = "held";
    /// <summary>Version suspendue sur tout le parc.</summary>
    public const string Suspended = "suspended";
    /// <summary>Version jamais vue par la console (famille inconnue, ou plus récente que la dernière version autorisée de sa famille) : retenue.</summary>
    public const string Unknown = "unknown";
}

/// <param name="Rule">Origine de la décision (diagnostic) : anneau-suspendu-N, anneau-retenue-N, anneau-famille, anneau-nouveau.</param>
public sealed record RolloutDecision(string Status, string? Reason = null, DateTimeOffset? AvailableAt = null, string? Rule = null)
{
    public static readonly RolloutDecision Allow = new(DecisionStatus.Allowed);
}

/// <summary>
/// Décisions de diffusion proposition par proposition (postes 4.0) : coût proportionnel aux propositions du poste, pas au
/// catalogue de pilotes du parc. Index construit une fois par état de la diffusion (<see cref="RolloutService.View"/>),
/// plafonds des familles calculés une fois par anneau.
/// <para>
/// Mêmes règles que la politique servie aux postes 3.x (<see cref="RolloutPolicyBuilder"/>), proposition par proposition :
/// <list type="number">
/// <item>version suspendue (même source, même titre — sans tenir compte de la casse —, même version numérique ; ou, 4.0.1,
///   même version du même pilote proposée par une autre source : même constructeur du matériel, même classe) → suspended,
///   pour tous les types de proposition ;</item>
/// <item>anneaux inactifs, proposition non soumise (pilote manquant, sauf réglage), version sans numéro lisible ET sans
///   identité stable (postes 4.0.0) → allowed ; sans numéro lisible avec identité (4.0.1) : voir <see cref="DecideVersionless"/> ;</item>
/// <item>version connue pas encore autorisée pour l'anneau → held (date de disponibilité si elle ne dépend que du délai) ;
///   version connue autorisée → allowed ;</item>
/// <item>version inconnue de la console, si l'anneau retient les nouveautés (délai ou validation) : famille de la proposition
///   (titre dont les numéros de version sont remplacés par « * », même source, même plage de version majeure) — plus récente
///   que la dernière version autorisée de la plage, ou plage sans version autorisée → unknown ; sinon allowed ; famille
///   jamais vue → unknown. Anneau sans délai ni validation (pilote) → allowed.</item>
/// </list>
/// La décision ne fait rien enregistrer : la première détection reste la réception du premier RAPPORT qui propose la version.
/// </para>
/// </summary>
public sealed class RolloutIndex
{
    private readonly Dictionary<(string Source, string Title, Version Version), List<VersionPlan>> _exact = new();
    /// <summary>Versions d'une source inconnue (libellé non reconnu) : leurs règles n'ont pas de critère de source.</summary>
    private readonly Dictionary<(string Title, Version Version), List<VersionPlan>> _anySource = new();
    /// <summary>Familles exactes (source, motif) ; recherche par source et motif sans tenir compte de la casse (comme les règles).</summary>
    private readonly Dictionary<(string Source, string Family), FamilyVersions> _families = new();
    private readonly Dictionary<(string Source, string Family), List<FamilyVersions>> _bySource = new();
    private readonly Dictionary<string, List<FamilyVersions>> _anySourceFamilies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TargetFamilies> _targets = new();

    /// <summary>Versions d'une famille (même source, même motif), triées par version, avec leur version majeure.</summary>
    internal sealed class FamilyVersions(string source, string family)
    {
        public string Source { get; } = source;
        public string Family { get; } = family;
        public List<(VersionPlan Plan, Version Parsed)> Versions { get; } = new();
        public int[] Majors { get; set; } = [];
    }

    /// <summary>Pour un destinataire : plus récente version autorisée par famille et par plage de version majeure.</summary>
    private sealed class TargetFamilies
    {
        public required Dictionary<FamilyVersions, VersionPlan?[]> Best { get; init; }
    }

    public RolloutSnapshot Snapshot { get; }
    public int Versions { get; }

    /// <summary>Propositions sans version lisible, par identité stable (« update:&lt;UpdateID&gt; »...) : toutes sources (même UpdateID pour Windows Update et le catalogue).</summary>
    private readonly Dictionary<string, List<VersionPlan>> _items = new(StringComparer.Ordinal);
    /// <summary>Propositions sans version lisible SUSPENDUES, par source et titre : la suspension vise toutes les propositions sans version de ce titre (comme la règle des postes 3.x).</summary>
    private readonly Dictionary<(string Source, string Title), VersionPlan> _suspendedTitles = new();
    /// <summary>Versions suspendues par constructeur du matériel et version : même pilote proposé par une autre source, sous un autre titre.</summary>
    private readonly Dictionary<(string Vendor, Version Version), List<VersionPlan>> _suspendedHardware = new();

    public RolloutIndex(RolloutSnapshot snapshot, IReadOnlyList<VersionPlan> plans, DateTimeOffset now)
    {
        Snapshot = snapshot;
        Versions = plans.Count;
        var since = now.AddDays(-snapshot.Settings.FamilyRetentionDays);
        foreach (var p in plans)
        {
            var v = p.Version;
            if (v.IsVersionless)
            {
                Add(_items, v.ItemKey, p);
                if (v.Suspended) _suspendedTitles.TryAdd((v.Source, Normalize(v.Title)), p);
                continue;
            }
            if (RolloutKeys.ParseVersion(v.Version) is not { } parsed) continue;
            if (v.Suspended && v.HardwareVendor is { } vendor) Add(_suspendedHardware, (vendor, RolloutKeys.CanonicalVersion(vendor, v.Version)!), p);
            var title = Normalize(v.Title);
            var known = RolloutKeys.SourceKind(v.Source) is not null;
            if (known) Add(_exact, (v.Source, title, parsed), p);
            else Add(_anySource, (title, parsed), p);

            // Familles : mêmes versions que les règles de famille de la politique (vue dans la période de rétention, ou pas
            // encore autorisée partout).
            if (v.LastSeen < since && p.Completed) continue;
            if (!_families.TryGetValue((v.Source, v.Family), out var family))
            {
                _families[(v.Source, v.Family)] = family = new FamilyVersions(v.Source, v.Family);
                var norm = Normalize(v.Family);
                List<FamilyVersions>? list;
                if (known)
                {
                    if (!_bySource.TryGetValue((v.Source, norm), out list)) _bySource[(v.Source, norm)] = list = new List<FamilyVersions>(1);
                }
                else if (!_anySourceFamilies.TryGetValue(norm, out list)) _anySourceFamilies[norm] = list = new List<FamilyVersions>(1);
                list.Add(family);
            }
            family.Versions.Add((p, parsed));
        }
        foreach (var f in _families.Values) f.Majors = f.Versions.Select(x => x.Parsed.Major).Distinct().Order().ToArray();
    }

    private static string Normalize(string text) => text.Trim().ToUpperInvariant();

    private static void Add<TKey>(Dictionary<TKey, List<VersionPlan>> map, TKey key, VersionPlan p) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<VersionPlan>(1);
        list.Add(p);
    }

    /// <summary>Plage de version majeure d'une famille qui contient <paramref name="major"/> (mêmes bornes que les règles de famille).</summary>
    internal static int RangeOf(int[] majors, int major)
    {
        var k = 0;
        for (var i = 1; i < majors.Length; i++) if (majors[i] <= major) k = i;
        return k;
    }

    private TargetFamilies For(PolicyTarget target) => _targets.GetOrAdd(target.CacheKey, _ =>
    {
        var best = new Dictionary<FamilyVersions, VersionPlan?[]>(ReferenceEqualityComparer.Instance);
        foreach (var f in _families.Values)
        {
            var ranges = new VersionPlan?[f.Majors.Length];
            var parsedBest = new Version?[f.Majors.Length];
            foreach (var (plan, parsed) in f.Versions)
            {
                if (plan.Version.Suspended || !RolloutPolicyBuilder.Eligible(plan, target)) continue;
                var k = Array.IndexOf(f.Majors, parsed.Major);
                if (parsedBest[k] is null || parsed > parsedBest[k]) { parsedBest[k] = parsed; ranges[k] = plan; }
            }
            best[f] = ranges;
        }
        return new TargetFamilies { Best = best };
    });

    /// <summary>Décision pour une proposition (voir la description de la classe).</summary>
    public RolloutDecision Decide(CandidateQuery q, PolicyTarget target, TimeZoneInfo zone, DateTimeOffset now)
    {
        var title = q.Title?.Trim();
        var version = q.Version?.Trim();
        if (string.IsNullOrEmpty(title)) return RolloutDecision.Allow;
        if (RolloutKeys.ParseVersion(version) is not { } parsed)
            return RolloutKeys.ItemKey(q.ItemId) is { } item ? DecideVersionless(q, title, item, target, zone, now) : RolloutDecision.Allow;
        var source = RolloutKeys.SourceKey(q.Source);
        var key = Normalize(title);
        var matches = new List<VersionPlan>(2);
        if (_exact.TryGetValue((source, key, parsed), out var exact)) matches.AddRange(exact);
        if (_anySource.TryGetValue((key, parsed), out var any)) matches.AddRange(any);

        // 1. Suspension : tous les anneaux, tous les types de proposition (même anneaux désactivés) — y compris la même version
        // du même pilote proposée par une autre source sous un autre titre (même constructeur du matériel, même classe).
        if (matches.Where(p => p.Version.Suspended).OrderBy(p => p.Version.Id).FirstOrDefault() is { } suspended)
            return new RolloutDecision(DecisionStatus.Suspended, RolloutPolicyBuilder.SuspensionReason(suspended.Version), Rule: RolloutPolicyBuilder.SuspendedPrefix + suspended.Version.Id);
        if (RolloutKeys.NormalizeVendor(q.HardwareVendor) is { } vendor && _suspendedHardware.TryGetValue((vendor, RolloutKeys.CanonicalVersion(vendor, version)!), out var sameDriver) &&
            sameDriver.Where(p => RolloutKeys.SameDriverClass(p.Version.DeviceClass, RolloutKeys.NormalizeClass(q.DeviceClass))).OrderBy(p => p.Version.Id).FirstOrDefault() is { } elsewhere)
            return new RolloutDecision(DecisionStatus.Suspended, RolloutPolicyBuilder.SuspensionReason(elsewhere.Version) +
                $" (même pilote, proposé par {RolloutKeys.SourceLabel(elsewhere.Version.Source)})", Rule: RolloutPolicyBuilder.SuspendedHardwarePrefix + elsewhere.Version.Id);

        var s = Snapshot;
        if (!s.RingsActive || (target.Ring is null && !target.Conservative)) return RolloutDecision.Allow;
        var kind = RolloutKeys.CandidateKind(q.Kind) ?? q.Kind?.Trim();
        if (!s.Settings.ApplyToMissingDrivers && string.Equals(kind, "MissingDriver", StringComparison.OrdinalIgnoreCase)) return RolloutDecision.Allow;

        // 2. Version connue : autorisée ou retenue pour ce destinataire.
        if (matches.Count > 0)
        {
            var held = matches.Where(p => !RolloutPolicyBuilder.Eligible(p, target)).OrderBy(p => p.Version.FirstSeen).ThenBy(p => p.Version.Id).FirstOrDefault();
            if (held is null) return RolloutDecision.Allow;
            var (reason, at) = RolloutPolicyBuilder.HoldReason(held, target, target.Conservative ? null : target.Ring!.Name, zone);
            return new RolloutDecision(DecisionStatus.Held, reason, at, RolloutPolicyBuilder.HoldPrefix + held.Version.Id);
        }

        // 3. Version inconnue de la console : famille de la proposition, si l'anneau retient les nouveautés.
        if (!RolloutPolicyBuilder.HoldsNewVersions(s, target)) return RolloutDecision.Allow;
        var familyKey = Normalize(RolloutKeys.FamilyPattern(title));
        var families = new List<FamilyVersions>(2);
        if (_bySource.TryGetValue((source, familyKey), out var own)) families.AddRange(own);
        if (_anySourceFamilies.TryGetValue(familyKey, out var anyFamilies)) families.AddRange(anyFamilies);
        var who = target.Conservative ? "ce poste" : $"votre anneau « {target.Ring!.Name} »";
        var display = RolloutKeys.DisplayName(title, version!);
        if (families.Count == 0)
        {
            var earliest = now.AddDays(target.Conservative ? s.Rings.Max(r => r.DelayDays) : target.Ring!.DelayDays);
            return new RolloutDecision(DecisionStatus.Unknown,
                $"Pilote {display} : nouveau pilote, pas encore vu par la console — retenu pour {who} (disponible au plus tôt le " +
                $"{RolloutPlanner.FormatDate(earliest, zone)}, une fois signalé à la console par un rapport)", Rule: "anneau-nouveau");
        }
        var best = For(target).Best;
        foreach (var f in families.OrderBy(f => f.Source, StringComparer.Ordinal).ThenBy(f => f.Family, StringComparer.Ordinal))
        {
            var k = RangeOf(f.Majors, parsed.Major);
            var ceiling = best[f][k];
            if (ceiling is null)
                return new RolloutDecision(DecisionStatus.Unknown,
                    $"Pilote {display} : nouvelle version de « {f.Family} », pas encore vue par la console ; aucune version de ce pilote n'est encore " +
                    $"autorisée pour {who} (diffusion par anneaux)", Rule: "anneau-famille");
            if (RolloutKeys.ParseVersion(ceiling.Version.Version) is { } max && parsed > max)
                return new RolloutDecision(DecisionStatus.Unknown,
                    $"Pilote {display} : nouvelle version, pas encore vue par la console — retenue pour {who} " +
                    $"(dernière version autorisée : {ceiling.Version.Version} ; une nouvelle version est diffusée par anneaux après sa première détection sur le parc)",
                    Rule: "anneau-famille");
        }
        return RolloutDecision.Allow;
    }

    /// <summary>
    /// Proposition sans numéro de version lisible (postes 4.0.1), identifiée par <paramref name="item"/> (UpdateID de Windows
    /// Update ou du catalogue, identifiant du paquet constructeur...) : suspendue si elle l'est (ou si une proposition sans
    /// version du même titre et de la même source l'est, comme la règle servie aux postes 3.x) ; connue → autorisée ou retenue
    /// selon son anneau (délai à compter de sa première détection) ; jamais vue → retenue si l'anneau retient les nouveautés.
    /// </summary>
    private RolloutDecision DecideVersionless(CandidateQuery q, string title, string item, PolicyTarget target, TimeZoneInfo zone, DateTimeOffset now)
    {
        var source = RolloutKeys.SourceKey(q.Source);
        var matches = _items.GetValueOrDefault(item) ?? [];
        if (matches.Where(p => p.Version.Suspended).OrderBy(p => p.Version.Id).FirstOrDefault() is { } suspended ||
            _suspendedTitles.TryGetValue((source, Normalize(title)), out suspended))
            return new RolloutDecision(DecisionStatus.Suspended, RolloutPolicyBuilder.SuspensionReason(suspended.Version), Rule: RolloutPolicyBuilder.SuspendedPrefix + suspended.Version.Id);

        var s = Snapshot;
        if (!s.RingsActive || (target.Ring is null && !target.Conservative)) return RolloutDecision.Allow;
        var kind = RolloutKeys.CandidateKind(q.Kind) ?? q.Kind?.Trim();
        if (!s.Settings.ApplyToMissingDrivers && string.Equals(kind, "MissingDriver", StringComparison.OrdinalIgnoreCase)) return RolloutDecision.Allow;
        if (matches.Count > 0)
        {
            var held = matches.Where(p => !RolloutPolicyBuilder.Eligible(p, target)).OrderBy(p => p.Version.FirstSeen).ThenBy(p => p.Version.Id).FirstOrDefault();
            if (held is null) return RolloutDecision.Allow;
            var (reason, at) = RolloutPolicyBuilder.HoldReason(held, target, target.Conservative ? null : target.Ring!.Name, zone);
            return new RolloutDecision(DecisionStatus.Held, reason, at, RolloutPolicyBuilder.HoldPrefix + held.Version.Id);
        }
        if (!RolloutPolicyBuilder.HoldsNewVersions(s, target)) return RolloutDecision.Allow;
        var who = target.Conservative ? "ce poste" : $"votre anneau « {target.Ring!.Name} »";
        var earliest = now.AddDays(target.Conservative ? s.Rings.Max(r => r.DelayDays) : target.Ring!.DelayDays);
        return new RolloutDecision(DecisionStatus.Unknown,
            $"Pilote {title} (sans numéro de version) : nouveau pilote, pas encore vu par la console — retenu pour {who} (disponible au plus tôt le " +
            $"{RolloutPlanner.FormatDate(earliest, zone)}, une fois signalé à la console par un rapport)", Rule: "anneau-nouveau");
    }
}
