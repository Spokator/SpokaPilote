using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Policy;

/// <summary>Politique effective d'une analyse, avec son origine (pour l'affichage et les rapports).</summary>
public sealed class LoadedPolicy
{
    /// <summary>Politique appliquée : organisation + liste de référence.</summary>
    public required DriverPolicy Effective { get; init; }
    public DriverPolicy? Organization { get; init; }
    /// <summary>Chemin ou URL de la politique de l'organisation (null = aucune configurée).</summary>
    public string? OrganizationOrigin { get; init; }
    /// <summary>La politique de l'organisation vient du cache local (source injoignable ou invalide).</summary>
    public bool FromCache { get; init; }
    public DateTime? CachedAt { get; init; }
    public DriverPolicy? Reference { get; init; }
    /// <summary>« intégrée » ou chemin du cache rafraîchi depuis GitHub.</summary>
    public string ReferenceOrigin { get; init; } = "intégrée";
    /// <summary>Avertissements à afficher (source injoignable, politique invalide, cache utilisé...).</summary>
    public List<string> Messages { get; init; } = new();

    public static LoadedPolicy None { get; } = new() { Effective = DriverPolicy.Combine(null, null) };

    public string Summary => Organization is null
        ? $"aucune politique d'organisation · liste de référence : {ReferenceRuleCount} règle(s)"
        : $"{Organization.DisplayName} ({Organization.Rules.Count} règle(s){(FromCache ? ", depuis le cache" : "")}) · liste de référence : {ReferenceRuleCount} règle(s)";

    public int ReferenceRuleCount => Organization?.Settings.UseReferenceBlocklist == false ? 0 : Reference?.Rules.Count ?? 0;
}

/// <summary>
/// Charge la politique de pilotes : fichier local/UNC ou URL HTTPS (<see cref="Settings.PolicySource"/>), sinon
/// la console centrale ({ServerUrl}/api/policy, jeton Bearer). La dernière politique valide est gardée en cache
/// (%ProgramData%\SpokaPilote\policy-cache.json) pour fonctionner hors ligne. Une politique injoignable ou invalide
/// ne fait jamais échouer l'analyse : message clair + politique précédente (ou aucune).
/// </summary>
public sealed class PolicyLoader
{
    /// <summary>
    /// Liste de référence publiée sur le dépôt GitHub du projet. Simple fichier de métadonnées (aucun pilote n'est
    /// téléchargé depuis ce domaine) : il n'apparaît donc volontairement pas dans les domaines de <see cref="TrustPolicy"/>.
    /// </summary>
    public const string ReferenceListUrl = "https://raw.githubusercontent.com/Spokator/SpokaPilote/main/src/SpokaPilote.Core/Policy/known-bad-drivers.json";
    private const string EmbeddedResource = "SpokaPilote.Core.Policy.known-bad-drivers.json";
    private const int MaxPolicyBytes = 2 * 1024 * 1024;

    private readonly Settings _settings;
    private readonly HttpClient _http;

    public PolicyLoader(Settings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? Http.Client;
    }

    /// <summary>Remplace la source configurée pour cette exécution (option --policy). La politique n'est alors pas mise en cache.</summary>
    public string? SourceOverride { get; init; }
    public bool UseCache { get; init; } = true;
    /// <summary>Rafraîchit la liste de référence depuis GitHub (au plus une fois par <see cref="ReferenceRefreshInterval"/>).</summary>
    public bool RefreshReferenceList { get; init; } = true;
    public TimeSpan ReferenceRefreshInterval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public string CacheFile { get; init; } = AppPaths.PolicyCache;
    public string ReferenceCacheFile { get; init; } = Path.Combine(AppPaths.Cache, "known-bad-drivers.json");

    /// <summary>Source effective de la politique de l'organisation (null = aucune).</summary>
    public string? ResolveSource()
    {
        var source = SourceOverride ?? _settings.PolicySource;
        if (!string.IsNullOrWhiteSpace(source)) return Environment.ExpandEnvironmentVariables(source.Trim());
        if (!string.IsNullOrWhiteSpace(_settings.ServerUrl)) return _settings.ServerUrl.Trim().TrimEnd('/') + "/api/policy";
        return null;
    }

    /// <summary>Ne lève jamais d'exception (hors annulation).</summary>
    public async Task<LoadedPolicy> LoadAsync(CancellationToken ct = default)
    {
        var messages = new List<string>();
        var (reference, referenceOrigin) = await LoadReferenceAsync(messages, ct).ConfigureAwait(false);

        DriverPolicy? organization = null;
        var fromCache = false;
        DateTime? cachedAt = null;
        var source = ResolveSource();
        if (source is not null)
        {
            string? problem;
            try
            {
                var (json, noPolicy) = await FetchAsync(source, ct).ConfigureAwait(false);
                if (noPolicy)
                {
                    // La console répond explicitement « pas de politique » : on oublie l'ancienne.
                    if (UseCache) TryDelete(CacheFile);
                    Log.Info($"Politique de pilotes : aucune politique publiée par {source}.");
                    return Build(null, null, false, null, reference, referenceOrigin, messages);
                }
                var parsed = DriverPolicy.Parse(json!);
                if (parsed.IsValid)
                {
                    organization = parsed.Policy;
                    foreach (var w in parsed.Warnings) Log.Debug($"Politique {source} : {w}");
                    if (UseCache) SaveCache(source, json!);
                    Log.Info($"Politique de pilotes {organization!.DisplayName} chargée depuis {source} ({organization.Rules.Count} règle(s)).");
                    return Build(organization, source, false, null, reference, referenceOrigin, messages);
                }
                problem = $"politique invalide ({string.Join(" ; ", parsed.Errors.Take(3))}{(parsed.Errors.Count > 3 ? $" ; +{parsed.Errors.Count - 3} autre(s)" : "")})";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                problem = $"source injoignable ({ex.Message})";
            }

            // Repli : dernière politique valide connue.
            var cached = UseCache ? ReadCache() : null;
            if (cached is not null)
            {
                organization = cached.Value.Policy;
                fromCache = true;
                cachedAt = cached.Value.SavedAt;
                messages.Add($"Politique de pilotes {source} : {problem}. Dernière politique valide conservée : {organization.DisplayName} (cache du {cachedAt:dd/MM/yyyy HH:mm}).");
            }
            else
            {
                messages.Add($"Politique de pilotes {source} : {problem}. Aucune politique précédente en cache : seule la liste de référence s'applique.");
            }
            // Journalisé ; l'affichage passe par LoadedPolicy.Messages (évite les doublons à l'écran).
            Log.Info(messages[^1]);
        }
        return Build(organization, source, fromCache, cachedAt, reference, referenceOrigin, messages);
    }

    private static LoadedPolicy Build(DriverPolicy? org, string? origin, bool fromCache, DateTime? cachedAt,
        DriverPolicy? reference, string referenceOrigin, List<string> messages) => new()
    {
        Effective = DriverPolicy.Combine(org, reference),
        Organization = org,
        OrganizationOrigin = origin,
        FromCache = fromCache,
        CachedAt = cachedAt,
        Reference = reference,
        ReferenceOrigin = referenceOrigin,
        Messages = messages,
    };

    // ------------------------------------------------------------------ récupération

    /// <summary>Lit la politique (fichier, partage ou URL). noPolicy = la console a répondu 204.</summary>
    public async Task<(string? Json, bool NoPolicy)> FetchAsync(string source, CancellationToken ct)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!IsAllowedPolicyUrl(uri))
                throw new InvalidOperationException("HTTPS obligatoire (http n'est accepté que pour localhost / 127.0.0.1)");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (ShouldSendToken(uri))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ServerToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return (null, true);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"accès refusé (HTTP {(int)response.StatusCode}) : vérifiez le jeton ServerToken");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            if (response.Content.Headers.ContentLength > MaxPolicyBytes)
                throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (text.Length > MaxPolicyBytes) throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            return (text, false);
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : source;
        // Un partage réseau injoignable peut bloquer longtemps : lecture bornée dans le temps.
        var read = Task.Run(() =>
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"fichier introuvable : {path}");
            if (new FileInfo(path).Length > MaxPolicyBytes) throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            return File.ReadAllText(path);
        }, ct);
        var json = await read.WaitAsync(Timeout, ct).ConfigureAwait(false);
        return (json, false);
    }

    public static bool IsAllowedPolicyUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Le jeton de la console n'est envoyé qu'à la console elle-même (même hôte que ServerUrl).</summary>
    private bool ShouldSendToken(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerToken) || string.IsNullOrWhiteSpace(_settings.ServerUrl)) return false;
        return Uri.TryCreate(_settings.ServerUrl.Trim(), UriKind.Absolute, out var server) &&
               server.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && server.Port == uri.Port &&
               server.Scheme == uri.Scheme;
    }

    // ------------------------------------------------------------------ cache

    private sealed class CacheEntry
    {
        public string Origin { get; set; } = "";
        public DateTime SavedAt { get; set; }
        /// <summary>Contenu brut du fichier de politique (commentaires compris).</summary>
        public string Content { get; set; } = "";
    }

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void SaveCache(string origin, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheEntry { Origin = origin, SavedAt = DateTime.Now, Content = json }, CacheJson));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer le cache de la politique ({CacheFile}) : {ex.Message}");
        }
    }

    private (DriverPolicy Policy, DateTime SavedAt)? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(CacheFile), CacheJson);
            if (entry is null) return null;
            var parsed = DriverPolicy.Parse(entry.Content);
            return parsed.IsValid ? (parsed.Policy!, entry.SavedAt) : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"Cache de politique illisible : {ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* sans gravité */ }
    }

    // ------------------------------------------------------------------ liste de référence

    /// <summary>Liste noire de référence intégrée à SpokaPilote (ressource embarquée).</summary>
    public static PolicyParseResult LoadEmbeddedReference()
    {
        using var stream = typeof(PolicyLoader).Assembly.GetManifestResourceStream(EmbeddedResource);
        if (stream is null)
        {
            var r = new PolicyParseResult();
            r.Errors.Add(new PolicyIssue("Liste de référence intégrée absente."));
            return r;
        }
        using var reader = new StreamReader(stream);
        return DriverPolicy.Parse(reader.ReadToEnd(), PolicyLayer.Reference);
    }

    private async Task<(DriverPolicy? Policy, string Origin)> LoadReferenceAsync(List<string> messages, CancellationToken ct)
    {
        var embedded = LoadEmbeddedReference();
        if (!embedded.IsValid) Log.Warn($"Liste de référence intégrée invalide : {string.Join(" ; ", embedded.Errors)}");
        var best = embedded.IsValid ? embedded.Policy : null;
        var origin = "intégrée";

        if (RefreshReferenceList) await RefreshReferenceAsync(ct).ConfigureAwait(false);

        // Copie rafraîchie depuis GitHub : retenue seulement si elle est au moins aussi récente que la liste intégrée.
        try
        {
            if (File.Exists(ReferenceCacheFile))
            {
                var cached = DriverPolicy.Parse(await File.ReadAllTextAsync(ReferenceCacheFile, ct).ConfigureAwait(false), PolicyLayer.Reference);
                if (cached.IsValid && IsNewerOrSame(cached.Policy!, best))
                {
                    best = cached.Policy;
                    origin = $"{ReferenceListUrl} (copie du {File.GetLastWriteTime(ReferenceCacheFile):dd/MM/yyyy})";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"Copie locale de la liste de référence illisible : {ex.Message}");
        }
        return (best, origin);
    }

    private static bool IsNewerOrSame(DriverPolicy candidate, DriverPolicy? current)
    {
        if (current is null) return true;
        var a = DriverPolicy.ParseDate(candidate.UpdatedAt);
        var b = DriverPolicy.ParseDate(current.UpdatedAt);
        return a is not null && (b is null || a >= b);
    }

    private async Task RefreshReferenceAsync(CancellationToken ct)
    {
        var marker = ReferenceCacheFile + ".lastcheck";
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < ReferenceRefreshInterval) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ReferenceCacheFile)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync(ReferenceListUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"Liste de référence en ligne indisponible (HTTP {(int)response.StatusCode}) : la liste intégrée est utilisée.");
                return;
            }
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var parsed = DriverPolicy.Parse(json, PolicyLayer.Reference);
            if (!parsed.IsValid)
            {
                Log.Warn($"Liste de référence en ligne invalide, ignorée : {string.Join(" ; ", parsed.Errors.Take(3))}");
                return;
            }
            File.WriteAllText(ReferenceCacheFile, json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"Rafraîchissement de la liste de référence impossible : {ex.Message}");
        }
    }
}
