using System.Globalization;

namespace SpokaPilote.Server.Jobs;

/// <summary>Fréquences d'une planification.</summary>
public static class ScheduleFrequencies
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

/// <summary>Planification enregistrée : une action de la liste blanche, lancée à heure fixe sur un groupe ou tout le parc.</summary>
/// <param name="GroupId">Groupe visé (null = tous les postes).</param>
/// <param name="Days">Jours de la semaine (planification hebdomadaire ; ignoré pour une planification quotidienne).</param>
/// <param name="Time">Heure locale du serveur (fuseau du conteneur, variable TZ).</param>
/// <param name="WindowMinutes">Fenêtre de maintenance : une tâche non récupérée par le poste dans ce délai expire (null = pas de fenêtre).</param>
public sealed record ScheduleRecord(
    long Id,
    string Name,
    string JobType,
    bool DryRun,
    long? GroupId,
    string Frequency,
    IReadOnlyList<DayOfWeek> Days,
    TimeOnly Time,
    int? WindowMinutes,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextRun,
    DateTimeOffset? LastRun,
    string? LastResult);

/// <summary>Demande de création ou de modification d'une planification (console).</summary>
public sealed class ScheduleRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool DryRun { get; set; }
    /// <summary>Groupe visé ; null ou absent = tous les postes.</summary>
    public long? GroupId { get; set; }
    public string? Frequency { get; set; }
    /// <summary>Jours ISO : 1 = lundi … 7 = dimanche.</summary>
    public List<int>? Days { get; set; }
    /// <summary>« HH:mm », heure du serveur.</summary>
    public string? Time { get; set; }
    public int? WindowMinutes { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ScheduleValidationException(string message) : Exception(message);

/// <summary>Calcul des échéances (sans état, testable seul), dans le fuseau horaire du serveur.</summary>
public static class ScheduleCalculator
{
    public const int MinWindowMinutes = 15;
    public const int MaxWindowMinutes = 24 * 60;

    /// <summary>
    /// Prochaine exécution strictement après <paramref name="after"/>. Changements d'heure : une heure qui n'existe pas
    /// (passage à l'heure d'été, ex. 02:30 le dernier dimanche de mars) est reportée à la première minute qui existe
    /// (03:00) ; une heure qui existe deux fois (retour à l'heure d'hiver) ne déclenche qu'une exécution, à la première.
    /// </summary>
    public static DateTimeOffset NextRun(string frequency, IReadOnlyCollection<DayOfWeek> days, TimeOnly time, DateTimeOffset after, TimeZoneInfo zone)
    {
        var localAfter = TimeZoneInfo.ConvertTime(after, zone);
        var date = DateOnly.FromDateTime(localAfter.DateTime);
        for (var i = 0; i <= 8; i++)
        {
            var day = date.AddDays(i);
            if (frequency == ScheduleFrequencies.Weekly && !days.Contains(day.DayOfWeek)) continue;
            var candidate = ToInstant(day.ToDateTime(time), zone);
            if (candidate > after) return candidate;
        }
        throw new InvalidOperationException("Planification sans jour d'exécution.");
    }

    public static DateTimeOffset NextRun(ScheduleRecord s, DateTimeOffset after, TimeZoneInfo zone) =>
        NextRun(s.Frequency, s.Days, s.Time, after, zone);

    /// <summary>Instant (UTC) d'une heure locale, avec les règles de changement d'heure décrites ci-dessus.</summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 24 * 60; guard++)
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Jours ISO (1 = lundi … 7 = dimanche) → DayOfWeek.</summary>
    public static DayOfWeek FromIso(int day) => (DayOfWeek)(day % 7);
    public static int ToIso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static readonly string[] DayNames = ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];

    /// <summary>« Chaque jour à 22:00 », « Chaque lundi et jeudi à 21:30 ».</summary>
    public static string Describe(string frequency, IReadOnlyCollection<DayOfWeek> days, TimeOnly time)
    {
        var at = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (frequency == ScheduleFrequencies.Daily) return $"Chaque jour à {at}";
        var names = days.OrderBy(ToIso).Select(d => DayNames[(int)d]).ToList();
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " et " + names[^1];
        return $"Chaque {list} à {at}";
    }

    /// <summary>Vérifie une demande de la console ; lève ScheduleValidationException (message en français).</summary>
    public static (string Name, JobType Type, string Frequency, List<DayOfWeek> Days, TimeOnly Time, int? Window) Validate(ScheduleRequest? req)
    {
        if (req is null) throw new ScheduleValidationException("Planification vide.");
        var name = new string((req.Name ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
        if (name.Length is 0 or > 80) throw new ScheduleValidationException("Nom obligatoire (80 caractères au plus).");
        var type = JobTypes.Find(req.Type)
                   ?? throw new ScheduleValidationException($"Action inconnue : « {req.Type} ». Actions autorisées : {string.Join(", ", JobTypes.All.Select(t => t.Id))}.");
        if (req.DryRun && !type.SupportsDryRun) throw new ScheduleValidationException($"« {type.Label} » ne propose pas de simulation.");
        var frequency = (req.Frequency ?? "").Trim().ToLowerInvariant();
        if (frequency is not (ScheduleFrequencies.Daily or ScheduleFrequencies.Weekly))
            throw new ScheduleValidationException("Fréquence inconnue : « daily » (chaque jour) ou « weekly » (certains jours de la semaine).");
        var days = new List<DayOfWeek>();
        if (frequency == ScheduleFrequencies.Weekly)
        {
            if (req.Days is not { Count: > 0 } || req.Days.Any(d => d is < 1 or > 7))
                throw new ScheduleValidationException("Choisissez au moins un jour de la semaine (1 = lundi … 7 = dimanche).");
            days = req.Days.Distinct().Order().Select(FromIso).ToList();
        }
        if (!TryParseTime(req.Time, out var time)) throw new ScheduleValidationException("Heure invalide (format HH:mm, ex. 22:00).");
        time = new TimeOnly(time.Hour, time.Minute);
        if (req.WindowMinutes is { } w && (w < MinWindowMinutes || w > MaxWindowMinutes))
            throw new ScheduleValidationException($"Fenêtre de maintenance : de {MinWindowMinutes} minutes à 24 heures.");
        return (name, type, frequency, days, time, req.WindowMinutes);
    }
}
