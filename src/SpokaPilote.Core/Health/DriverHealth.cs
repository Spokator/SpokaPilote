using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;

namespace SpokaPilote.Core.Health;

/// <summary>Gravité d'un constat. Sérialisée en minuscules (« critical », « warning », « info ») : contrat de la console.</summary>
public enum HealthSeverity { Critical, Warning, Info }

internal sealed class HealthSeverityJsonConverter() : JsonStringEnumConverter<HealthSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>Codes stables des constats (exploités par la console centrale et l'interface).</summary>
public static class HealthCodes
{
    /// <summary>Pilote installé visé par une règle « block » de la liste de référence ou de la politique de l'organisation.</summary>
    public const string KnownBadDriver = "known-bad-driver";
    /// <summary>Périphérique sans pilote (codes 1, 18, 28).</summary>
    public const string MissingDriver = "missing-driver";
    /// <summary>Périphérique en erreur (codes 10, 43, 31...), hors panne matérielle.</summary>
    public const string DeviceError = "device-error";
    /// <summary>Panne matérielle (USB qui ne répond pas...) : aucun pilote ne peut la corriger.</summary>
    public const string HardwareFault = "hardware-fault";
    /// <summary>Périphérique désactivé (code 22), le plus souvent volontairement.</summary>
    public const string DeviceDisabled = "device-disabled";
    /// <summary>Pilote installé non signé.</summary>
    public const string UnsignedDriver = "unsigned-driver";
    /// <summary>Pilote tiers très ancien (voir <see cref="DriverHealth.OldDriverYears"/>).</summary>
    public const string OldDriver = "old-driver";
    /// <summary>Mise à jour de pilote (ou de firmware) proposée par une source officielle et autorisée par la politique.</summary>
    public const string UpdateAvailable = "update-available";
}

/// <summary>Constat de santé. Seuls severity, code, title, detail et deviceId sont publiés dans le rapport JSON.</summary>
public sealed class HealthFinding
{
    /// <summary>Attribut sur la propriété : il l'emporte sur le convertisseur d'énumérations global des rapports.</summary>
    [JsonConverter(typeof(HealthSeverityJsonConverter))]
    public HealthSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Identifiant d'instance du périphérique concerné (absent pour un constat global).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Points retirés au score par ce constat (après plafonnement de sa catégorie).</summary>
    [JsonIgnore] public int Penalty { get; set; }
    /// <summary>Action proposée au technicien (également reprise à la fin de <see cref="Detail"/>).</summary>
    [JsonIgnore] public string? Recommendation { get; set; }
    /// <summary>Lien de l'avis officiel (pilote dangereux).</summary>
    [JsonIgnore] public string? AdvisoryUrl { get; set; }

    [JsonIgnore]
    public string SeverityLabel => Severity switch
    {
        HealthSeverity.Critical => "critique",
        HealthSeverity.Warning => "attention",
        _ => "info",
    };
}

/// <summary>
/// Santé des pilotes d'un poste. Contrat JSON (objet « health » du rapport) :
/// <c>{ "score": 0-100, "grade": "A".."E", "findings": [ { "severity", "code", "title", "detail", "deviceId" } ] }</c>.
/// </summary>
public sealed class HealthReport
{
    public int Score { get; set; } = 100;
    public string Grade { get; set; } = "A";
    /// <summary>Constats triés : critiques, puis avertissements, puis informations.</summary>
    public List<HealthFinding> Findings { get; set; } = new();

    [JsonIgnore] public int CriticalCount => Findings.Count(f => f.Severity == HealthSeverity.Critical);
    [JsonIgnore] public int WarningCount => Findings.Count(f => f.Severity == HealthSeverity.Warning);
    [JsonIgnore] public int InfoCount => Findings.Count(f => f.Severity == HealthSeverity.Info);

    /// <summary>Phrase de synthèse (« Note B (82/100) : 1 avertissement, 3 informations »).</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (CriticalCount > 0) parts.Add($"{CriticalCount} critique(s)");
            if (WarningCount > 0) parts.Add($"{WarningCount} avertissement(s)");
            if (InfoCount > 0) parts.Add($"{InfoCount} information(s)");
            return $"Note {Grade} ({Score}/100) : {(parts.Count == 0 ? "aucun constat" : string.Join(", ", parts))}";
        }
    }

    [JsonIgnore]
    public string GradeLabel => Grade switch
    {
        "A" => "Excellent",
        "B" => "Bon",
        "C" => "À surveiller",
        "D" => "À traiter",
        _ => "Critique",
    };
}

/// <summary>Données facultatives du calcul (tests, contexte du poste).</summary>
public sealed class HealthOptions
{
    /// <summary>Date de référence (âge des pilotes, expiration des règles). Défaut : maintenant.</summary>
    public DateTime? Now { get; init; }
    /// <summary>oemNN.inf → nom d'origine de l'INF (ex. oem42.inf → intcaudiobus.inf), pour les règles « title ».</summary>
    public IReadOnlyDictionary<string, string>? OriginalInfNames { get; init; }
    /// <summary>Historique des interventions SpokaPilote : permet de proposer « rollback » pour un pilote dangereux installé par SpokaPilote.</summary>
    public IReadOnlyList<Intervention>? Interventions { get; init; }
}

/// <summary>
/// Calcul de la santé des pilotes : score 0-100, note A-E et constats.
/// <para>
/// Pondération (points retirés à 100, documentée dans docs/OUTILS-TECHNICIEN.md) :
/// <list type="bullet">
/// <item>pilote dangereux installé (« known-bad-driver », critique) : −30 chacun, et la note est plafonnée à D (score ≤ 59) ;</item>
/// <item>périphérique sans pilote ou en erreur (« missing-driver », « device-error ») : −15 chacun ;</item>
/// <item>pilote non signé (« unsigned-driver ») : −15 chacun ;</item>
/// <item>pilote tiers de plus de <see cref="OldDriverYears"/> ans (« old-driver ») : −3 chacun, −15 au total au maximum ;</item>
/// <item>mise à jour disponible (« update-available ») : −2 chacune, −10 au total au maximum ;</item>
/// <item>panne matérielle, périphérique désactivé : information seule, 0 point.</item>
/// </list>
/// Note : A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, E en dessous. Un poste sans constat obtient 100/A.
/// </para>
/// </summary>
public static partial class DriverHealth
{
    public const int OldDriverYears = 5;

    public const int PenaltyKnownBadDriver = 30;
    public const int PenaltyMissingOrError = 15;
    public const int PenaltyUnsigned = 15;
    public const int PenaltyOldDriver = 3;
    public const int MaxPenaltyOldDrivers = 15;
    public const int PenaltyUpdate = 2;
    public const int MaxPenaltyUpdates = 10;
    /// <summary>Score maximal lorsqu'un pilote dangereux est installé (note D au mieux).</summary>
    public const int KnownBadScoreCap = 59;

    public static string GradeFor(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "E",
    };

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemInf();

    /// <summary>Pilote tiers (paquet oemNN.inf ajouté au magasin), par opposition aux pilotes intégrés à Windows.</summary>
    public static bool IsThirdPartyDriver(DeviceInfo d) => d.InfName is { } inf && OemInf().IsMatch(inf);

    /// <summary>Santé à partir d'une analyse (inventaire, pilotes proposés, politique appliquée).</summary>
    public static HealthReport Compute(Engine.ScanResult scan, HealthOptions? options = null) =>
        Compute(scan.Devices, scan.Candidates, scan.Policy?.Effective, options);

    /// <param name="devices">Inventaire du poste.</param>
    /// <param name="candidates">Pilotes proposés par l'analyse (vide si les sources n'ont pas été interrogées).</param>
    /// <param name="policy">Politique effective (organisation + liste de référence) ; null = aucune règle.</param>
    public static HealthReport Compute(IReadOnlyList<DeviceInfo> devices, IReadOnlyList<DriverCandidate> candidates,
        DriverPolicy? policy, HealthOptions? options = null)
    {
        options ??= new HealthOptions();
        var now = options.Now ?? DateTime.Now;
        var findings = new List<HealthFinding>();
        var present = devices.Where(d => d.IsPresent).ToList();

        bool Targets(DriverCandidate c, DeviceInfo d) => c.TargetDeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase);
        DriverCandidate? Installable(DeviceInfo d) => candidates.FirstOrDefault(c => Targets(c, d) && c.BlockedReason is null && !c.IsInformational);

        // 1. Pilotes dangereux déjà installés (liste de référence + politique de l'organisation).
        var knownBad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (policy is not null)
        {
            foreach (var d in present.Where(d => !string.IsNullOrWhiteSpace(d.DriverVersion)))
            {
                var original = d.InfName is not null && options.OriginalInfNames?.TryGetValue(d.InfName, out var o) == true ? o : null;
                if (policy.FindInstalledBlock(d, original, now) is not { } rule) continue;
                knownBad.Add(d.InstanceId);
                var update = Installable(d);
                var intervention = d.InfName is null ? null : options.Interventions?
                    .FirstOrDefault(i => i.CanRollback && i.AddedDriverPackages.Contains(d.InfName, StringComparer.OrdinalIgnoreCase));
                var recommendation = update is not null
                    ? $"Mettez à jour : {update.Title} {update.Version} ({update.SourceLabel}) est proposé — spokapilote install --only \"{d.Name}\"."
                    : intervention is not null
                        ? $"Annulez l'intervention SpokaPilote qui l'a installé : spokapilote rollback {intervention.Id}."
                        : "Installez une version corrigée (Windows Update ou site du constructeur), ou revenez au pilote précédent " +
                          "(Gestionnaire de périphériques > Propriétés > Pilote > Restaurer le pilote).";
                var inf = d.InfName is null ? "" : $", {d.InfName}{(original is null ? "" : $" / {original}")}";
                var where = rule.Layer == PolicyLayer.Reference ? "liste de référence SpokaPilote" : "politique de l'organisation";
                findings.Add(new HealthFinding
                {
                    Severity = HealthSeverity.Critical,
                    Code = HealthCodes.KnownBadDriver,
                    Title = $"Pilote dangereux installé : {d.Name}",
                    Detail = $"Version {d.DriverVersion} ({d.DriverProvider ?? d.Manufacturer ?? "fournisseur inconnu"}{inf}) signalée par la {where} " +
                             $"(règle {rule.Label}) : {rule.Reason ?? "pilote bloqué"}" +
                             (rule.SourceUrl is null ? "" : $" Avis : {rule.SourceUrl}") + $" → {recommendation}",
                    DeviceId = d.InstanceId,
                    Penalty = PenaltyKnownBadDriver,
                    Recommendation = recommendation,
                    AdvisoryUrl = rule.SourceUrl,
                });
            }
        }

        // 2. Périphériques sans pilote, en erreur, en panne matérielle ou désactivés.
        foreach (var d in present)
        {
            if (d.IsHardwareFault)
            {
                findings.Add(new HealthFinding
                {
                    Severity = HealthSeverity.Info,
                    Code = HealthCodes.HardwareFault,
                    Title = $"Panne matérielle probable : {d.Name}",
                    Detail = $"{d.StatusText} (code {d.ErrorCode}). {d.Hint ?? "Aucun pilote ne peut corriger ce problème."} Non compté dans la note : ce n'est pas un problème de pilote.",
                    DeviceId = d.InstanceId,
                    Recommendation = "Vérifiez le câble, le port et l'alimentation du périphérique.",
                });
                continue;
            }
            if (d.Health == DeviceHealth.Disabled)
            {
                findings.Add(new HealthFinding
                {
                    Severity = HealthSeverity.Info,
                    Code = HealthCodes.DeviceDisabled,
                    Title = $"Périphérique désactivé : {d.Name}",
                    Detail = "Désactivé (code 22), souvent volontairement. Non compté dans la note. Pour le réactiver : spokapilote device repair \"" + d.InstanceId + "\" --action enable.",
                    DeviceId = d.InstanceId,
                    Recommendation = "Réactivez-le s'il n'a pas été désactivé volontairement.",
                });
                continue;
            }
            if (!d.HasProblem) continue;

            var available = Installable(d);
            var missing = d.Health == DeviceHealth.MissingDriver;
            var recommendationText = available is not null
                ? $"Pilote disponible : {available.Title} {available.Version} ({available.SourceLabel}) — spokapilote install --only \"{d.Name}\"."
                : missing
                    ? "Aucun pilote trouvé par les sources interrogées : vérifiez le site du constructeur ou le dépôt interne."
                    : $"Réparation guidée : spokapilote device repair \"{d.InstanceId}\".";
            findings.Add(new HealthFinding
            {
                Severity = HealthSeverity.Warning,
                Code = missing ? HealthCodes.MissingDriver : HealthCodes.DeviceError,
                Title = missing ? $"Pilote manquant : {d.Name}" : $"Périphérique en erreur : {d.Name}",
                Detail = $"{d.StatusText} (code {d.ErrorCode}).{(d.Hint is null ? "" : " " + d.Hint)} → {recommendationText}",
                DeviceId = d.InstanceId,
                Penalty = PenaltyMissingOrError,
                Recommendation = recommendationText,
            });
        }

        // 3. Pilotes non signés.
        foreach (var d in present.Where(d => d.IsSigned == false && !string.IsNullOrWhiteSpace(d.DriverVersion)))
        {
            findings.Add(new HealthFinding
            {
                Severity = HealthSeverity.Warning,
                Code = HealthCodes.UnsignedDriver,
                Title = $"Pilote non signé : {d.Name}",
                Detail = $"Le pilote {d.DriverVersion} ({d.DriverProvider ?? "fournisseur inconnu"}{(d.InfName is null ? "" : ", " + d.InfName)}) n'est pas signé : " +
                         "son origine et son intégrité ne sont pas garanties. → Remplacez-le par un pilote signé d'une source officielle.",
                DeviceId = d.InstanceId,
                Penalty = PenaltyUnsigned,
                Recommendation = "Remplacez-le par un pilote signé d'une source officielle.",
            });
        }

        // 4. Pilotes tiers très anciens (les pilotes intégrés à Windows, souvent datés de 2006, sont ignorés).
        var limit = now.AddYears(-OldDriverYears);
        var oldBudget = MaxPenaltyOldDrivers;
        foreach (var d in present.Where(d => d.IsHardware && IsThirdPartyDriver(d) && d.DriverDate is { } date && date < limit && !knownBad.Contains(d.InstanceId))
                                 .OrderBy(d => d.DriverDate))
        {
            var penalty = Math.Min(PenaltyOldDriver, oldBudget);
            oldBudget -= penalty;
            var update = Installable(d);
            var years = (int)((now - d.DriverDate!.Value).TotalDays / 365.25);
            var recommendation = update is not null
                ? $"Mise à jour proposée : {update.Title} {update.Version} ({update.SourceLabel})."
                : "Aucune version plus récente proposée par les sources officielles : à surveiller, sans urgence si le périphérique fonctionne.";
            findings.Add(new HealthFinding
            {
                Severity = HealthSeverity.Info,
                Code = HealthCodes.OldDriver,
                Title = $"Pilote ancien : {d.Name}",
                Detail = $"Pilote {d.DriverVersion} du {d.DriverDate:dd/MM/yyyy} ({years} ans, {d.DriverProvider ?? "fournisseur inconnu"}). → {recommendation}",
                DeviceId = d.InstanceId,
                Penalty = penalty,
                Recommendation = recommendation,
            });
        }

        // 5. Mises à jour disponibles (autorisées par la politique, installables automatiquement).
        var updateBudget = MaxPenaltyUpdates;
        foreach (var c in candidates.Where(c => c.BlockedReason is null && !c.IsInformational && c.Kind is CandidateKind.Update or CandidateKind.Firmware))
        {
            var penalty = Math.Min(PenaltyUpdate, updateBudget);
            updateBudget -= penalty;
            var target = c.TargetDeviceIds.FirstOrDefault();
            var names = c.TargetDeviceNames.Distinct().ToList();
            findings.Add(new HealthFinding
            {
                Severity = HealthSeverity.Info,
                Code = HealthCodes.UpdateAvailable,
                Title = $"{(c.Kind == CandidateKind.Firmware ? "Mise à jour BIOS / firmware" : "Mise à jour")} disponible : {c.Title}",
                Detail = $"Version {c.Version ?? "?"} ({c.SourceLabel}){(names.Count == 0 ? "" : " pour " + string.Join(", ", names.Take(3)))}. → spokapilote install",
                DeviceId = target,
                Penalty = penalty,
                Recommendation = "spokapilote install",
            });
        }

        // Score et note.
        var score = Math.Max(0, 100 - findings.Sum(f => f.Penalty));
        if (findings.Any(f => f.Code == HealthCodes.KnownBadDriver)) score = Math.Min(score, KnownBadScoreCap);
        return new HealthReport
        {
            Score = score,
            Grade = GradeFor(score),
            Findings = Sort(findings),
        };
    }

    /// <summary>Tri d'affichage : gravité, puis points retirés, puis titre.</summary>
    public static List<HealthFinding> Sort(IEnumerable<HealthFinding> findings) => findings
        .OrderBy(f => f.Severity)
        .ThenByDescending(f => f.Penalty)
        .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
