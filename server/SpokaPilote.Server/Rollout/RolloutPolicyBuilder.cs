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

/// <summary>Règles ajoutées par la diffusion par anneaux pour un destinataire.</summary>
public sealed class RolloutRules
{
    public List<JsonObject> Rules { get; } = new();
    /// <summary>Identifiant de règle de retenue → date à laquelle la version devient disponible (instant ISO 8601).</summary>
    public Dictionary<string, string> Availability { get; } = new(StringComparer.Ordinal);
    public bool HoldUnknown { get; set; }
    public int Suspensions { get; set; }
    public int Holds { get; set; }
    public int Families { get; set; }
}

/// <summary>
/// Génère la politique effective d'un poste : politique de l'organisation + règles de diffusion, exprimées UNIQUEMENT avec
/// des règles du format 1 (block / pin, critères source, title, kind, versions, versionMin/versionMax) que tous les postes
/// 3.x comprennent déjà. Les postes 4.0 reçoivent en plus une section « rollout » (anneau, dates de disponibilité), sur
/// demande explicite (en-tête X-SpokaPilote-Rollout) : un poste 3.x, qui refuse toute propriété inconnue, ne la reçoit jamais.
/// <para>
/// Ordre (les règles de diffusion passent avant celles de l'organisation : une règle « allow » ne lève pas une retenue) :
/// 1. versions suspendues (tous types, tous anneaux) ; 2. versions en cours de diffusion pas encore autorisées pour
/// l'anneau (date de disponibilité dans le motif) ; 3. règles de famille (« pin » à la version la plus récente autorisée
/// pour l'anneau, ou « block » si aucune ne l'est encore) : une version plus récente, même jamais vue par la console, est
/// retenue — c'est ce qui empêche un poste de l'installer avant que la console ne l'ait vue (afflux à heure fixe).
/// Seules les versions en cours de diffusion sont listées (pas l'historique) ; une famille est une règle, quelle que soit
/// le nombre de ses versions.
/// </para>
/// </summary>
public static class RolloutPolicyBuilder
{
    public const string RulePrefix = "anneau-";
    public const string SuspendedPrefix = RulePrefix + "suspendu-";
    public const string HoldPrefix = RulePrefix + "retenue-";
    public const string FamilyPrefix = RulePrefix + "famille-";
    /// <summary>Plus grande composante de version (versionMax ouverte d'une plage de versions majeures).</summary>
    private const string MaxComponent = "2147483647";

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

    public static RolloutRules Build(RolloutSnapshot s, IReadOnlyList<VersionPlan> plans, PolicyTarget target, DateTimeOffset now, TimeZoneInfo zone)
    {
        var result = new RolloutRules();
        var kinds = s.Settings.ApplyToMissingDrivers ? null : UpdateKinds;

        // 1. Suspensions : toujours servies (même anneaux désactivés), sans restriction de type.
        foreach (var p in plans.Where(p => p.Version.Suspended).OrderBy(p => p.Version.Id))
        {
            var v = p.Version;
            result.Rules.Add(Rule(SuspendedPrefix + v.Id, "block", v.Source, v.Title, null, versions: v.Version,
                reason: $"Pilote {v.DisplayName} : {v.SuspendReason ?? "suspendu sur tout le parc par l'équipe informatique"}"));
            result.Suspensions++;
        }
        if (!s.RingsActive || (target.Ring is null && !target.Conservative)) return result;

        var ringLabel = target.Conservative ? null : target.Ring!.Name;

        // 2. Versions en cours de diffusion, pas encore autorisées pour ce destinataire.
        foreach (var p in plans.Where(p => !p.Version.Suspended && !Eligible(p, target)).OrderBy(p => p.Version.FirstSeen).ThenBy(p => p.Version.Id))
        {
            var v = p.Version;
            var id = HoldPrefix + v.Id;
            var (reason, availableAt) = HoldReason(p, target, ringLabel, zone);
            result.Rules.Add(Rule(id, "block", v.Source, v.Title, kinds, versions: v.Version, reason: reason));
            if (availableAt is { } at) result.Availability[id] = at.ToUniversalTime().ToString("O");
            result.Holds++;
        }

        // 3. Familles : une version plus récente que la plus récente autorisée (même inconnue de la console) est retenue.
        result.HoldUnknown = HoldsNewVersions(s, target);
        if (!result.HoldUnknown) return result;
        var since = now.AddDays(-s.Settings.FamilyRetentionDays);
        var families = plans
            .Where(p => p.Version.LastSeen >= since || !p.Completed)
            .Select(p => (Plan: p, Parsed: RolloutKeys.ParseVersion(p.Version.Version)))
            .Where(x => x.Parsed is not null)
            .GroupBy(x => (x.Plan.Version.Source, x.Plan.Version.Family))
            .OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => g.Key.Family, StringComparer.Ordinal);
        var delay = target.Conservative ? s.Rings.Max(r => r.DelayDays) : target.Ring!.DelayDays;
        var n = 0;
        foreach (var family in families)
        {
            var majors = family.Select(x => x.Parsed!.Major).Distinct().Order().ToList();
            for (var k = 0; k < majors.Count; k++)
            {
                var major = majors[k];
                var inRange = family.Where(x => x.Parsed!.Major == major).ToList();
                string? min = k == 0 ? null : major.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string? max = k == majors.Count - 1 ? null : $"{majors[k + 1] - 1}.{MaxComponent}.{MaxComponent}.{MaxComponent}";
                var best = inRange.Where(x => !x.Plan.Version.Suspended && Eligible(x.Plan, target)).OrderByDescending(x => x.Parsed).Select(x => x.Plan).FirstOrDefault();
                var who = ringLabel is null ? "anneau le plus prudent" : $"anneau « {ringLabel} »";
                n++;
                if (best is not null)
                    result.Rules.Add(Rule(FamilyPrefix + n, "pin", family.Key.Source, family.Key.Family, kinds, versionMin: min, versionMax: max,
                        pinVersion: best.Version.Version,
                        reason: $"Diffusion par anneaux ({who}) : version de « {family.Key.Family} » postérieure à {best.Version.Version} " +
                                $"pas encore autorisée pour cet anneau (une nouvelle version attend {RolloutPlanner.Plural(delay, "jour", "jours")} " +
                                "après sa première détection sur le parc)"));
                else
                    result.Rules.Add(Rule(FamilyPrefix + n, "block", family.Key.Source, family.Key.Family, kinds, versionMin: min, versionMax: max,
                        reason: $"Diffusion par anneaux ({who}) : « {family.Key.Family} » en cours de diffusion, aucune version encore autorisée pour cet anneau"));
                result.Families++;
            }
        }
        return result;
    }

    /// <summary>Version autorisée pour le destinataire (anneau le plus prudent : autorisée pour tous les anneaux).</summary>
    public static bool Eligible(VersionPlan p, PolicyTarget target) =>
        target.Conservative ? p.Completed : target.Ring is { } ring ? p.For(ring.Id)?.Eligible ?? false : !p.Version.Suspended;

    private static (string Reason, DateTimeOffset? AvailableAt) HoldReason(VersionPlan p, PolicyTarget target, string? ringLabel, TimeZoneInfo zone)
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

    private static JsonObject Rule(string id, string action, string source, string title, string[]? kinds, string? versions = null,
        string? versionMin = null, string? versionMax = null, string? pinVersion = null, string? reason = null)
    {
        var rule = new JsonObject { ["id"] = id, ["action"] = action };
        if (RolloutKeys.SourceKind(source) is { } kind) rule["source"] = kind;
        rule["title"] = title;
        if (kinds is not null) rule["kind"] = new JsonArray(kinds.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
        if (versions is not null) rule["versions"] = versions;
        if (versionMin is not null) rule["versionMin"] = versionMin;
        if (versionMax is not null) rule["versionMax"] = versionMax;
        if (pinVersion is not null) rule["pinVersion"] = pinVersion;
        if (reason is not null) rule["reason"] = reason;
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
    public static JsonObject RolloutSection(RolloutSnapshot s, PolicyTarget target, RolloutRules rules, bool machineKnown, DateTimeOffset now)
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
        o["holdUnknown"] = rules.HoldUnknown;
        o["applyToMissingDrivers"] = s.Settings.ApplyToMissingDrivers;
        o["generatedAt"] = now.ToUniversalTime().ToString("O");
        var availability = new JsonObject();
        foreach (var (id, at) in rules.Availability) availability[id] = at;
        o["availability"] = availability;
        return o;
    }
}
