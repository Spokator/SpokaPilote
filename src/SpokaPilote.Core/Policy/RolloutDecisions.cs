using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Policy;

/// <summary>
/// Décision de la console 4.0 pour une proposition (POST /api/rollout/decide) : « allowed », « held » (version en cours de
/// diffusion, pas encore autorisée pour l'anneau du poste ; date de disponibilité si elle ne dépend que du délai),
/// « suspended » (suspendue sur tout le parc) ou « unknown » (version jamais vue par la console : retenue).
/// </summary>
public sealed class RolloutDecision
{
    public const string Allowed = "allowed";
    public const string Held = "held";
    public const string Suspended = "suspended";
    public const string Unknown = "unknown";

    public string Status { get; set; } = Allowed;
    public string? Reason { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }
    /// <summary>Date de la décision (cache local).</summary>
    public DateTime DecidedAt { get; set; }

    [JsonIgnore] public bool IsHold => Status is Held or Suspended or Unknown;
}

/// <summary>
/// Décisions de diffusion des propositions d'une analyse, reçues de la console (ou, console injoignable, lues dans le
/// cache local : dernière décision connue de chaque proposition). Une proposition sans décision est retenue si l'anneau du
/// poste retient les nouveautés (règle prudente : jamais « tout autoriser » faute de réponse).
/// </summary>
public sealed class RolloutDecisions
{
    private readonly Dictionary<string, RolloutDecision> _byKey;

    public RolloutDecisions(Dictionary<string, RolloutDecision> byKey, bool fromCache, DateTime? cachedAt = null)
    {
        _byKey = byKey;
        FromCache = fromCache;
        CachedAt = cachedAt;
    }

    /// <summary>Décisions lues dans le cache local (console injoignable).</summary>
    public bool FromCache { get; }
    public DateTime? CachedAt { get; }
    public int Count => _byKey.Count;

    /// <summary>Identité d'une proposition : source, titre (sans tenir compte de la casse), version numérique (comme la console).</summary>
    public static string Key(DriverCandidate c) =>
        $"{c.Source}|{c.Title.Trim().ToUpperInvariant()}|{(DriverVersion.Parse(c.Version) is { } v ? v.ToString(4) : c.Version?.Trim())}";

    public RolloutDecision? For(DriverCandidate c) => _byKey.GetValueOrDefault(Key(c));

    public IReadOnlyDictionary<string, RolloutDecision> All => _byKey;

    // ------------------------------------------------------------------ cache local

    private sealed class CacheFile
    {
        public string Origin { get; set; } = "";
        public Dictionary<string, RolloutDecision> Decisions { get; set; } = new();
    }

    /// <summary>Entrées gardées au plus, et ancienneté maximale d'une décision gardée.</summary>
    internal const int MaxCacheEntries = 5000;
    internal static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(180);

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Ajoute les décisions reçues au cache (les plus récentes gardées, borné en nombre et en âge). Ne lève pas d'exception.</summary>
    public static void Save(string path, string origin, IReadOnlyDictionary<string, RolloutDecision> received, DateTime now)
    {
        try
        {
            var cache = Read(path, origin) ?? new CacheFile { Origin = origin };
            foreach (var (k, d) in received) cache.Decisions[k] = d;
            cache.Decisions = cache.Decisions
                .Where(kv => now - kv.Value.DecidedAt <= MaxCacheAge)
                .OrderByDescending(kv => kv.Value.DecidedAt)
                .Take(MaxCacheEntries)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, CacheJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Impossible d'enregistrer le cache des décisions de diffusion ({path}) : {ex.Message}");
        }
    }

    /// <summary>Dernières décisions connues pour ces propositions (cache de la même console), null si aucun cache.</summary>
    public static RolloutDecisions? FromCacheFile(string path, string origin, IEnumerable<DriverCandidate> candidates)
    {
        var cache = Read(path, origin);
        if (cache is null) return null;
        var found = new Dictionary<string, RolloutDecision>(StringComparer.Ordinal);
        foreach (var c in candidates)
            if (cache.Decisions.TryGetValue(Key(c), out var d)) found[Key(c)] = d;
        DateTime? at = cache.Decisions.Count == 0 ? null : cache.Decisions.Values.Max(d => d.DecidedAt);
        return new RolloutDecisions(found, fromCache: true, at);
    }

    private static CacheFile? Read(string path, string origin)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), CacheJson);
            // Décisions d'une autre console (ServerUrl modifié) : ignorées.
            return cache is not null && string.Equals(cache.Origin, origin, StringComparison.OrdinalIgnoreCase) ? cache : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug($"Cache des décisions de diffusion illisible : {ex.Message}");
            return null;
        }
    }
}
