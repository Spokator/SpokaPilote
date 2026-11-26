using System.Globalization;
using System.Text.Json;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Cache;

/// <summary>Réglages du cache de pilotes et de catalogues (page Paramètres de la console ; une variable d'environnement définie prime).</summary>
public sealed class DriverCacheSettings
{
    /// <summary>Cache actif : les postes peuvent demander à la console les fichiers officiels et les catalogues.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Catalogues et métadonnées (Microsoft Update Catalog, Dell, HP, Lenovo, NVIDIA) relayés et gardés <see cref="CatalogTtlHours"/> heures.</summary>
    public bool Catalogs { get; set; } = true;
    /// <summary>Taille maximale du cache (Go) ; au-delà, les fichiers les moins récemment demandés sont supprimés.</summary>
    public double MaxSizeGb { get; set; } = 20;
    /// <summary>Durée de conservation d'un catalogue avant de le redemander à la source officielle.</summary>
    public int CatalogTtlHours { get; set; } = 12;
    /// <summary>Un fichier téléchargé il y a plus de ce nombre de jours est retéléchargé à la demande suivante (l'empreinte de son contenu évite tout doublon).</summary>
    public int FileMaxAgeDays { get; set; } = 30;
    /// <summary>Espace libre minimal du volume (Go, et au moins 5 % du volume) : en dessous, plus aucun fichier n'est mis en cache.</summary>
    public double MinFreeGb { get; set; } = 10;

    public long MaxBytes => (long)(MaxSizeGb * 1024 * 1024 * 1024);
    public long MinFreeBytes => (long)(MinFreeGb * 1024 * 1024 * 1024);

    public DriverCacheSettings Clone() => (DriverCacheSettings)MemberwiseClone();
}

/// <summary>Modification envoyée par la console (null = inchangé).</summary>
public sealed class DriverCacheSettingsUpdate
{
    public bool? Enabled { get; set; }
    public bool? Catalogs { get; set; }
    public double? MaxSizeGb { get; set; }
    public int? CatalogTtlHours { get; set; }
    public int? FileMaxAgeDays { get; set; }
    public double? MinFreeGb { get; set; }
}

public sealed class DriverCacheSettingsException(string message) : Exception(message);

/// <summary>
/// Lecture et enregistrement des réglages du cache : document « driver-cache-settings » de la base, variables
/// d'environnement SPOKAPILOTE_CACHE_* prioritaires (champs verrouillés dans la console). Valeurs gardées en mémoire.
/// </summary>
public sealed class DriverCacheSettingsService(ServerStore store, IConfiguration config)
{
    public const string DocumentKey = "driver-cache-settings";

    /// <summary>Champ de la console → variable d'environnement.</summary>
    public static readonly (string Field, string Variable)[] Variables =
    [
        ("enabled", "SPOKAPILOTE_CACHE_ENABLED"),
        ("catalogs", "SPOKAPILOTE_CACHE_CATALOGS"),
        ("maxSizeGb", "SPOKAPILOTE_CACHE_MAX_GB"),
        ("catalogTtlHours", "SPOKAPILOTE_CACHE_CATALOG_HOURS"),
        ("fileMaxAgeDays", "SPOKAPILOTE_CACHE_FILE_DAYS"),
        ("minFreeGb", "SPOKAPILOTE_CACHE_MIN_FREE_GB"),
    ];

    private readonly Lock _lock = new();
    private DriverCacheSettings? _current;

    private string? Env(string name) => config[name] is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public HashSet<string> LockedFields() => Variables.Where(v => Env(v.Variable) is not null).Select(v => v.Field).ToHashSet();

    /// <summary>Réglages effectifs (base + variables d'environnement), mis en cache jusqu'à la prochaine modification.</summary>
    public DriverCacheSettings Current()
    {
        lock (_lock)
        {
            return (_current ??= Compute()).Clone();
        }
    }

    private DriverCacheSettings Compute()
    {
        var s = Stored();
        if (Bool(Env("SPOKAPILOTE_CACHE_ENABLED")) is { } enabled) s.Enabled = enabled;
        if (Bool(Env("SPOKAPILOTE_CACHE_CATALOGS")) is { } catalogs) s.Catalogs = catalogs;
        if (Double(Env("SPOKAPILOTE_CACHE_MAX_GB")) is { } max) s.MaxSizeGb = max;
        if (int.TryParse(Env("SPOKAPILOTE_CACHE_CATALOG_HOURS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttl)) s.CatalogTtlHours = ttl;
        if (int.TryParse(Env("SPOKAPILOTE_CACHE_FILE_DAYS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) s.FileMaxAgeDays = days;
        if (Double(Env("SPOKAPILOTE_CACHE_MIN_FREE_GB")) is { } free) s.MinFreeGb = free;
        Clamp(s);
        return s;
    }

    private DriverCacheSettings Stored()
    {
        var doc = store.GetDocument(DocumentKey);
        if (doc is null) return new DriverCacheSettings();
        try
        {
            return JsonSerializer.Deserialize<DriverCacheSettings>(doc.Json, ServerStore.Json) ?? new DriverCacheSettings();
        }
        catch (JsonException)
        {
            return new DriverCacheSettings();
        }
    }

    private static void Clamp(DriverCacheSettings s)
    {
        s.MaxSizeGb = Math.Clamp(double.IsFinite(s.MaxSizeGb) ? s.MaxSizeGb : 20, 0.01, 100_000);
        s.CatalogTtlHours = Math.Clamp(s.CatalogTtlHours, 1, 168);
        s.FileMaxAgeDays = Math.Clamp(s.FileMaxAgeDays, 1, 3650);
        s.MinFreeGb = Math.Clamp(double.IsFinite(s.MinFreeGb) ? s.MinFreeGb : 10, 0, 100_000);
    }

    /// <summary>Enregistre une modification (les champs verrouillés par variable d'environnement sont refusés).</summary>
    public DriverCacheSettings Update(DriverCacheSettingsUpdate update, DateTimeOffset now)
    {
        var locked = LockedFields();
        void Check(object? value, string field)
        {
            if (value is not null && locked.Contains(field))
                throw new DriverCacheSettingsException($"« {field} » est fixé par variable d'environnement sur le serveur : modifiable uniquement là.");
        }
        Check(update.Enabled, "enabled");
        Check(update.Catalogs, "catalogs");
        Check(update.MaxSizeGb, "maxSizeGb");
        Check(update.CatalogTtlHours, "catalogTtlHours");
        Check(update.FileMaxAgeDays, "fileMaxAgeDays");
        Check(update.MinFreeGb, "minFreeGb");
        if (update.MaxSizeGb is { } max && (!double.IsFinite(max) || max < 0.01 || max > 100_000))
            throw new DriverCacheSettingsException("Taille maximale : de 0,01 à 100 000 Go.");
        if (update.CatalogTtlHours is { } ttl && ttl is < 1 or > 168)
            throw new DriverCacheSettingsException("Durée de conservation des catalogues : de 1 à 168 heures.");
        if (update.FileMaxAgeDays is { } days && days is < 1 or > 3650)
            throw new DriverCacheSettingsException("Ancienneté maximale des fichiers : de 1 à 3 650 jours.");
        if (update.MinFreeGb is { } free && (!double.IsFinite(free) || free < 0 || free > 100_000))
            throw new DriverCacheSettingsException("Espace libre minimal : de 0 à 100 000 Go.");

        lock (_lock)
        {
            var s = Stored();
            if (update.Enabled is { } e) s.Enabled = e;
            if (update.Catalogs is { } c) s.Catalogs = c;
            if (update.MaxSizeGb is { } m) s.MaxSizeGb = m;
            if (update.CatalogTtlHours is { } t) s.CatalogTtlHours = t;
            if (update.FileMaxAgeDays is { } d) s.FileMaxAgeDays = d;
            if (update.MinFreeGb is { } f) s.MinFreeGb = f;
            Clamp(s);
            store.SetDocument(DocumentKey, JsonSerializer.Serialize(s, ServerStore.Json), now);
            _current = null;
        }
        return Current();
    }

    internal static bool? Bool(string? v) => v?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "oui" or "on" => true,
        "0" or "false" or "no" or "non" or "off" => false,
        _ => null,
    };

    private static double? Double(string? v) =>
        double.TryParse(v?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
}
