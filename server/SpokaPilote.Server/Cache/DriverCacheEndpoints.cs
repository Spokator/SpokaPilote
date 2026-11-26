using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Server.Api;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Cache;

/// <summary>
/// API du cache de pilotes et de catalogues (4.0). Les postes (jeton d'enrôlement) demandent un fichier officiel par son
/// adresse ; la console ne télécharge que depuis les domaines officiels acceptés par le client, une seule fois, et le
/// sert ensuite depuis /data/cache. Les catalogues (Microsoft Update Catalog, Dell, HP, Lenovo, NVIDIA) sont relayés et
/// gardés quelques heures. Le poste revérifie tout comme pour un téléchargement direct : la console n'est qu'un transport.
/// </summary>
public static partial class DriverCacheEndpoints
{
    /// <summary>Possibilité annoncée par /api/health.</summary>
    public const string Capability = "driver-cache";
    /// <summary>État de la réponse : hit, miss, wait (téléchargement en cours rejoint), stale, bypass, refused, error, disabled, throttled.</summary>
    public const string CacheHeader = "X-SpokaPilote-Cache";
    /// <summary>Statut renvoyé par la source officielle (404 : absent de la source, à distinguer d'une route inconnue).</summary>
    public const string OriginStatusHeader = "X-SpokaPilote-Origin-Status";
    /// <summary>Date (ISO 8601) à laquelle la console a obtenu ce catalogue de la source.</summary>
    public const string FetchedAtHeader = "X-SpokaPilote-Fetched-At";
    public const string Sha256Header = "X-SpokaPilote-Sha256";

    private const string MicrosoftCatalogBase = "https://" + OfficialHosts.MicrosoftCatalogHost;

    public static IServiceCollection AddSpokaPiloteDriverCache(this IServiceCollection services)
    {
        services.AddSingleton<DriverCacheSettingsService>();
        services.AddSingleton<DriverCacheOrigin>();
        services.AddSingleton(sp => new DriverCacheStore(sp.GetRequiredService<ServerOptions>(), sp.GetRequiredService<DriverCacheSettingsService>(),
            sp.GetRequiredService<DriverCacheOrigin>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DriverCacheStore>>()));
        services.AddSingleton<DriverCachePrefetch>();
        return services;
    }

    public static void MapDriverCacheApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Résumé sans adresse ni nom de fichier (lisible par les postes : « spokapilote server status »).
        api.MapGet("/cache/status", (DriverCacheStore cache) => Results.Ok(StatusView(cache.Stats(), cache.Settings)))
            .RequireAccess(Access.Agent | Access.View);

        var agent = api.MapGroup("/cache").RequireAccess(Access.Agent | Access.Admin);
        agent.MapGet("/file", FileAsync);
        agent.MapGet("/catalog", (HttpContext http, string? url, DriverCacheStore cache, CancellationToken ct) =>
        {
            if (Disabled(http, cache, catalogs: true) is { } off) return Task.FromResult(off);
            if (!TryParseOfficial(url, out var uri, out var error)) return Task.FromResult(Refuse(http, cache, error));
            if (uri.Host.EndsWith("catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Bad("Microsoft Update Catalog : utilisez /api/cache/catalog/mscatalog-search ou mscatalog-download."));
            return CatalogAsync(http, cache, new DriverCacheStore.CatalogRequest("catalog:" + DriverCacheStore.Normalize(uri), uri,
                u => new HttpRequestMessage(HttpMethod.Get, u)), ct);
        });
        agent.MapGet("/catalog/mscatalog-search", (HttpContext http, string? q, DriverCacheStore cache, CancellationToken ct) =>
        {
            if (Disabled(http, cache, catalogs: true) is { } off) return Task.FromResult(off);
            var query = Spaces().Replace(q ?? "", " ").Trim();
            if (query.Length is 0 or > 256 || query.Any(char.IsControl)) return Task.FromResult(Bad("Recherche invalide (1 à 256 caractères)."));
            var uri = new Uri($"{MicrosoftCatalogBase}/Search.aspx?q={Uri.EscapeDataString(query)}");
            return CatalogAsync(http, cache, new DriverCacheStore.CatalogRequest("mscatalog-search:" + query.ToLowerInvariant(), uri,
                u => new HttpRequestMessage(HttpMethod.Get, u), ClassifySearch, MicrosoftCatalog: true), ct);
        });
        agent.MapGet("/catalog/mscatalog-download", (HttpContext http, string? updateId, DriverCacheStore cache, CancellationToken ct) =>
        {
            if (Disabled(http, cache, catalogs: true) is { } off) return Task.FromResult(off);
            if (!Guid.TryParseExact(updateId ?? "", "D", out var id)) return Task.FromResult(Bad("Identifiant de mise à jour invalide."));
            var idText = id.ToString("D");
            var uri = new Uri($"{MicrosoftCatalogBase}/DownloadDialog.aspx");
            return CatalogAsync(http, cache, new DriverCacheStore.CatalogRequest("mscatalog-download:" + idText, uri, u =>
                {
                    // Même requête que le client (MicrosoftCatalogClient.GetDownloadUrlsAsync).
                    var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{idText}\",\"updateID\":\"{idText}\"}}]";
                    return new HttpRequestMessage(HttpMethod.Post, u)
                    {
                        Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("updateIDs", payload)]),
                    };
                }, ClassifyDownloadDialog, MicrosoftCatalog: true), ct);
        });

        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapGet("/settings/cache", (DriverCacheStore cache, DriverCacheSettingsService settings, DriverCachePrefetch prefetch) =>
            Results.Ok(AdminView(cache, settings, prefetch)));

        admin.MapPut("/settings/cache", async (HttpRequest request, DriverCacheStore cache, DriverCacheSettingsService settings,
            DriverCachePrefetch prefetch, TimeProvider clock, AuditService audit, ILogger<DriverCacheStore> log, CancellationToken ct) =>
        {
            var body = await BodyReader.ReadAsync(request, 64 * 1024, "Réglages", ct);
            if (body.Error is not null) return body.Error;
            DriverCacheSettings before = settings.Current(), after;
            try
            {
                var update = JsonSerializer.Deserialize<DriverCacheSettingsUpdate>(body.Text!, Storage.ServerStore.Json)
                             ?? throw new DriverCacheSettingsException("Réglages vides.");
                after = settings.Update(update, clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is JsonException or DriverCacheSettingsException)
            {
                return Results.Json(new ApiError(ex is JsonException ? $"Réglages illisibles : {ex.Message}" : ex.Message),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            cache.EnforceLimits();
            var detail = Describe(after);
            log.LogInformation("Réglages du cache de pilotes modifiés : {Detail}.", detail);
            audit.Record(request.HttpContext, AuditActions.SettingsCache, "Cache de pilotes", Changes(before, after) is { Length: > 0 } c ? c : detail);
            return Results.Ok(AdminView(cache, settings, prefetch));
        });

        admin.MapPost("/cache/purge", (HttpContext http, string? scope, DriverCacheStore cache, AuditService audit, ILogger<DriverCacheStore> log) =>
        {
            scope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
            if (scope is not ("all" or "files" or "catalogs")) return Bad("Portée inconnue : all, files ou catalogs.");
            var (entries, bytes) = cache.Purge(scope);
            var detail = string.Create(CultureInfo.InvariantCulture, $"{ScopeLabel(scope)} : {entries} entrée(s), {bytes / 1048576.0:0.0} Mo libérés");
            log.LogInformation("Cache de pilotes vidé — {Detail}.", detail);
            audit.Record(http, AuditActions.CachePurge, "Cache de pilotes", detail);
            return Results.Ok(new { scope, entries, bytes });
        });

        // Préchargement (parc sans accès Internet aux heures de bureau, sites isolés) : catalogues des modèles du parc et
        // paquets proposés dans les derniers rapports de leurs postes.
        admin.MapPost("/cache/prefetch", async (HttpRequest request, DriverCachePrefetch prefetch, DriverCacheStore cache, AuditService audit,
            CancellationToken ct) =>
        {
            if (!cache.Settings.Enabled) return Results.Json(new ApiError("Cache de pilotes désactivé."), statusCode: StatusCodes.Status409Conflict);
            var body = await BodyReader.ReadAsync(request, 64 * 1024, "Préchargement", ct);
            if (body.Error is not null) return body.Error;
            PrefetchRequest? req;
            try { req = JsonSerializer.Deserialize<PrefetchRequest>(string.IsNullOrWhiteSpace(body.Text) ? "{}" : body.Text!, Storage.ServerStore.Json); }
            catch (JsonException ex) { return Bad($"Demande illisible : {ex.Message}"); }
            req ??= new PrefetchRequest();
            var models = (req.Models ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
            if (models.Count == 0 && req.All != true) return Bad("Indiquez des modèles (« models ») ou « all »: true pour tous les modèles du parc.");
            var by = Principal.Of(request.HttpContext)!.Actor;
            if (!prefetch.TryStart(models, req.All == true, req.Packages != false, by))
                return Results.Json(new ApiError("Un préchargement est déjà en cours."), statusCode: StatusCodes.Status409Conflict);
            audit.Record(request.HttpContext, AuditActions.CachePrefetch, "Cache de pilotes",
                req.All == true ? "tous les modèles du parc" : string.Join(", ", models.Take(20)) + (models.Count > 20 ? $" (+{models.Count - 20})" : ""));
            return Results.Accepted("/api/settings/cache", new { running = true });
        });
    }

    public sealed class PrefetchRequest
    {
        public List<string>? Models { get; set; }
        public bool? All { get; set; }
        /// <summary>Précharger aussi les paquets proposés aux postes (défaut), pas seulement les catalogues.</summary>
        public bool? Packages { get; set; }
    }

    // ------------------------------------------------------------------ fichiers

    private static IResult FileAsync(HttpContext http, string? url, string? sha256, string? sha1, string? md5, long? size,
        DriverCacheStore cache)
    {
        if (Disabled(http, cache, catalogs: false) is { } off) return off;
        if (!TryParseOfficial(url, out var uri, out var error)) return Refuse(http, cache, error);
        if (!Hex(sha256, 64) || !Hex(sha1, 40) || !Hex(md5, 32)) return Bad("Empreinte invalide (sha256 : 64, sha1 : 40, md5 : 32 caractères hexadécimaux).");
        var expected = new ExpectedHashes(Empty(sha256), Empty(sha1), Empty(md5));
        var lookup = cache.BeginFile(uri, expected, size is > 0 ? size : null);
        switch (lookup.Kind)
        {
            case DriverCacheStore.LookupKind.Hit:
                http.Response.Headers[CacheHeader] = "hit";
                http.Response.Headers[Sha256Header] = lookup.Sha256;
                http.Response.OnCompleted(() =>
                {
                    cache.RecordServed(http.Response.ContentLength ?? lookup.Size, fromCache: true);
                    return Task.CompletedTask;
                });
                return TypedResults.PhysicalFile(lookup.BlobPath!, lookup.ContentType ?? "application/octet-stream",
                    entityTag: new EntityTagHeaderValue($"\"{lookup.Sha256}\""), enableRangeProcessing: true);
            case DriverCacheStore.LookupKind.Flight:
                return new FlightResult(cache, lookup.Flight!, lookup.CacheHeader!);
            default:
                if (lookup.Status == 403) return Refuse(http, cache, lookup.Message!);
                http.Response.Headers[CacheHeader] = lookup.CacheHeader;
                return Results.Json(new ApiError(lookup.Message!), statusCode: lookup.Status);
        }
    }

    private sealed class FlightResult(DriverCacheStore cache, DriverCacheStore.Flight flight, string header) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => cache.ServeFlightAsync(httpContext, flight, header, httpContext.RequestAborted);
    }

    // ------------------------------------------------------------------ catalogues

    private static async Task<IResult> CatalogAsync(HttpContext http, DriverCacheStore cache, DriverCacheStore.CatalogRequest request, CancellationToken ct)
    {
        var result = await cache.GetCatalogAsync(request, ct);
        http.Response.Headers[CacheHeader] = result.CacheState;
        if (result.OriginStatus is { } origin) http.Response.Headers[OriginStatusHeader] = origin.ToString(CultureInfo.InvariantCulture);
        if (result.FetchedAt is { } fetched) http.Response.Headers[FetchedAtHeader] = fetched.ToString("O", CultureInfo.InvariantCulture);
        if (result.Status == 200)
        {
            if (result.Path is not null) return TypedResults.PhysicalFile(result.Path, result.ContentType ?? "application/octet-stream");
            return Results.Bytes(result.Body ?? [], result.ContentType ?? "application/octet-stream");
        }
        if (result.Status == 403) return Refuse(http, cache, result.Error ?? "Adresse refusée.");
        if (result.RetryAfterSeconds is { } retry) http.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new ApiError(result.Error ?? "Catalogue indisponible."), statusCode: result.Status);
    }

    [GeneratedRegex(@"<tr id=""[0-9a-f-]{36}_R\d+""", RegexOptions.IgnoreCase)]
    private static partial Regex SearchRow();

    [GeneratedRegex(@"files\[\d+\]\.url\s*=\s*'", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrl();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// Page de recherche du Microsoft Update Catalog : « Too many search requests » → jamais en cache ; résultats ou
    /// « aucun résultat » explicite → en cache ; toute autre page (erreur, maintenance) → relayée sans être gardée.
    /// </summary>
    internal static DriverCacheStore.CatalogVerdict ClassifySearch(string html)
    {
        if (html.Contains("Too many search requests", StringComparison.OrdinalIgnoreCase)) return DriverCacheStore.CatalogVerdict.Throttled;
        if (SearchRow().IsMatch(html)) return DriverCacheStore.CatalogVerdict.Cacheable;
        return html.Contains("ctl00_catalogBody_noResults", StringComparison.OrdinalIgnoreCase) && html.Contains("Search tips", StringComparison.OrdinalIgnoreCase)
            ? DriverCacheStore.CatalogVerdict.Cacheable
            : DriverCacheStore.CatalogVerdict.PassThrough;
    }

    internal static DriverCacheStore.CatalogVerdict ClassifyDownloadDialog(string html)
    {
        if (html.Contains("Too many search requests", StringComparison.OrdinalIgnoreCase)) return DriverCacheStore.CatalogVerdict.Throttled;
        return DownloadUrl().IsMatch(html) ? DriverCacheStore.CatalogVerdict.Cacheable : DriverCacheStore.CatalogVerdict.PassThrough;
    }

    // ------------------------------------------------------------------ utilitaires

    private static IResult? Disabled(HttpContext http, DriverCacheStore cache, bool catalogs)
    {
        var s = cache.Settings;
        if (s.Enabled && (!catalogs || s.Catalogs)) return null;
        http.Response.Headers[CacheHeader] = "disabled";
        return Results.Json(new ApiError(s.Enabled ? "Relais des catalogues désactivé sur la console." : "Cache de pilotes désactivé sur la console."),
            statusCode: StatusCodes.Status404NotFound);
    }

    private static bool TryParseOfficial(string? url, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "Adresse absente ou invalide (paramètre url).";
            return false;
        }
        if (!OfficialHosts.IsOfficialMetadata(parsed))
        {
            error = parsed.Scheme != Uri.UriSchemeHttps
                ? "Seules les adresses HTTPS des sources officielles sont acceptées."
                : $"{parsed.Host} ne fait pas partie des sources officielles autorisées.";
            return false;
        }
        uri = parsed;
        return true;
    }

    private static IResult Refuse(HttpContext http, DriverCacheStore cache, string message)
    {
        cache.RecordRefused();
        http.Response.Headers[CacheHeader] = "refused";
        return Results.Json(new ApiError(message), statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult Bad(string message) => Results.Json(new ApiError(message), statusCode: StatusCodes.Status400BadRequest);

    private static bool Hex(string? value, int length) => string.IsNullOrEmpty(value) || (value.Length == length && value.All(Uri.IsHexDigit));
    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();

    private static string ScopeLabel(string scope) => scope switch { "files" => "paquets", "catalogs" => "catalogues", _ => "tout le cache" };

    internal static object StatusView(DriverCacheStats s, DriverCacheSettings settings) => new
    {
        enabled = s.Enabled,
        catalogs = s.Catalogs,
        sizeBytes = s.SizeBytes,
        maxBytes = s.MaxBytes,
        files = s.Files,
        catalogEntries = s.CatalogEntries,
        hits = s.Hits,
        misses = s.Misses,
        hitRatio = s.HitRatio is { } r ? Math.Round(r, 4) : (double?)null,
        bytesSaved = s.BytesServedFromCache,
        catalogTtlHours = settings.CatalogTtlHours,
        downloadsInProgress = s.DownloadsInProgress,
        diskGuardActive = s.DiskGuardActive,
    };

    private static object AdminView(DriverCacheStore cache, DriverCacheSettingsService service, DriverCachePrefetch prefetch)
    {
        var settings = service.Current();
        var stats = cache.Stats();
        return new
        {
            settings = new
            {
                enabled = settings.Enabled,
                catalogs = settings.Catalogs,
                maxSizeGb = settings.MaxSizeGb,
                catalogTtlHours = settings.CatalogTtlHours,
                fileMaxAgeDays = settings.FileMaxAgeDays,
                minFreeGb = settings.MinFreeGb,
            },
            locked = service.LockedFields(),
            variables = DriverCacheSettingsService.Variables.ToDictionary(v => v.Field, v => v.Variable),
            directory = cache.Root,
            maxConcurrentDownloads = cache.MaxConcurrentDownloads,
            stats = new
            {
                sizeBytes = stats.SizeBytes,
                maxBytes = stats.MaxBytes,
                files = stats.Files,
                catalogEntries = stats.CatalogEntries,
                blobs = stats.Blobs,
                fileHits = stats.FileHits,
                fileMisses = stats.FileMisses,
                catalogHits = stats.CatalogHits,
                catalogMisses = stats.CatalogMisses,
                hitRatio = stats.HitRatio is { } r ? Math.Round(r, 4) : (double?)null,
                bytesSaved = stats.BytesServedFromCache,
                bytesFromOrigin = stats.BytesFromOrigin,
                refused = stats.Refused,
                bypassed = stats.Bypassed,
                errors = stats.Errors,
                hashMismatches = stats.HashMismatches,
                downloadsInProgress = stats.DownloadsInProgress,
                freeBytes = stats.FreeBytes,
                volumeBytes = stats.VolumeBytes,
                diskGuardActive = stats.DiskGuardActive,
                microsoftCatalogPausedUntil = stats.MicrosoftCatalogPausedUntil,
            },
            top = cache.Top(10),
            prefetch = prefetch.Status,
        };
    }

    private static string Describe(DriverCacheSettings s) => string.Create(CultureInfo.InvariantCulture,
        $"{(s.Enabled ? "actif" : "désactivé")}, catalogues {(s.Catalogs ? "relayés" : "non relayés")}, {s.MaxSizeGb:0.#} Go max, " +
        $"catalogues {s.CatalogTtlHours} h, fichiers {s.FileMaxAgeDays} j, {s.MinFreeGb:0.#} Go libres au moins");

    private static string Changes(DriverCacheSettings a, DriverCacheSettings b)
    {
        var parts = new List<string>();
        void Add<T>(string label, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y)) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} : {x} → {y}"));
        }
        Add("actif", a.Enabled, b.Enabled);
        Add("catalogues", a.Catalogs, b.Catalogs);
        Add("taille max (Go)", a.MaxSizeGb, b.MaxSizeGb);
        Add("durée des catalogues (h)", a.CatalogTtlHours, b.CatalogTtlHours);
        Add("ancienneté des fichiers (j)", a.FileMaxAgeDays, b.FileMaxAgeDays);
        Add("espace libre min (Go)", a.MinFreeGb, b.MinFreeGb);
        return string.Join(" · ", parts);
    }

    internal static string Prometheus(DriverCacheStats s)
    {
        var sb = new StringBuilder();
        void M(string name, string help, string type, double value)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            sb.Append(name).Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
        M("spokapilote_cache_enabled", "Cache de pilotes actif.", "gauge", s.Enabled ? 1 : 0);
        M("spokapilote_cache_bytes", "Taille du cache de pilotes.", "gauge", s.SizeBytes);
        M("spokapilote_cache_max_bytes", "Taille maximale du cache de pilotes.", "gauge", s.MaxBytes);
        M("spokapilote_cache_entries", "Fichiers et catalogues en cache.", "gauge", s.Files + s.CatalogEntries);
        M("spokapilote_cache_hits_total", "Demandes servies sans retourner à la source.", "counter", s.Hits);
        M("spokapilote_cache_misses_total", "Demandes transmises à la source officielle.", "counter", s.Misses);
        M("spokapilote_cache_bytes_saved_total", "Octets servis depuis le cache (téléchargements Internet évités).", "counter", s.BytesServedFromCache);
        M("spokapilote_cache_origin_bytes_total", "Octets téléchargés depuis les sources officielles.", "counter", s.BytesFromOrigin);
        M("spokapilote_cache_bypassed_total", "Demandes renvoyées au téléchargement direct (cache plein, disque, limite).", "counter", s.Bypassed);
        M("spokapilote_cache_refused_total", "Adresses refusées (hors sources officielles).", "counter", s.Refused);
        M("spokapilote_cache_errors_total", "Échecs vers les sources officielles.", "counter", s.Errors);
        M("spokapilote_cache_hash_mismatch_total", "Fichiers refusés : empreinte différente de celle publiée.", "counter", s.HashMismatches);
        M("spokapilote_cache_downloads_in_progress", "Téléchargements en cours vers la source.", "gauge", s.DownloadsInProgress);
        M("spokapilote_cache_disk_guard", "Garde d'espace disque active (plus rien n'est mis en cache).", "gauge", s.DiskGuardActive ? 1 : 0);
        return sb.ToString();
    }
}
