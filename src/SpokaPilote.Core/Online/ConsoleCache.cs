using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Online;

/// <summary>Réglage UseConsoleCache : « auto » (défaut), « on » ou « off ».</summary>
public enum ConsoleCacheMode { Auto, On, Off }

/// <summary>Réponse de la console à une demande de catalogue.</summary>
public enum ConsoleCatalogStatus { Ok, NotFoundAtSource, RateLimited }

/// <param name="FetchedAt">Date à laquelle la console a obtenu ce contenu de la source officielle.</param>
public sealed record ConsoleCatalogResult(ConsoleCatalogStatus Status, string? Text = null, DateTimeOffset? FetchedAt = null, TimeSpan? RetryAfter = null);

/// <summary>État du cache de la console (GET /api/cache/status), pour « spokapilote server status ».</summary>
public sealed record ConsoleCacheStatus(bool Enabled, bool Catalogs, long SizeBytes, long MaxBytes, int Files, double? HitRatio, long BytesSaved,
    bool DiskGuardActive);

/// <summary>
/// Cache de pilotes et de catalogues de la console centrale, côté poste : décide, pour chaque téléchargement officiel,
/// « par la console » ou « direct ». La console n'est qu'un TRANSPORT :
/// <list type="bullet">
/// <item>seules les adresses que le poste accepte lui-même (domaines officiels, <see cref="TrustPolicy"/>) lui sont demandées ;</item>
/// <item>un fichier reçu de la console n'est utilisé que si le poste peut le vérifier seul AVANT tout usage : empreinte
///   publiée par la source, ou à défaut signature Authenticode valide de l'éditeur attendu pour ce domaine (voir
///   <see cref="Downloader"/>) ; ensuite, les contrôles habituels s'appliquent sans changement (signature, éditeur, ID
///   matériel de l'INF) ;</item>
/// <item>métadonnées : seuls les catalogues .cab signés (Authenticode) par l'éditeur attendu de leur domaine (Dell, HP)
///   sont relayés ; tout ce qui n'est pas authentifiable par sa signature (XML Lenovo, réponses NVIDIA, liens et
///   empreintes du Microsoft Update Catalog) est lu directement à la source, car ces métadonnées désignent les
///   installeurs, leurs empreintes et leurs lignes de commande. Seule exception : la page de RECHERCHE du Microsoft
///   Update Catalog, simple indication (l'entrée retenue est ensuite résolue directement chez Microsoft) ;</item>
/// <item>console injoignable, en erreur, trop lente, ancienne (3.x) ou copie refusée : téléchargement direct depuis la
///   source officielle (journalisé), jamais d'échec d'installation à cause du cache.</item>
/// </list>
/// </summary>
public sealed class ConsoleCache
{
    private static readonly Lock StaticLock = new();
    private static ConsoleCache? _current;
    private static bool _resolved;
    /// <summary>Remplacement pour les tests (portée : le test et ce qu'il attend).</summary>
    private static readonly AsyncLocal<ConsoleCache?> TestOverride = new();
    private static readonly AsyncLocal<bool> TestDisabled = new();

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private DateTimeOffset _unavailableUntil = DateTimeOffset.MinValue;
    private string? _unavailableReason;
    private DateTimeOffset _statusCheckedAt = DateTimeOffset.MinValue;
    private (bool Enabled, bool Catalogs)? _consoleState;

    /// <summary>Attente maximale des en-têtes de la console (elle peut d'abord contacter la source officielle).</summary>
    internal TimeSpan HeadersTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Transfert arrêté plus longtemps que ce délai : abandon et téléchargement direct.</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    internal TimeSpan CatalogTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Après une panne (console injoignable, délai dépassé), la console n'est plus sollicitée pendant ce délai.</summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StatusRefresh = TimeSpan.FromMinutes(10);

    public Uri BaseUri { get; }
    public ConsoleCacheMode Mode { get; }
    /// <summary>Catalogues et métadonnées demandés aussi à la console (réglage ConsoleCacheCatalogs).</summary>
    public bool Catalogs { get; }

    internal ConsoleCache(Uri baseUri, string token, ConsoleCacheMode mode, bool catalogs, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        BaseUri = baseUri;
        _token = token;
        Mode = mode;
        Catalogs = catalogs;
        _clock = clock ?? TimeProvider.System;
        handler ??= new SocketsHttpHandler
        {
            // Comme ServerClient : jamais de redirection (le jeton ne part que vers l'adresse configurée), proxy système.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpokaPilote", UpdateChecker.CurrentVersion.ToString(3)));
    }

    // ------------------------------------------------------------------ configuration

    public static ConsoleCacheMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" or "non" or "false" or "0" or "désactivé" or "desactive" => ConsoleCacheMode.Off,
        "on" or "oui" or "true" or "1" or "activé" or "active" => ConsoleCacheMode.On,
        _ => ConsoleCacheMode.Auto,
    };

    public static bool IsValidMode(string? value) =>
        value?.Trim().ToLowerInvariant() is "auto" or "on" or "off";

    /// <summary>Cache à utiliser d'après les réglages, ou null (désactivé, pas de console ou pas de jeton, adresse invalide).</summary>
    public static ConsoleCache? From(Settings settings)
    {
        var mode = ParseMode(settings.UseConsoleCache);
        if (mode == ConsoleCacheMode.Off || string.IsNullOrWhiteSpace(settings.ServerUrl) || string.IsNullOrWhiteSpace(settings.ServerToken)) return null;
        try
        {
            var baseUri = ServerClient.ValidateUrl(settings.ServerUrl);
            var token = settings.ServerToken.Trim();
            if (token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
            return new ConsoleCache(baseUri, token, mode, settings.ConsoleCacheCatalogs);
        }
        catch (ServerException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cache en vigueur pour ce processus : réglages enregistrés (lus au premier usage), mis à jour à chaque enregistrement
    /// des réglages (<see cref="Configure"/>).
    /// </summary>
    public static ConsoleCache? Current
    {
        get
        {
            if (TestDisabled.Value) return null;
            if (TestOverride.Value is { } test) return test;
            lock (StaticLock)
            {
                if (!_resolved)
                {
                    _current = From(Settings.Load());
                    _resolved = true;
                }
                return _current;
            }
        }
    }

    /// <summary>Réglages modifiés (enregistrement depuis l'interface ou « config set ») : le cache suit.</summary>
    public static void Configure(Settings settings)
    {
        var next = From(settings);
        lock (StaticLock)
        {
            if (_current is { } c && next is { } n && c.BaseUri == n.BaseUri && c._token == n._token && c.Mode == n.Mode && c.Catalogs == n.Catalogs) return;
            _current = next;
            _resolved = true;
        }
    }

    /// <summary>Tests : <paramref name="cache"/> utilisé par le code appelé depuis ce test (null = aucun cache).</summary>
    internal static IDisposable UseForTest(ConsoleCache? cache)
    {
        var previous = (TestOverride.Value, TestDisabled.Value);
        TestOverride.Value = cache;
        TestDisabled.Value = cache is null;
        return new Restore(() => (TestOverride.Value, TestDisabled.Value) = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // ------------------------------------------------------------------ disponibilité

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Raison pour laquelle la console n'est pas sollicitée en ce moment (null = disponible).</summary>
    public string? UnavailableReason
    {
        get { lock (_lock) return Now < _unavailableUntil ? _unavailableReason : null; }
    }

    private void MarkUnavailable(string reason, TimeSpan duration)
    {
        lock (_lock)
        {
            _unavailableUntil = Now + duration;
            _unavailableReason = reason;
        }
    }

    /// <summary>Le cache de la console est-il utilisable (console joignable, cache actif, catalogues relayés si demandé) ?</summary>
    private async Task<bool> UsableAsync(bool catalog, CancellationToken ct)
    {
        if (catalog && !Catalogs) return false;
        (bool Enabled, bool Catalogs)? state;
        lock (_lock)
        {
            if (Now < _unavailableUntil) return false;
            state = Now - _statusCheckedAt < StatusRefresh ? _consoleState : null;
        }
        if (state is null)
        {
            var status = await FetchStatusAsync(ct).ConfigureAwait(false);
            if (status is null) return false;
            state = (status.Enabled, status.Catalogs);
            lock (_lock)
            {
                _consoleState = state;
                _statusCheckedAt = Now;
            }
        }
        return state.Value.Enabled && (!catalog || state.Value.Catalogs);
    }

    /// <summary>État du cache sur la console ; null si indisponible (console ancienne, injoignable, jeton refusé).</summary>
    public async Task<ConsoleCacheStatus?> GetStatusAsync(CancellationToken ct = default) => await FetchStatusAsync(ct).ConfigureAwait(false);

    private async Task<ConsoleCacheStatus?> FetchStatusAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await SendAsync("api/cache/status", null, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Console 3.x : pas de cache. Rien à réessayer avant longtemps.
                MarkUnavailable("console sans cache de pilotes (version antérieure à 4.0)", TimeSpan.FromHours(1));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                MarkUnavailable($"état du cache illisible (réponse {(int)response.StatusCode})", RetryAfterFailure);
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            var root = doc.RootElement;
            bool B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            long L(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var x) ? x : 0;
            return new ConsoleCacheStatus(B("enabled"), B("catalogs"), L("sizeBytes"), L("maxBytes"), (int)L("files"),
                root.TryGetProperty("hitRatio", out var hr) && hr.ValueKind == JsonValueKind.Number ? hr.GetDouble() : null,
                L("bytesSaved"), B("diskGuardActive"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            MarkUnavailable(ex is OperationCanceledException ? "console trop lente" : $"console injoignable ({ex.Message})", RetryAfterFailure);
            return null;
        }
    }

    // ------------------------------------------------------------------ fichiers

    /// <summary>
    /// Télécharge <paramref name="official"/> par la console dans <paramref name="destination"/> (créé ou remplacé).
    /// false = utiliser le téléchargement direct (la raison est journalisée). Le fichier obtenu n'est PAS vérifié ici :
    /// l'appelant (<see cref="Downloader"/>) le vérifie avant tout usage.
    /// </summary>
    public async Task<bool> TryDownloadFileAsync(Uri official, string destination, string? sha256, string? sha1, string? md5,
        Action<long, long?>? progress = null, CancellationToken ct = default)
    {
        if (!await UsableAsync(catalog: false, ct).ConfigureAwait(false))
        {
            Fallback(official, UnavailableReason ?? "cache désactivé sur la console", quiet: true);
            return false;
        }
        var query = $"api/cache/file?url={Uri.EscapeDataString(official.AbsoluteUri)}" +
                    (sha256 is null ? "" : $"&sha256={sha256}") + (sha1 is null ? "" : $"&sha1={sha1}") + (md5 is null ? "" : $"&md5={md5}");
        long received = 0;
        string? etag = null;
        var resumable = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var resuming = attempt > 0;
            try
            {
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headers.CancelAfter(HeadersTimeout);
                using var response = await SendAsync(query, resuming ? (received, etag) : null, headers.Token).ConfigureAwait(false);
                // Le délai ne vaut que pour les en-têtes : un gros fichier peut ensuite prendre plusieurs minutes.
                headers.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!response.IsSuccessStatusCode)
                {
                    HandleRefusal(official, response);
                    TryDelete(destination);
                    return false;
                }
                // Reprise refusée (fichier remplacé entre-temps : réponse complète 200) : on recommence depuis le début.
                if (resuming && response.StatusCode != HttpStatusCode.PartialContent) received = 0;
                var total = response.Content.Headers.ContentLength is { } len ? len + received : (long?)null;
                etag = response.Headers.ETag?.Tag;
                resumable = etag is not null && response.Headers.AcceptRanges.Contains("bytes");
                var cache = response.Headers.TryGetValues("X-SpokaPilote-Cache", out var v) ? v.FirstOrDefault() : null;

                await using (var output = OpenLocal(destination, received > 0 ? FileMode.Append : FileMode.Create))
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    var lastReport = DateTime.MinValue;
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        var n = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        await WriteLocalAsync(output, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        received += n;
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                        {
                            progress?.Invoke(received, total);
                            lastReport = DateTime.UtcNow;
                        }
                    }
                }
                progress?.Invoke(received, total);
                if (total is { } expected && received != expected)
                    throw new IOException($"transfert incomplet ({received} octets sur {expected})");
                Log.Info($"Téléchargé par la console ({cache ?? "cache"}) : {Path.GetFileName(official.AbsolutePath)} ({received / 1048576.0:0.0} Mo).");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (LocalFileException ex)
            {
                // Problème du poste (fichier temporaire verrouillé, accès refusé, disque plein) : la console n'y est pour
                // rien, elle reste sollicitée pour les fichiers suivants ; celui-ci est téléchargé directement.
                TryDelete(destination);
                Fallback(official, $"fichier local inaccessible ({ex.Message})");
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                var reason = ex is OperationCanceledException ? "console trop lente (délai dépassé)" : $"transfert interrompu ({ex.Message})";
                if (attempt == 0 && resumable && received > 0)
                {
                    Log.Info($"Cache de la console : {reason}, reprise à {received / 1048576.0:0.0} Mo.");
                    continue;
                }
                TryDelete(destination);
                // Console coupée en plein transfert ou injoignable : elle n'est plus sollicitée pendant quelques minutes.
                if (received == 0 || ex is OperationCanceledException) MarkUnavailable(reason, RetryAfterFailure);
                Fallback(official, reason);
                return false;
            }
        }
        TryDelete(destination);
        return false;
    }

    private void HandleRefusal(Uri official, HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues("X-SpokaPilote-Cache", out var v) ? v.FirstOrDefault() : null;
        var code = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound when header == "disabled":
                lock (_lock)
                {
                    _consoleState = (false, false);
                    _statusCheckedAt = Now;
                }
                Fallback(official, "cache désactivé sur la console", quiet: true);
                break;
            case HttpStatusCode.NotFound when header is null:
                MarkUnavailable("console sans cache de pilotes (version antérieure à 4.0)", TimeSpan.FromHours(1));
                Fallback(official, "console sans cache de pilotes", quiet: true);
                break;
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when header != "refused":
                MarkUnavailable($"jeton refusé par la console ({code})", TimeSpan.FromMinutes(10));
                Fallback(official, "jeton refusé par la console");
                break;
            case HttpStatusCode.TooManyRequests:
                MarkUnavailable("trop de tentatives refusées (429)", RetryAfterFailure);
                Fallback(official, "console : trop de tentatives (429)");
                break;
            default:
                // 403 « refused » (hors sources officielles de la console), 503 « bypass » (cache plein, disque, limite), 502...
                Fallback(official, $"la console ne sert pas ce fichier ({code}{(header is null ? "" : $", {header}")})", quiet: response.StatusCode == HttpStatusCode.ServiceUnavailable);
                break;
        }
    }

    private void Fallback(Uri official, string reason, bool quiet = false)
    {
        var message = $"Cache de la console non utilisé pour {Path.GetFileName(official.AbsolutePath)} : {reason} ; téléchargement direct depuis {official.Host}.";
        if (Mode == ConsoleCacheMode.On && !quiet) Log.Warn(message);
        else Log.Info(message);
    }

    /// <summary>Copie de la console refusée par les vérifications du poste : événement de sécurité, toujours journalisé en avertissement.</summary>
    public void ReportRejected(Uri official, string reason) =>
        Log.Warn($"⚠ Copie fournie par la console REFUSÉE pour {Path.GetFileName(official.AbsolutePath)} : {reason}. " +
                 $"Téléchargement direct depuis {official.Host}. Vérifiez l'intégrité de la console ({BaseUri.Host}).");

    // ------------------------------------------------------------------ catalogues

    /// <summary>
    /// La console peut-elle relayer ce catalogue ? Seulement un .cab d'un domaine officiel dont l'éditeur attendu est connu
    /// (Dell, HP...) : sa signature Authenticode l'authentifie, d'où qu'il vienne. Jamais un XML, un JSON ou une page
    /// (catalogue et descripteurs Lenovo, service NVIDIA, Microsoft Update Catalog) : rien ne les authentifie, or ils
    /// désignent les installeurs à lancer, leurs empreintes et leurs lignes de commande.
    /// </summary>
    internal static bool CanRelayCatalog(Uri official) =>
        OfficialHosts.IsOfficialMetadata(official) &&
        !official.Host.EndsWith("catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
        official.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) &&
        TrustPolicy.ExpectedPublishers(official) is not null;

    /// <summary>
    /// Catalogue officiel .cab par la console, écrit dans <paramref name="destination"/>. Il doit porter une signature
    /// Authenticode valide de l'éditeur attendu pour son domaine (Dell pour downloads.dell.com, HP pour hpia.hpcloud.hp.com),
    /// sinon il est refusé. null = utiliser le téléchargement direct (toujours le cas pour un catalogue non signable).
    /// </summary>
    public async Task<ConsoleCatalogResult?> TryCatalogFileAsync(Uri official, string destination, CancellationToken ct = default)
    {
        if (!CanRelayCatalog(official)) return null;
        var result = await FetchCatalogAsync($"api/cache/catalog?url={Uri.EscapeDataString(official.AbsoluteUri)}", official, destination, ct).ConfigureAwait(false);
        if (result is not { Status: ConsoleCatalogStatus.Ok }) return result;
        var sig = SignatureVerifier.Verify(destination);
        if (!sig.IsValid || !TrustPolicy.IsExpectedPublisher(official, sig.Signer))
        {
            TryDelete(destination);
            ReportRejected(official, sig.IsValid
                ? $"catalogue signé par {sig.Signer}, éditeur attendu : {string.Join(" / ", TrustPolicy.ExpectedPublishers(official)!)}"
                : $"catalogue non signé ou altéré ({sig.Detail})");
            return null;
        }
        return result;
    }

    /// <summary>
    /// Page de RECHERCHE du Microsoft Update Catalog par la console (gardée quelques heures, requêtes espacées) : la
    /// limitation « Too many search requests » frappe les recherches de tout un parc derrière une même adresse IP. Ce
    /// n'est qu'une indication : les liens de téléchargement et les empreintes de l'entrée retenue sont toujours demandés
    /// directement à Microsoft (<see cref="Sources.MicrosoftCatalogClient.GetDownloadUrlsAsync"/>).
    /// </summary>
    public Task<ConsoleCatalogResult?> TryCatalogSearchAsync(string query, CancellationToken ct = default) =>
        FetchCatalogAsync($"api/cache/catalog/mscatalog-search?q={Uri.EscapeDataString(query)}",
            new Uri($"https://{OfficialHosts.MicrosoftCatalogHost}/Search.aspx"), null, ct);

    private async Task<ConsoleCatalogResult?> FetchCatalogAsync(string path, Uri official, string? destination, CancellationToken ct)
    {
        if (!await UsableAsync(catalog: true, ct).ConfigureAwait(false)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CatalogTimeout);
            using var response = await SendAsync(path, null, cts.Token).ConfigureAwait(false);
            var header = response.Headers.TryGetValues("X-SpokaPilote-Cache", out var v) ? v.FirstOrDefault() : null;
            var origin = response.Headers.TryGetValues("X-SpokaPilote-Origin-Status", out var o) ? o.FirstOrDefault() : null;
            DateTimeOffset? fetchedAt = response.Headers.TryGetValues("X-SpokaPilote-Fetched-At", out var f) &&
                                        DateTimeOffset.TryParse(f.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fa)
                ? fa
                : null;
            if (response.StatusCode == HttpStatusCode.NotFound && origin == "404")
                return new ConsoleCatalogResult(ConsoleCatalogStatus.NotFoundAtSource, FetchedAt: fetchedAt);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && header == "throttled")
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(3);
                return new ConsoleCatalogResult(ConsoleCatalogStatus.RateLimited, RetryAfter: retry);
            }
            if (!response.IsSuccessStatusCode)
            {
                HandleRefusal(official, response);
                return null;
            }
            if (destination is not null)
            {
                long written = 0;
                await using (var output = OpenLocal(destination, FileMode.Create))
                await using (var input = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    int n;
                    while ((n = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await WriteLocalAsync(output, buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                        written += n;
                    }
                }
                if (response.Content.Headers.ContentLength is { } len && written != len)
                    throw new IOException("catalogue incomplet");
                return new ConsoleCatalogResult(ConsoleCatalogStatus.Ok, FetchedAt: fetchedAt);
            }
            return new ConsoleCatalogResult(ConsoleCatalogStatus.Ok, await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), fetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LocalFileException ex)
        {
            // Problème du poste, pas de la console : accès direct pour ce catalogue, la console reste sollicitée.
            if (destination is not null) TryDelete(destination);
            Log.Info($"Cache de la console non utilisé pour un catalogue ({official.Host}) : fichier local inaccessible ({ex.Message}) ; accès direct.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (destination is not null) TryDelete(destination);
            var reason = ex is OperationCanceledException ? "console trop lente (délai dépassé)" : $"console injoignable ({ex.Message})";
            MarkUnavailable(reason, RetryAfterFailure);
            Log.Info($"Cache de la console non utilisé pour un catalogue ({official.Host}) : {reason} ; accès direct.");
            return null;
        }
    }

    // ------------------------------------------------------------------ plomberie

    private async Task<HttpResponseMessage> SendAsync(string path, (long From, string? ETag)? range, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (range is { } r)
        {
            request.Headers.Range = new RangeHeaderValue(r.From, null);
            if (r.ETag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(r.ETag));
        }
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Erreur d'accès à un fichier du POSTE (et non de la console) : verrouillé, accès refusé, disque plein.</summary>
    private sealed class LocalFileException(Exception inner) : Exception(inner.Message, inner);

    private static FileStream OpenLocal(string path, FileMode mode)
    {
        try
        {
            return new FileStream(path, mode, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileException(ex);
        }
    }

    private static async ValueTask WriteLocalAsync(FileStream output, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            await output.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileException(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fichier temporaire */ }
    }
}
