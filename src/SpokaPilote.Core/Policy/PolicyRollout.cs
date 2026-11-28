using System.Globalization;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Policy;

/// <summary>
/// Section « rollout » d'une politique servie par la console 4.0 (diffusion par anneaux). Elle n'est envoyée qu'aux postes
/// qui la demandent (en-tête X-SpokaPilote-Rollout) : les postes 3.x, qui refusent toute propriété inconnue, ne la reçoivent
/// jamais — ils reçoivent les mêmes retenues sous forme de règles « block » / « pin » ordinaires.
/// <para>
/// Les retenues elles-mêmes sont des règles de la politique (identifiants « anneau-… », placées en tête) ; cette section
/// apporte l'anneau du poste, les dates de disponibilité (affichées à l'heure locale) et la consigne <see cref="HoldUnknown"/> :
/// une version d'une famille de pilotes jamais vue par la console est retenue tant qu'un rapport ne l'a pas signalée.
/// Lecture tolérante (propriétés inconnues ignorées) : une console plus récente peut y ajouter des informations.
/// </para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class PolicyRollout
{
    public const string RulePrefix = "anneau-";
    public const string SuspendedPrefix = RulePrefix + "suspendu-";
    /// <summary>Règle complémentaire d'une suspension (console 4.0.1) : même version, même matériel, quelle que soit la source.</summary>
    public const string SuspendedHardwarePrefix = SuspendedPrefix + "materiel-";
    public const string HoldPrefix = RulePrefix + "retenue-";
    public const string FamilyPrefix = RulePrefix + "famille-";

    /// <summary>Anneau du poste (null : poste non identifié → anneau le plus prudent).</summary>
    public string? Ring { get; set; }
    public int? RingPosition { get; set; }
    public int RingCount { get; set; }
    /// <summary>Délai de l'anneau, en jours, à compter de la première détection d'une version sur le parc.</summary>
    public int DelayDays { get; set; }
    /// <summary>Anneau précédent dont la validation est exigée (null : aucune).</summary>
    public string? ValidationRing { get; set; }
    public int? MinSuccesses { get; set; }
    public int? MaxFailures { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Poste non identifié par la console : politique de l'anneau le plus prudent.</summary>
    public bool Conservative { get; set; }
    /// <summary>La console connaît ce poste (au moins un rapport reçu) ; sinon il est dans l'anneau par défaut.</summary>
    public bool MachineKnown { get; set; }
    /// <summary>Retenir les versions d'une famille jamais vue par la console (anneau avec délai ou validation).</summary>
    public bool HoldUnknown { get; set; }
    public bool ApplyToMissingDrivers { get; set; }
    /// <summary>
    /// Console qui décide proposition par proposition (POST /api/rollout/decide) : la politique ne contient que les
    /// suspensions ; les retenues viennent des décisions demandées après la recherche des pilotes (<see cref="RolloutDecisions"/>).
    /// Absent (consoles 4.0 antérieures) : retenues dans les règles « anneau-… » de la politique.
    /// </summary>
    public bool Decisions { get; set; }
    public string? GeneratedAt { get; set; }
    /// <summary>Identifiant de règle de retenue → instant de disponibilité (ISO 8601).</summary>
    public Dictionary<string, string>? Availability { get; set; }

    [JsonIgnore] public string RingLabel => Ring is null ? "l'anneau le plus prudent" : $"« {Ring} »";

    /// <summary>Instant de disponibilité d'une retenue (null : inconnu, par ex. en attente de validation).</summary>
    public DateTimeOffset? AvailableAt(string? ruleId) =>
        ruleId is not null && Availability is not null && Availability.TryGetValue(ruleId, out var text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>Le type de proposition est-il soumis aux anneaux ? (pilotes manquants : seulement si la console le demande)</summary>
    public bool AppliesTo(DriverCandidate c) => ApplyToMissingDrivers || c.Kind != CandidateKind.MissingDriver;

    /// <summary>« 08/12/2026 à 14:05 », à l'heure locale du poste.</summary>
    public static string FormatLocal(DateTimeOffset at) => at.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm", CultureInfo.GetCultureInfo("fr-FR"));

    /// <summary>« Intel - Display - 31.0.101.5590 » ou « Intel UHD Graphics 31.0.101.5590 ».</summary>
    public static string DisplayName(DriverCandidate c) =>
        string.IsNullOrEmpty(c.Version) || c.Title.Contains(c.Version, StringComparison.OrdinalIgnoreCase) ? c.Title : $"{c.Title} {c.Version}";

    /// <summary>Motif d'une retenue datée, à l'heure locale : « Pilote X 31.0.101.5590 : disponible pour votre anneau « Général » le 08/12/2026 à 14:05 ».</summary>
    public string AvailableReason(DriverCandidate c, DateTimeOffset at) => Ring is null
        ? $"Pilote {DisplayName(c)} : en cours de diffusion par anneaux, disponible pour ce poste le {FormatLocal(at)} (anneau le plus prudent : poste non identifié par la console)"
        : $"Pilote {DisplayName(c)} : disponible pour votre anneau « {Ring} » le {FormatLocal(at)}";

    /// <summary>Motif d'une proposition sans décision de la console (console injoignable, aucune décision en cache).</summary>
    public string NoDecisionReason(DriverCandidate c) =>
        $"Pilote {DisplayName(c)} : décision de diffusion indisponible (console injoignable, aucune décision connue pour cette version) — " +
        $"retenu pour {(Ring is null ? "ce poste" : $"votre anneau « {Ring} »")} jusqu'au retour de la console";

    /// <summary>Motif d'une version jamais vue par la console (famille inconnue).</summary>
    public string UnknownReason(DriverCandidate c, DateTime now)
    {
        var earliest = new DateTimeOffset(now).AddDays(DelayDays);
        var validation = ValidationRing is null ? "" : $", après validation dans l'anneau « {ValidationRing} »";
        return $"Pilote {DisplayName(c)} : nouveau pilote, pas encore vu par la console — retenu pour {(Ring is null ? "ce poste" : $"votre anneau « {Ring} »")} " +
               $"(disponible au plus tôt le {FormatLocal(earliest)}{validation}, une fois signalé à la console par un rapport)";
    }
}
