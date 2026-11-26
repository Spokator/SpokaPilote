using System.Globalization;

namespace SpokaPilote.Server.Rollout;

/// <summary>État d'une version pour un anneau.</summary>
public enum RingState
{
    /// <summary>Délai de l'anneau pas encore écoulé.</summary>
    Waiting,
    /// <summary>Délai écoulé, validation dans l'anneau précédent pas encore acquise.</summary>
    AwaitingValidation,
    /// <summary>Installable par les postes de l'anneau.</summary>
    Allowed,
    /// <summary>Installable, et assez d'installations réussies sans échec dans cet anneau.</summary>
    Validated,
    /// <summary>Version suspendue sur tout le parc.</summary>
    Suspended,
}

/// <summary>Situation d'une version dans un anneau.</summary>
/// <param name="AvailableAt">Fin du délai de l'anneau : première détection + délai (instant UTC, indépendant des fuseaux).</param>
/// <param name="ValidationRing">Anneau précédent dont la validation est exigée (null si aucune).</param>
/// <param name="ValidationStat">Installations de la version dans l'anneau précédent (si validation exigée).</param>
public sealed record RingStatus(
    RingRecord Ring,
    RingState State,
    DateTimeOffset AvailableAt,
    bool Eligible,
    RingStat Stat,
    RingRecord? ValidationRing,
    RingStat ValidationStat,
    bool DateReached,
    bool ValidationMet)
{
    public string StateCode => State switch
    {
        RingState.Waiting => "waiting",
        RingState.AwaitingValidation => "validation",
        RingState.Allowed => "allowed",
        RingState.Validated => "validated",
        _ => "suspended",
    };
}

/// <summary>Situation d'une version sur tous les anneaux.</summary>
public sealed class VersionPlan
{
    public required RolloutVersion Version { get; init; }
    public required IReadOnlyList<RingStatus> Rings { get; init; }

    /// <summary>Autorisée pour tous les anneaux (diffusion terminée) et non suspendue.</summary>
    public bool Completed => !Version.Suspended && Rings.All(r => r.Eligible);
    /// <summary>« rollout » (en cours de diffusion), « completed » (terminée), « suspended ».</summary>
    public string Status => Version.Suspended ? "suspended" : Completed ? "completed" : "rollout";

    public RingStatus? For(long ringId) => Rings.FirstOrDefault(r => r.Ring.Id == ringId);

    /// <summary>Fin du délai pour tous les anneaux (anneau le plus prudent).</summary>
    public DateTimeOffset LatestAvailableAt => Rings.Count == 0 ? Version.FirstSeen : Rings.Max(r => r.AvailableAt);
}

/// <summary>
/// Calcul des états de diffusion (fonctions pures : mêmes entrées, même résultat ; testable sans base ni horloge).
/// <para>
/// Une version est autorisée pour l'anneau n si elle n'est pas suspendue et, soit elle a été approuvée pour tout le parc
/// (« Diffuser partout maintenant »), soit : (1) première détection + délai de l'anneau ≤ maintenant, et (2) si l'anneau
/// l'exige, la version est validée dans l'anneau précédent (au moins MinSuccesses postes où elle est installée avec succès,
/// au plus MaxFailures postes en échec — seuils de l'anneau n). Le calcul est refait en permanence : un échec apparu dans
/// l'anneau précédent retire l'autorisation aux anneaux suivants qui l'exigent (les postes déjà installés ne sont pas touchés).
/// </para>
/// </summary>
public static class RolloutPlanner
{
    public static List<VersionPlan> Plan(RolloutSnapshot snapshot, DateTimeOffset now)
    {
        var plans = new List<VersionPlan>(snapshot.Versions.Count);
        foreach (var v in snapshot.Versions) plans.Add(PlanVersion(snapshot, v, now));
        return plans;
    }

    public static VersionPlan PlanVersion(RolloutSnapshot snapshot, RolloutVersion v, DateTimeOffset now)
    {
        var rings = snapshot.Rings;
        var statuses = new List<RingStatus>(rings.Count);
        for (var i = 0; i < rings.Count; i++)
        {
            var ring = rings[i];
            var availableAt = v.FirstSeen.AddDays(ring.DelayDays);
            var dateReached = now >= availableAt;
            var previous = ring.RequireValidation && i > 0 ? rings[i - 1] : null;
            var previousStat = previous is null ? RingStat.Empty : snapshot.Stat(v.Id, previous.Id);
            var validationMet = previous is null || Meets(previousStat, ring);
            var stat = snapshot.Stat(v.Id, ring.Id);
            var eligible = !v.Suspended && (v.ApprovedAt is not null || (dateReached && validationMet));

            RingState state;
            if (v.Suspended) state = RingState.Suspended;
            else if (eligible)
            {
                var next = i + 1 < rings.Count && rings[i + 1].RequireValidation ? rings[i + 1] : null;
                var validated = next is not null ? Meets(stat, next) : stat.Successes > 0 && stat.Failures == 0;
                state = validated ? RingState.Validated : RingState.Allowed;
            }
            else state = dateReached ? RingState.AwaitingValidation : RingState.Waiting;

            statuses.Add(new RingStatus(ring, state, availableAt, eligible, stat, previous, previousStat, dateReached, validationMet));
        }
        return new VersionPlan { Version = v, Rings = statuses };
    }

    /// <summary>Seuils de validation exigés par <paramref name="requiring"/> (appliqués aux installations de l'anneau précédent).</summary>
    public static bool Meets(RingStat stat, RingRecord requiring) =>
        stat.Successes >= requiring.MinSuccesses && stat.Failures <= requiring.MaxFailures;

    /// <summary>Prochain changement d'état dû au temps (fin d'un délai), null si aucun : sert à invalider les politiques en cache.</summary>
    public static DateTimeOffset? NextChange(IEnumerable<VersionPlan> plans, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var p in plans)
        {
            if (p.Version.Suspended || p.Version.ApprovedAt is not null) continue;
            foreach (var r in p.Rings)
                if (r.AvailableAt > now && (next is null || r.AvailableAt < next)) next = r.AvailableAt;
        }
        return next;
    }

    // ------------------------------------------------------------------ libellés (console, motifs des règles)

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>« 08/12/2026 à 14:05 » dans le fuseau de la console.</summary>
    public static string FormatDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("dd/MM/yyyy 'à' HH:mm", Fr);

    public static string Plural(int n, string one, string many) => $"{n} {(n > 1 ? many : one)}";

    /// <summary>Libellé de l'état d'une version dans un anneau (console).</summary>
    public static string Label(RingStatus s, RolloutVersion v, TimeZoneInfo zone) => s.State switch
    {
        RingState.Suspended => "suspendu",
        RingState.Waiting => $"en attente jusqu'au {FormatDate(s.AvailableAt, zone)}" +
                             (s.ValidationRing is not null && !s.ValidationMet ? $", puis validation dans « {s.ValidationRing.Name} »" : ""),
        RingState.AwaitingValidation => $"en attente de validation dans « {s.ValidationRing!.Name} » " +
                                        $"({s.ValidationStat.Successes}/{s.Ring.MinSuccesses} réussie(s), {Plural(s.ValidationStat.Failures, "échec", "échecs")})",
        RingState.Validated => "validé",
        _ => v.ApprovedAt is not null && !(s.DateReached && s.ValidationMet) ? "autorisé (diffusion forcée)" : "autorisé",
    };
}
