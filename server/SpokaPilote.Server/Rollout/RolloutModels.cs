namespace SpokaPilote.Server.Rollout;

/// <summary>
/// Anneau de déploiement : les postes de ses groupes ne reçoivent une nouvelle version de pilote que
/// <see cref="DelayDays"/> jours après sa première détection sur le parc, et (facultatif) après validation dans l'anneau
/// précédent (au moins <see cref="MinSuccesses"/> installations réussies, au plus <see cref="MaxFailures"/> échecs).
/// </summary>
public sealed record RingRecord(
    long Id,
    string Name,
    int Position,
    int DelayDays,
    bool RequireValidation,
    int MinSuccesses,
    int MaxFailures,
    string? Description,
    IReadOnlyList<long> GroupIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Disjoncteur : suspension automatique d'une version qui échoue trop (par anneau, sur une fenêtre glissante).</summary>
public sealed class BreakerSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Nombre minimal de postes en échec dans la fenêtre.</summary>
    public int MinFailures { get; set; } = 3;
    /// <summary>Part minimale de postes en échec parmi les postes qui ont tenté l'installation (en %).</summary>
    public int MinFailureRatePercent { get; set; } = 20;
    /// <summary>Fenêtre glissante, en heures.</summary>
    public int WindowHours { get; set; } = 48;
    /// <summary>Compter un périphérique passé en erreur après l'installation comme un échec (régression).</summary>
    public bool CountRegressions { get; set; } = true;
}

/// <summary>Réglages de la diffusion par anneaux (document « rollout-settings » de la base).</summary>
public sealed class RolloutSettings
{
    /// <summary>Diffusion par anneaux active (les suspensions s'appliquent même quand elle est désactivée).</summary>
    public bool Enabled { get; set; }
    /// <summary>Anneau des postes qui n'appartiennent à aucun groupe d'anneau (null = le dernier anneau).</summary>
    public long? DefaultRingId { get; set; }
    /// <summary>Soumettre aussi les pilotes manquants (périphérique sans pilote) aux délais des anneaux.</summary>
    public bool ApplyToMissingDrivers { get; set; }
    /// <summary>Une famille de pilotes plus vue sur le parc depuis ce nombre de jours n'est plus retenue (règles de famille).</summary>
    public int FamilyRetentionDays { get; set; } = 365;
    public BreakerSettings Breaker { get; set; } = new();

    public RolloutSettings Clone() => new()
    {
        Enabled = Enabled,
        DefaultRingId = DefaultRingId,
        ApplyToMissingDrivers = ApplyToMissingDrivers,
        FamilyRetentionDays = FamilyRetentionDays,
        Breaker = new BreakerSettings
        {
            Enabled = Breaker.Enabled,
            MinFailures = Breaker.MinFailures,
            MinFailureRatePercent = Breaker.MinFailureRatePercent,
            WindowHours = Breaker.WindowHours,
            CountRegressions = Breaker.CountRegressions,
        },
    };

    /// <summary>Contrôle les valeurs ; renvoie un message d'erreur en français, ou null.</summary>
    public string? Validate()
    {
        if (FamilyRetentionDays is < 30 or > 3650) return "Rétention des familles : 30 à 3650 jours.";
        if (Breaker.MinFailures is < 1 or > 10_000) return "Disjoncteur : nombre minimal d'échecs de 1 à 10 000.";
        if (Breaker.MinFailureRatePercent is < 1 or > 100) return "Disjoncteur : taux d'échec de 1 à 100 %.";
        if (Breaker.WindowHours is < 1 or > 24 * 90) return "Disjoncteur : fenêtre de 1 heure à 90 jours.";
        return null;
    }
}

/// <summary>
/// Version de pilote vue sur le parc : clé (source, titre, version) et date de première détection (réception par la
/// console du premier rapport qui la propose).
/// </summary>
public sealed class RolloutVersion
{
    public long Id { get; init; }
    /// <summary>Source normalisée (nom de source du poste : WindowsUpdate, Dell... ou libellé reçu).</summary>
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Famille : titre dont les numéros de version sont remplacés par « * » (voir <see cref="RolloutKeys.FamilyPattern"/>).</summary>
    public string Family { get; init; } = "";
    public string? Kind { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public string? FirstMachine { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    public bool Suspended { get; init; }
    public bool SuspendAuto { get; init; }
    public string? SuspendReason { get; init; }
    public DateTimeOffset? SuspendedAt { get; init; }
    public string? SuspendedBy { get; init; }
    /// <summary>Suspension examinée (« Maintenir le blocage ») : elle n'est plus signalée comme à examiner.</summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public DateTimeOffset? ResumedAt { get; init; }
    public string? ResumedBy { get; init; }
    /// <summary>« Diffuser partout maintenant » : version autorisée pour tous les anneaux, sans délai ni validation.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }

    public bool NeedsAttention => Suspended && SuspendAuto && AcknowledgedAt is null;
    public string DisplayName => RolloutKeys.DisplayName(Title, Version);
}

/// <summary>Installations d'une version dans un anneau (postes distincts, état actuel de chaque poste).</summary>
public readonly record struct RingStat(int Successes, int Failures, int Regressions)
{
    public static readonly RingStat Empty = new(0, 0, 0);
}

/// <summary>Événement de l'historique de la diffusion (suspension, reprise, approbation...).</summary>
public sealed record RolloutEvent(long Id, DateTimeOffset At, long? VersionId, string? Driver, string Action, string Actor, string? Detail);

/// <summary>Échec récent d'une version sur un poste (fiche d'une version).</summary>
public sealed record RolloutFailure(string Machine, long RingId, DateTimeOffset At, bool Regression, string? Message);

/// <summary>Compteurs de la fenêtre du disjoncteur pour un anneau (postes distincts).</summary>
public sealed record BreakerWindow(long RingId, int Attempts, int Failures);

/// <summary>Données de la diffusion lues en une fois (anneaux, versions, compteurs) pour calculer les états et les règles.</summary>
public sealed class RolloutSnapshot
{
    public required RolloutSettings Settings { get; init; }
    /// <summary>Anneaux, dans l'ordre (position croissante).</summary>
    public required IReadOnlyList<RingRecord> Rings { get; init; }
    public required IReadOnlyList<RolloutVersion> Versions { get; init; }
    public required IReadOnlyDictionary<(long Version, long Ring), RingStat> Stats { get; init; }
    public DateTimeOffset BuiltAt { get; init; }

    public bool RingsActive => Settings.Enabled && Rings.Count > 0;

    public RingStat Stat(long versionId, long ringId) => Stats.TryGetValue((versionId, ringId), out var s) ? s : RingStat.Empty;

    /// <summary>Anneau par défaut (postes sans groupe d'anneau) : celui des réglages s'il existe encore, sinon le dernier.</summary>
    public RingRecord? DefaultRing =>
        Rings.Count == 0 ? null : Rings.FirstOrDefault(r => r.Id == Settings.DefaultRingId) ?? Rings[^1];
}
