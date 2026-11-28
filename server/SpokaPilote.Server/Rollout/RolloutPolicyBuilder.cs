using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpokaPilote.Server.Rollout;

/// <summary>Destinataire d'une politique effective.</summary>
/// <param name="Ring">Anneau du poste (null : anneaux inactifs, ou poste non identifié si <paramref name="Conservative"/>).</param>
/// <param name="Conservative">Poste non identifié (client 3.x) : anneau le plus prudent — une version n'est autorisée que
/// lorsqu'elle l'est pour tous les anneaux.</param>
public sealed record PolicyTarget(RingRecord? Ring, bool Conservative)
{
    public static readonly PolicyTarget SuspensionsOnly = new(null, false);

    /// <summary>Clé de cache : identifiant de l'anneau, -1 = anneau le plus prudent, 0 = suspensions seules.</summary>
    public long CacheKey => Conservative ? -1 : Ring?.Id ?? 0;
}

/// <summary>
/// Plafond des règles de diffusion d'une politique servie (postes 3.x, et postes 4.0 qui ne demandent pas de décisions) :
/// nombre de règles et taille du document. Les postes refusent une politique de plus de 2 Mo (3.x comme 4.0) : au-delà,
/// ils garderaient leur politique précédente — le plafond par défaut (2 000 règles, 1 Mo) laisse de la marge.
/// </summary>
public sealed record RolloutLimits(int MaxRules, int MaxBytes)
{
    public static readonly RolloutLimits Default = new(2000, 1024 * 1024);
    /// <summary>Taille maximale acceptée par les postes (2 Mo de caractères, PolicyLoader 3.0 à 4.0).</summary>
    public const int ClientMaxBytes = 2 * 1024 * 1024;
}

/// <summary>Règles ajoutées par la diffusion par anneaux pour un destinataire.</summary>
public sealed class RolloutRules
{
    public List<JsonObject> Rules { get; } = new();
    /// <summary>Identifiant de règle de retenue → date à laquelle la version devient disponible (instant ISO 8601).</summary>
    public Dictionary<string, string> Availability { get; } = new(StringComparer.Ordinal);
    public bool HoldUnknown { get; set; }
    public int Suspensions { get; set; }
    /// <summary>Règles de retenue datées (une version chacune).</summary>
    public int Holds { get; set; }
    /// <summary>Règles de famille (une par famille et par plage de version majeure).</summary>
    public int Families { get; set; }
    /// <summary>Versions en cours de diffusion pas encore autorisées pour le destinataire (qu'elles aient une règle datée ou non).</summary>
    public int HeldVersions { get; set; }
    /// <summary>
    /// Forme du document : « complete » (chaque version retenue a sa règle datée), « compact » (versions retenues couvertes
    /// par les règles de famille, sans date ; familles dont la diffusion est terminée en partie omises), « source » (en plus,
    /// toutes les mises à jour de certaines sources retenues en bloc : voir <see cref="SourceHolds"/>).
    /// </summary>
    public string Form { get; set; } = "complete";
    /// <summary>Sources dont toutes les mises à jour sont retenues (« * » = toutes les sources).</summary>
    public List<string> SourceHolds { get; } = new();
    /// <summary>Règles de famille de diffusions terminées non servies faute de place (protection contre une version plus récente jamais vue).</summary>
    public int OmittedFamilies { get; set; }
    /// <summary>Versions retenues sans règle datée (couvertes par une règle de famille ou de source).</summary>
    public int UndatedHolds { get; set; }
    /// <summary>Taille des règles de diffusion (JSON compact, octets).</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// Génère la politique effective d'un poste : politique de l'organisation + règles de diffusion, exprimées UNIQUEMENT avec
/// des règles du format 1 (block / pin, critères source, title, kind, versions, versionMin/versionMax) que tous les postes
/// 3.x comprennent déjà. Les postes 4.0 reçoivent en plus une section « rollout » (anneau, dates de disponibilité), sur
/// demande explicite (en-tête X-SpokaPilote-Rollout) : un poste 3.x, qui refuse toute propriété inconnue, ne la reçoit jamais.
/// Les postes 4.0 qui demandent des décisions (en-tête X-SpokaPilote-Rollout-Decisions) ne reçoivent que les suspensions :
/// les retenues leur sont données proposition par proposition (<see cref="RolloutIndex"/>).
/// <para>
/// Ordre (les règles de diffusion passent avant celles de l'organisation : une règle « allow » ne lève pas une retenue) :
/// 1. versions suspendues (tous types, tous anneaux) ; 2. sources retenues en bloc (forme « source » seulement) ;
/// 3. versions en cours de diffusion pas encore autorisées pour l'anneau (date de disponibilité dans le motif) ; 4. règles
/// de famille (« pin » à la version la plus récente autorisée pour l'anneau, ou « block » si aucune ne l'est encore) : une
/// version plus récente, même jamais vue par la console, est retenue — c'est ce qui empêche un poste de l'installer avant
/// que la console ne l'ait vue (afflux à heure fixe).
/// </para>
/// <para>
/// Document BORNÉ (<see cref="RolloutLimits"/>) quelle que soit la taille du catalogue de pilotes du parc, par ordre de
/// priorité : (a) suspensions ; (b) règles indispensables — règles de famille des familles en cours de diffusion, et règle
/// datée des seules versions retenues qu'aucune règle de famille ne couvre (version inférieure au plafond de sa famille) :
/// ensemble, elles retiennent TOUTE version connue pas encore autorisée ; si elles ne tiennent pas, les sources les plus
/// chargées sont retenues en bloc (toutes leurs mises à jour, jusqu'à ce que leurs règles tiennent à nouveau) ; (c) règles
/// de famille des diffusions terminées (les plus récemment vues d'abord) : protection contre une version plus récente
/// jamais vue ; (d) règles datées des autres versions retenues (motif avec la date de disponibilité). Invariant : un poste
/// 3.x n'installe jamais une version connue de la console qui n'est pas autorisée pour l'anneau le plus prudent.
/// </para>
/// </summary>
public static class RolloutPolicyBuilder
{
    public const string RulePrefix = "anneau-";
    public const string SuspendedPrefix = RulePrefix + "suspendu-";
    /// <summary>Même version suspendue, même pilote quelle que soit la source (constructeur du matériel + classe + version).</summary>
    public const string SuspendedHardwarePrefix = SuspendedPrefix + "materiel-";
    public const string HoldPrefix = RulePrefix + "retenue-";
    public const string FamilyPrefix = RulePrefix + "famille-";
    public const string SourcePrefix = RulePrefix + "source-";
    /// <summary>Plus grande composante de version (versionMax ouverte d'une plage de versions majeures).</summary>
    private const string MaxComponent = "2147483647";
    /// <summary>Taille réservée par règle datée pour sa date dans la section « rollout » (postes 4.0).</summary>
    private const int AvailabilityBytes = 64;
    /// <summary>Identifiant d'une règle de famille, attribué à l'émission (« anneau-famille-12345 »).</summary>
    private const int IdBytes = 24;

    private static readonly string[] UpdateKinds = ["Update", "Optional", "Firmware"];

    /// <summary>Le destinataire doit-il retenir une version nouvelle (jamais vue) ? Oui si son anneau a un délai ou exige une validation.</summary>
    public static bool HoldsNewVersions(RolloutSnapshot s, PolicyTarget target)
    {
        if (!s.RingsActive) return false;
        if (target.Conservative) return s.Rings.Select((r, i) => NeedsHold(r, i)).Any(x => x);
        if (target.Ring is not { } ring) return false;
        var index = s.Rings.ToList().FindIndex(r => r.Id == ring.Id);
        return index >= 0 && NeedsHold(ring, index);
    }

    private static bool NeedsHold(RingRecord r, int index) => r.DelayDays > 0 || (r.RequireValidation && index > 0);

    /// <summary>Motif d'une suspension (règle et décision).</summary>
    public static string SuspensionReason(RolloutVersion v) =>
        $"Pilote {v.DisplayName} : {v.SuspendReason ?? "suspendu sur tout le parc par l'équipe informatique"}";

    /// <summary>Règle candidate : objet JSON (identifiant attribué à l'émission), taille estimée.</summary>
    private sealed class Item(JsonObject rule, int extraBytes = 0)
    {
        public JsonObject Rule { get; } = rule;
        public int Bytes { get; } = rule.ToJsonString().Length + 1 + extraBytes;
        public string? Availability { get; init; }
        public string? HoldId { get; init; }
    }

    /// <summary>Famille de pilotes (source, motif) : règles par plage de version majeure et versions retenues.</summary>
    private sealed class FamilyPlan
    {
        public required string Source { get; init; }
        public required string Family { get; init; }
        /// <summary>Nom de source du poste (critère « source »), null si la source n'est pas reconnue : règle valable pour toutes.</summary>
        public string? Kind { get; init; }
        public required List<Item> RangeRules { get; init; }
        /// <summary>Versions retenues non couvertes par une règle de famille (inférieures ou égales au plafond de leur plage).</summary>
        public required List<VersionPlan> Uncovered { get; init; }
        /// <summary>Versions retenues couvertes par une règle de famille (règle datée facultative).</summary>
        public required List<VersionPlan> Covered { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public bool InRollout => Uncovered.Count > 0 || Covered.Count > 0;
        public string Bucket => Kind ?? "*";
    }

    /// <summary>Règles de diffusion d'un destinataire, bornées par <paramref name="limits"/> (défaut : <see cref="RolloutLimits.Default"/>).</summary>
    /// <param name="suspensionsOnly">Postes 4.0 en mode décisions : seules les suspensions sont servies dans le document.</param>
    /// <param name="reservedBytes">Taille déjà occupée dans le document (politique de l'organisation, section « rollout »).</param>
    public static RolloutRules Build(RolloutSnapshot s, IReadOnlyList<VersionPlan> plans, PolicyTarget target, DateTimeOffset now, TimeZoneInfo zone,
        RolloutLimits? limits = null, bool suspensionsOnly = false, int reservedBytes = 0)
    {
        limits ??= RolloutLimits.Default;
        var result = new RolloutRules();
        var kinds = s.Settings.ApplyToMissingDrivers ? null : UpdateKinds;
        var budgetRules = limits.MaxRules;
        long budgetBytes = limits.MaxBytes - reservedBytes - 256;

        // 1. Suspensions : toujours servies (même anneaux désactivés), sans restriction de type. Version ordinaire : source, titre,
        // version — et, si le matériel visé est connu, la même version pour ce constructeur et cette classe de périphérique, QUELLE
        // QUE SOIT LA SOURCE (même pilote proposé par Dell, le catalogue Microsoft, Windows Update... sous un autre titre).
        // Proposition sans numéro de version : source et titre (toutes les propositions sans version de ce titre).
        var suspended = plans.Where(p => p.Version.Suspended).OrderBy(p => p.Version.Id).Select(p => p.Version).ToList();
        var suspensionItems = new List<Item>();
        foreach (var v in suspended)
        {
            if (v.IsVersionless)
            {
                suspensionItems.Add(new Item(Rule(SuspendedPrefix + v.Id, "block", v.Source, v.Title, null, reason: SuspensionReason(v))));
                continue;
            }
            suspensionItems.Add(new Item(Rule(SuspendedPrefix + v.Id, "block", v.Source, v.Title, null, versions: [v.Version], reason: SuspensionReason(v))));
            if (v.HardwareVendor is { } vendor)
                suspensionItems.Add(new Item(HardwareRule(SuspendedHardwarePrefix + v.Id, vendor, v.DeviceClass, RolloutKeys.RuleVersions(vendor, v.Version).ToArray(),
                    $"{SuspensionReason(v)} (même pilote, quelle que soit la source)")));
        }
        if (suspensionItems.Count > budgetRules / 4 || suspensionItems.Sum(i => (long)i.Bytes) > budgetBytes / 4)
        {
            // Très nombreuses suspensions : une règle par source, qui bloque ces numéros de version quel que soit le titre (plus large,
            // sûr) ; une règle par constructeur du matériel et classe (même pilote, toutes sources) ; propositions sans version :
            // une règle par source avec la liste de leurs titres.
            var versioned = suspended.Where(v => !v.IsVersionless).ToList();
            suspensionItems = versioned.GroupBy(v => RolloutKeys.SourceKind(v.Source)).OrderBy(g => g.Key ?? "~", StringComparer.Ordinal)
                .Select(g => new Item(Rule(SuspendedPrefix + (g.Key ?? "toutes"), "block", g.Key ?? "", "*", null,
                    versions: g.Select(v => RolloutKeys.Text(RolloutKeys.ParseVersion(v.Version)!)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                    reason: $"{RolloutPlanner.Plural(g.Count(), "version suspendue", "versions suspendues")} sur tout le parc " +
                            $"({(g.Key is null ? "toutes sources" : RolloutKeys.SourceLabel(g.Key))}) : liste détaillée dans la console, page Déploiement")))
                .ToList();
            var group = 0;
            suspensionItems.AddRange(versioned.Where(v => v.HardwareVendor is not null)
                .GroupBy(v => (Vendor: v.HardwareVendor!, Class: v.DeviceClass?.ToUpperInvariant()))
                .OrderBy(g => g.Key.Vendor, StringComparer.Ordinal).ThenBy(g => g.Key.Class ?? "", StringComparer.Ordinal)
                .Select(g => new Item(HardwareRule(SuspendedHardwarePrefix + "groupe-" + ++group, g.Key.Vendor, g.First().DeviceClass,
                    g.SelectMany(v => RolloutKeys.RuleVersions(v.HardwareVendor, v.Version)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                    $"{RolloutPlanner.Plural(g.Count(), "version suspendue", "versions suspendues")} sur tout le parc (même pilote, quelle que soit la source) : " +
                    "liste détaillée dans la console, page Déploiement"))));
            suspensionItems.AddRange(suspended.Where(v => v.IsVersionless).GroupBy(v => RolloutKeys.SourceKind(v.Source)).OrderBy(g => g.Key ?? "~", StringComparer.Ordinal)
                .Select(g => new Item(Rule(SuspendedPrefix + "sans-version-" + (g.Key ?? "toutes"), "block", g.Key ?? "",
                    titles: g.Select(v => v.Title).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
                    reason: $"{RolloutPlanner.Plural(g.Count(), "pilote sans numéro de version suspendu", "pilotes sans numéro de version suspendus")} sur tout le parc " +
                            $"({(g.Key is null ? "toutes sources" : RolloutKeys.SourceLabel(g.Key))}) : liste détaillée dans la console, page Déploiement"))));
        }
        foreach (var i in suspensionItems)
        {
            result.Rules.Add(i.Rule);
            result.Bytes += i.Bytes;
        }
        result.Suspensions = suspended.Count;
        budgetRules -= suspensionItems.Count;
        budgetBytes -= suspensionItems.Sum(i => (long)i.Bytes);
        if (suspensionsOnly || !s.RingsActive || (target.Ring is null && !target.Conservative)) return result;

        var ringLabel = target.Conservative ? null : target.Ring!.Name;
        var held = plans.Where(p => !p.Version.Suspended && !Eligible(p, target)).OrderBy(p => p.Version.FirstSeen).ThenBy(p => p.Version.Id).ToList();
        result.HeldVersions = held.Count;
        result.HoldUnknown = HoldsNewVersions(s, target);
        Item Dated(VersionPlan p)
        {
            var v = p.Version;
            var (reason, availableAt) = HoldReason(p, target, ringLabel, zone);
            var at = availableAt?.ToUniversalTime().ToString("O");
            // Sans numéro de version : retenue du titre (toutes les propositions sans version de ce titre et de cette source).
            return new Item(Rule(HoldPrefix + v.Id, "block", v.Source, v.Title, kinds, versions: v.IsVersionless ? null : [v.Version], reason: reason),
                at is null ? 0 : AvailabilityBytes)
            {
                Availability = at, HoldId = HoldPrefix + v.Id,
            };
        }

        // 2. Familles (seulement si l'anneau retient les nouveautés), avec leurs versions retenues.
        var families = result.HoldUnknown ? Families(s, plans, target, now, kinds, ringLabel) : new List<FamilyPlan>();
        // Anneau sans nouveautés retenues : règles datées seules. Propositions sans numéro de version : aucune règle de famille ne
        // peut les couvrir (critères de version), leur règle datée est toujours indispensable.
        var uncoveredWithoutFamily = result.HoldUnknown ? held.Where(p => p.Version.IsVersionless).ToList() : held;

        var required = new List<(string Bucket, Item Item)>();
        foreach (var p in uncoveredWithoutFamily) required.Add((RolloutKeys.SourceKind(p.Version.Source) ?? "*", Dated(p)));
        foreach (var f in families.Where(f => f.InRollout))
        {
            foreach (var p in f.Uncovered) required.Add((f.Bucket, Dated(p)));
            foreach (var r in f.RangeRules) required.Add((f.Bucket, r));
        }

        // Sources retenues en bloc si les règles indispensables ne tiennent pas (les plus chargées d'abord, « toutes » en dernier).
        var cost = required.GroupBy(x => x.Bucket).ToDictionary(g => g.Key, g => (Rules: g.Count(), Bytes: g.Sum(x => (long)x.Item.Bytes)));
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var rulesNeeded = required.Count;
        var bytesNeeded = required.Sum(x => (long)x.Item.Bytes);
        var sourceItems = new List<Item>();
        Item SourceHold(string bucket) => new(Rule(SourcePrefix + (bucket == "*" ? "toutes" : bucket), "block", bucket == "*" ? "" : bucket, "*", kinds,
            reason: $"Diffusion par anneaux ({(ringLabel is null ? "anneau le plus prudent" : $"anneau « {ringLabel} »")}) : trop de pilotes " +
                    $"{(bucket == "*" ? "" : RolloutKeys.SourceLabel(bucket) + " ")}en cours de diffusion pour les détailler dans la politique de ce poste ; " +
                    "toutes ces mises à jour sont retenues jusqu'à ce que leur diffusion avance. SpokaPilote 4.0 reçoit une décision pilote par pilote."));
        foreach (var bucket in cost.OrderBy(kv => kv.Key == "*" ? 1 : 0).ThenByDescending(kv => kv.Value.Bytes).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key))
        {
            if (rulesNeeded + sourceItems.Count <= budgetRules && bytesNeeded + sourceItems.Sum(i => (long)i.Bytes) <= budgetBytes) break;
            if (bucket == "*")
            {
                // Retenue de toutes les sources : les retenues par source deviennent inutiles.
                sourceItems.Clear();
                blocked.UnionWith(cost.Keys);
                rulesNeeded = 0;
                bytesNeeded = 0;
            }
            else
            {
                rulesNeeded -= cost[bucket].Rules;
                bytesNeeded -= cost[bucket].Bytes;
            }
            blocked.Add(bucket);
            sourceItems.Add(SourceHold(bucket));
        }
        var usedRules = rulesNeeded + sourceItems.Count;
        var usedBytes = bytesNeeded + sourceItems.Sum(i => (long)i.Bytes);
        bool Fits(int rules, long bytes) => usedRules + rules <= budgetRules && usedBytes + bytes <= budgetBytes;

        // Familles dont la diffusion est terminée (protection contre une version plus récente jamais vue), puis règles datées facultatives.
        var chosenFamilies = new HashSet<FamilyPlan>(families.Where(f => f.InRollout && !blocked.Contains(f.Bucket)), ReferenceEqualityComparer.Instance);
        foreach (var f in families.Where(f => !f.InRollout && !blocked.Contains(f.Bucket)).OrderByDescending(f => f.LastSeen))
        {
            var bytes = f.RangeRules.Sum(r => (long)r.Bytes);
            if (Fits(f.RangeRules.Count, bytes))
            {
                chosenFamilies.Add(f);
                usedRules += f.RangeRules.Count;
                usedBytes += bytes;
            }
            else result.OmittedFamilies += f.RangeRules.Count;
        }
        result.OmittedFamilies += families.Where(f => blocked.Contains(f.Bucket)).Sum(f => f.RangeRules.Count);
        var dated = required.Where(x => !blocked.Contains(x.Bucket) && x.Item.HoldId is not null).Select(x => x.Item).ToList();
        var optional = families.Where(f => !blocked.Contains(f.Bucket)).SelectMany(f => f.Covered).OrderByDescending(p => p.Version.FirstSeen).ThenByDescending(p => p.Version.Id);
        var undated = 0;
        foreach (var p in optional)
        {
            if (!Fits(1, 400)) { undated++; continue; }
            var item = Dated(p);
            if (!Fits(1, item.Bytes)) { undated++; continue; }
            dated.Add(item);
            usedRules++;
            usedBytes += item.Bytes;
        }

        // Émission : sources retenues en bloc, retenues datées (ordre de première détection), familles (source, motif, plage).
        foreach (var i in sourceItems) Emit(result, i);
        result.SourceHolds.AddRange(blocked.Order(StringComparer.Ordinal));
        var order = held.Select((p, n) => (p.Version.Id, n)).ToDictionary(x => HoldPrefix + x.Id, x => x.n);
        foreach (var i in dated.OrderBy(i => order.GetValueOrDefault(i.HoldId!, int.MaxValue)))
        {
            Emit(result, i);
            if (i.Availability is not null) result.Availability[i.HoldId!] = i.Availability;
            result.Holds++;
        }
        var n = 0;
        foreach (var f in families.Where(chosenFamilies.Contains))
            foreach (var r in f.RangeRules)
            {
                r.Rule["id"] = FamilyPrefix + ++n;
                Emit(result, r);
                result.Families++;
            }
        result.UndatedHolds = held.Count - result.Holds;
        result.Form = blocked.Count > 0 ? "source" : result.UndatedHolds > 0 || result.OmittedFamilies > 0 ? "compact" : "complete";
        return result;
    }

    private static void Emit(RolloutRules result, Item item)
    {
        result.Rules.Add(item.Rule);
        result.Bytes += item.Bytes;
    }

    /// <summary>Familles (source, motif) : une règle par plage de version majeure, versions retenues couvertes ou non par ces règles.</summary>
    private static List<FamilyPlan> Families(RolloutSnapshot s, IReadOnlyList<VersionPlan> plans, PolicyTarget target, DateTimeOffset now, string[]? kinds,
        string? ringLabel)
    {
        var since = now.AddDays(-s.Settings.FamilyRetentionDays);
        var delay = target.Conservative ? s.Rings.Max(r => r.DelayDays) : target.Ring!.DelayDays;
        var who = ringLabel is null ? "anneau le plus prudent" : $"anneau « {ringLabel} »";
        var list = new List<FamilyPlan>();
        var groups = plans
            .Where(p => p.Version.LastSeen >= since || !p.Completed)
            .Select(p => (Plan: p, Parsed: RolloutKeys.ParseVersion(p.Version.Version)))
            .Where(x => x.Parsed is not null)
            .GroupBy(x => (x.Plan.Version.Source, x.Plan.Version.Family))
            .OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => g.Key.Family, StringComparer.Ordinal);
        foreach (var family in groups)
        {
            var majors = family.Select(x => x.Parsed!.Major).Distinct().Order().ToList();
            var rules = new List<Item>();
            var uncovered = new List<VersionPlan>();
            var covered = new List<VersionPlan>();
            for (var k = 0; k < majors.Count; k++)
            {
                var major = majors[k];
                var inRange = family.Where(x => x.Parsed!.Major == major).ToList();
                string? min = k == 0 ? null : major.ToString(CultureInfo.InvariantCulture);
                string? max = k == majors.Count - 1 ? null : $"{majors[k + 1] - 1}.{MaxComponent}.{MaxComponent}.{MaxComponent}";
                var best = inRange.Where(x => !x.Plan.Version.Suspended && Eligible(x.Plan, target)).OrderByDescending(x => x.Parsed).FirstOrDefault();
                foreach (var x in inRange.Where(x => !x.Plan.Version.Suspended && !Eligible(x.Plan, target)))
                    (best.Plan is not null && x.Parsed! <= best.Parsed! ? uncovered : covered).Add(x.Plan);
                if (best.Plan is not null)
                    rules.Add(new Item(Rule("", "pin", family.Key.Source, family.Key.Family, kinds, versionMin: min, versionMax: max,
                        pinVersion: best.Plan.Version.Version,
                        reason: $"Diffusion par anneaux ({who}) : version de « {family.Key.Family} » postérieure à {best.Plan.Version.Version} " +
                                $"pas encore autorisée pour cet anneau (une nouvelle version attend {RolloutPlanner.Plural(delay, "jour", "jours")} " +
                                "après sa première détection sur le parc)"), IdBytes));
                else
                    rules.Add(new Item(Rule("", "block", family.Key.Source, family.Key.Family, kinds, versionMin: min, versionMax: max,
                        reason: $"Diffusion par anneaux ({who}) : « {family.Key.Family} » en cours de diffusion, aucune version encore autorisée pour cet anneau"), IdBytes));
            }
            list.Add(new FamilyPlan
            {
                Source = family.Key.Source, Family = family.Key.Family, Kind = RolloutKeys.SourceKind(family.Key.Source), RangeRules = rules,
                Uncovered = uncovered, Covered = covered, LastSeen = family.Max(x => x.Plan.Version.LastSeen),
            });
        }
        return list;
    }

    /// <summary>Version autorisée pour le destinataire (anneau le plus prudent : autorisée pour tous les anneaux).</summary>
    public static bool Eligible(VersionPlan p, PolicyTarget target) =>
        target.Conservative ? p.Completed : target.Ring is { } ring ? p.For(ring.Id)?.Eligible ?? false : !p.Version.Suspended;

    /// <summary>Motif d'une version retenue pour le destinataire, et date de disponibilité si elle ne dépend que du délai.</summary>
    public static (string Reason, DateTimeOffset? AvailableAt) HoldReason(VersionPlan p, PolicyTarget target, string? ringLabel, TimeZoneInfo zone)
    {
        var v = p.Version;
        if (target.Conservative)
        {
            var pendingValidation = p.Rings.Any(r => !r.ValidationMet);
            var at = p.LatestAvailableAt;
            return ($"Pilote {v.DisplayName} : en cours de diffusion par anneaux" +
                    (pendingValidation ? ", en attente de validation dans les premiers anneaux" : $", disponible pour ce poste le {RolloutPlanner.FormatDate(at, zone)}") +
                    " (poste non identifié par la console : anneau le plus prudent ; SpokaPilote 4.0 reçoit son propre anneau)",
                pendingValidation ? null : at);
        }
        var s = p.For(target.Ring!.Id)!;
        var validation = s.ValidationRing is not null && !s.ValidationMet
            ? $"validation dans l'anneau « {s.ValidationRing.Name} » ({s.ValidationStat.Successes}/{s.Ring.MinSuccesses} installation(s) réussie(s), " +
              $"{RolloutPlanner.Plural(s.ValidationStat.Failures, "échec", "échecs")}{(s.Ring.MaxFailures > 0 ? $", {s.Ring.MaxFailures} toléré(s)" : "")})"
            : null;
        if (!s.DateReached)
            return ($"Pilote {v.DisplayName} : disponible pour votre anneau « {ringLabel} » le {RolloutPlanner.FormatDate(s.AvailableAt, zone)}" +
                    (validation is null ? "" : $", après {validation}"), validation is null ? s.AvailableAt : null);
        return ($"Pilote {v.DisplayName} : disponible pour votre anneau « {ringLabel} » après {validation ?? "validation dans l'anneau précédent"}", null);
    }

    private static JsonObject Rule(string id, string action, string source, string? title = null, string[]? kinds = null, string[]? versions = null,
        string? versionMin = null, string? versionMax = null, string? pinVersion = null, string? reason = null, string[]? titles = null)
    {
        var rule = new JsonObject { ["id"] = id, ["action"] = action };
        if (RolloutKeys.SourceKind(source) is { } kind) rule["source"] = kind;
        if (titles is { Length: > 1 }) rule["title"] = new JsonArray(titles.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());
        else rule["title"] = titles is { Length: 1 } ? titles[0] : title;
        if (kinds is not null) rule["kind"] = new JsonArray(kinds.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
        if (versions is { Length: 1 }) rule["versions"] = versions[0];
        else if (versions is not null) rule["versions"] = new JsonArray(versions.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        if (versionMin is not null) rule["versionMin"] = versionMin;
        if (versionMax is not null) rule["versionMax"] = versionMax;
        if (pinVersion is not null) rule["pinVersion"] = pinVersion;
        if (reason is not null) rule["reason"] = reason;
        return rule;
    }

    /// <summary>
    /// Règle « block » du même pilote quelle que soit la source : matériel de ce constructeur (« hardwareId » : ID matériels et
    /// compatibles des périphériques visés), de cette classe (« category » : classe Windows du périphérique visé, si connue),
    /// ces versions. Format 1 : comprise par les postes 3.x.
    /// </summary>
    private static JsonObject HardwareRule(string id, string vendor, string? deviceClass, string[] versions, string reason)
    {
        var rule = new JsonObject
        {
            ["id"] = id,
            ["action"] = "block",
            ["hardwareId"] = new JsonArray(RolloutKeys.VendorPatterns(vendor).Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
        };
        if (deviceClass is not null) rule["category"] = deviceClass;
        rule["versions"] = versions.Length == 1 ? versions[0] : new JsonArray(versions.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        rule["reason"] = reason;
        return rule;
    }

    // ------------------------------------------------------------------ document servi au poste

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Politique servie : la politique de l'organisation (JSON brut, commentaires acceptés) avec les règles de diffusion en
    /// tête de « rules », et la section « rollout » si demandée. null = rien à servir (HTTP 204, comme avant).
    /// Une politique d'organisation illisible est servie telle quelle (le poste la refusera et gardera sa politique précédente).
    /// </summary>
    public static string? Compose(string? organizationJson, RolloutRules rules, JsonObject? rollout)
    {
        JsonObject root;
        if (organizationJson is not null)
        {
            try
            {
                if (JsonNode.Parse(organizationJson, documentOptions: ReadOptions) is not JsonObject parsed) return organizationJson;
                root = parsed;
            }
            catch (JsonException)
            {
                return organizationJson;
            }
            if (rules.Rules.Count == 0 && rollout is null) return organizationJson;
        }
        else
        {
            if (rules.Rules.Count == 0 && rollout is null) return null;
            root = new JsonObject
            {
                ["schema"] = 1,
                ["name"] = "Diffusion par anneaux",
                ["description"] = "Politique générée par la console SpokaPilote (aucune politique d'organisation publiée) : suspensions et retenues de la diffusion par anneaux.",
            };
        }

        var rulesKey = root.Select(p => p.Key).FirstOrDefault(k => k.Equals("rules", StringComparison.OrdinalIgnoreCase)) ?? "rules";
        var list = root[rulesKey] as JsonArray ?? new JsonArray();
        root[rulesKey] = list;
        for (var i = rules.Rules.Count - 1; i >= 0; i--) list.Insert(0, rules.Rules[i].DeepClone());
        if (rollout is not null)
        {
            foreach (var key in root.Select(p => p.Key).Where(k => k.Equals("rollout", StringComparison.OrdinalIgnoreCase)).ToList()) root.Remove(key);
            root["rollout"] = rollout;
        }
        return root.ToJsonString();
    }

    /// <summary>Section « rollout » des postes 4.0 (lue par SpokaPilote.Core : PolicyRollout).</summary>
    /// <param name="decisions">Poste 4.0 en mode décisions : les retenues lui sont données proposition par proposition
    /// (POST /api/rollout/decide) ; le document ne contient que les suspensions.</param>
    public static JsonObject RolloutSection(RolloutSnapshot s, PolicyTarget target, RolloutRules rules, bool machineKnown, DateTimeOffset now,
        bool decisions = false)
    {
        var o = new JsonObject();
        if (target.Ring is { } ring)
        {
            var index = s.Rings.ToList().FindIndex(r => r.Id == ring.Id);
            o["ring"] = ring.Name;
            o["ringPosition"] = index + 1;
            o["delayDays"] = ring.DelayDays;
            if (ring.RequireValidation && index > 0)
            {
                o["validationRing"] = s.Rings[index - 1].Name;
                o["minSuccesses"] = ring.MinSuccesses;
                o["maxFailures"] = ring.MaxFailures;
            }
        }
        else
        {
            o["delayDays"] = s.Rings.Count == 0 ? 0 : s.Rings.Max(r => r.DelayDays);
        }
        o["ringCount"] = s.Rings.Count;
        o["enabled"] = s.RingsActive;
        o["conservative"] = target.Conservative;
        o["machineKnown"] = machineKnown;
        o["holdUnknown"] = decisions ? HoldsNewVersions(s, target) : rules.HoldUnknown;
        // Postes 4.0 : décisions par proposition (absent : retenues dans les règles, comportement des consoles 4.0 antérieures).
        if (decisions) o["decisions"] = true;
        else if (rules.Form != "complete") o["form"] = rules.Form;
        o["applyToMissingDrivers"] = s.Settings.ApplyToMissingDrivers;
        o["generatedAt"] = now.ToUniversalTime().ToString("O");
        var availability = new JsonObject();
        foreach (var (id, at) in rules.Availability) availability[id] = at;
        o["availability"] = availability;
        return o;
    }
}
