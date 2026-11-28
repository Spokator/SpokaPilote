using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Sources;

public sealed class CatalogRateLimitedException()
    : Exception("Le Microsoft Update Catalog limite temporairement les recherches ; réessayez dans quelques minutes.");

public sealed record CatalogEntry(
    string UpdateId,
    string Title,
    string Products,
    string Classification,
    DateTime? LastUpdated,
    string? Version,
    long? SizeBytes);

/// <summary>Fiche d'une entrée du catalogue lue directement chez Microsoft (voir <see cref="MicrosoftCatalogClient.GetEntryDetailsAsync(string, CancellationToken)"/>).</summary>
public sealed record CatalogEntryDetails(string UpdateId, string Title, string? Version, string? Provider, string? Manufacturer);

/// <summary>
/// Client du Microsoft Update Catalog (catalog.update.microsoft.com) : le dépôt public de Microsoft où sont
/// publiés tous les pilotes distribués par Windows Update (signés WHQL). Recherche par ID matériel ou par modèle.
/// </summary>
public static partial class MicrosoftCatalogClient
{
    private const string BaseUrl = "https://www.catalog.update.microsoft.com";
    // Le catalogue limite le débit (« Too many search requests ») : 2 requêtes simultanées max, espacées.
    private static readonly SemaphoreSlim Throttle = new(2);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(400);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly object IntervalLock = new();

    /// <summary>Date jusqu'à laquelle le catalogue est considéré comme saturé (aucune nouvelle requête).</summary>
    public static DateTime RateLimitedUntil { get; private set; } = DateTime.MinValue;
    public static bool IsRateLimited => DateTime.UtcNow < RateLimitedUntil;

    /// <summary>
    /// Mode automatisé (CLI, déploiement de parc) : en cas de limitation, attendre la fin de la pause puis reprendre
    /// (au plus 2 fois) plutôt que de rendre un résultat partiel.
    /// </summary>
    public static bool WaitWhenRateLimited { get; set; }

    public static async Task<List<CatalogEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SearchOnceAsync(query, ct).ConfigureAwait(false);
            }
            catch (CatalogRateLimitedException) when (WaitWhenRateLimited && attempt < 2)
            {
                var wait = RateLimitedUntil - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Log.Info($"Catalogue Microsoft saturé : reprise dans {Math.Ceiling(wait.TotalMinutes)} min.");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
        }
    }

    [GeneratedRegex(@"<tr id=""(?<id>[0-9a-f-]{36})_R\d+""(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<td[^>]*_C(?<col>\d)_R\d+""[^>]*>(?<cell>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"_originalSize"">(?<n>\d+)<", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    internal static async Task<List<CatalogEntry>> SearchOnceAsync(string query, CancellationToken ct)
    {
        // --- Durcissement du dossier de données (4.0.1) : début -----------------------------------------------------------
        // Dossier Cache\catalog jamais suivi s'il est un lien (recréé), et résultat en cache lu par une exécution élevée
        // seulement s'il a été écrit par SYSTEM / un administrateur, sans lien sur le chemin (DataProtection).
        var cacheFile = Path.Combine(AppPaths.CacheSubdirectory("catalog"), AppPaths.SafeName(query, 120) + ".json");
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(12) &&
            DataProtection.IsTrustedForElevatedUse(cacheFile))
        {
            try { return JsonSerializer.Deserialize<List<CatalogEntry>>(await File.ReadAllTextAsync(cacheFile, ct)) ?? new(); }
            catch { /* cache corrompu : on relance la recherche */ }
        }
        // --- Durcissement du dossier de données (4.0.1) : fin -------------------------------------------------------------

        // Console 4.0 : la recherche passe par son cache (une requête vers Microsoft pour tout le parc, requêtes espacées).
        // Simple indication : les liens et empreintes de l'entrée retenue sont ensuite demandés directement à Microsoft
        // (GetDownloadUrlsAsync), et le paquet est vérifié (SHA-1 publié, signature, ID matériel de l'INF).
        if (Online.ConsoleCache.Current is { Catalogs: true } console)
        {
            var viaConsole = await console.TryCatalogSearchAsync(query, ct).ConfigureAwait(false);
            if (viaConsole is { Status: Online.ConsoleCatalogStatus.RateLimited } limited)
            {
                RateLimitedUntil = DateTime.UtcNow + (limited.RetryAfter is { } r && r > TimeSpan.Zero && r < TimeSpan.FromMinutes(30) ? r : TimeSpan.FromMinutes(3));
                Log.Warn("Le Microsoft Update Catalog limite temporairement les recherches (signalé par la console) : pause.");
                throw new CatalogRateLimitedException();
            }
            if (viaConsole is { Status: Online.ConsoleCatalogStatus.Ok, Text: { } page })
                return HandleSearchPage(query, page, cacheFile, viaConsole.FetchedAt);
        }

        if (IsRateLimited) throw new CatalogRateLimitedException();

        await Throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRateLimited) throw new CatalogRateLimitedException();
            TimeSpan wait;
            lock (IntervalLock)
            {
                var next = _lastRequest + MinInterval;
                wait = next > DateTime.UtcNow ? next - DateTime.UtcNow : TimeSpan.Zero;
                _lastRequest = DateTime.UtcNow + wait;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

            var url = $"{BaseUrl}/Search.aspx?q={Uri.EscapeDataString(query)}";
            string html;
            try
            {
                html = await Http.GetStringAsync(url, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"Catalogue Microsoft injoignable pour « {query} » : {ex.Message}");
                return new();
            }

            return HandleSearchPage(query, html, cacheFile, null);
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>
    /// Page de résultats (directe ou relayée par la console) : « Too many search requests » → pause de 3 minutes, jamais
    /// mise en cache ; résultats → cache local de 12 h (daté de l'obtention par la console, le cas échéant).
    /// </summary>
    private static List<CatalogEntry> HandleSearchPage(string query, string html, string cacheFile, DateTimeOffset? fetchedAt)
    {
        if (html.Contains("Too many search requests", StringComparison.OrdinalIgnoreCase))
        {
            RateLimitedUntil = DateTime.UtcNow.AddMinutes(3);
            Log.Warn("Le Microsoft Update Catalog limite temporairement les recherches : pause de 3 minutes.");
            throw new CatalogRateLimitedException();
        }

        var entries = ParseSearchResults(html);
        // On ne met en cache un résultat vide que si la page indique explicitement « aucun résultat ».
        var cacheable = entries.Count > 0 || (html.Contains("ctl00_catalogBody_noResults", StringComparison.OrdinalIgnoreCase)
                                          && html.Contains("Search tips", StringComparison.OrdinalIgnoreCase));
        if (cacheable)
        {
            try
            {
                File.WriteAllText(cacheFile, JsonSerializer.Serialize(entries));
                if (fetchedAt is { } f && f.UtcDateTime <= DateTime.UtcNow) File.SetLastWriteTimeUtc(cacheFile, f.UtcDateTime);
            }
            catch
            {
                /* cache facultatif */
            }
        }
        else
        {
            Log.Debug($"Catalogue Microsoft : page inattendue pour « {query} » (non mise en cache).");
        }
        return entries;
    }

    /// <summary>Tests : fin de la pause « Too many search requests ».</summary>
    internal static void ResetRateLimit() => RateLimitedUntil = DateTime.MinValue;

    internal static List<CatalogEntry> ParseSearchResults(string html)
    {
        var entries = new List<CatalogEntry>();
        foreach (Match row in RowRegex().Matches(html))
        {
            var cells = new string[8];
            foreach (Match cell in CellRegex().Matches(row.Value))
            {
                var col = int.Parse(cell.Groups["col"].Value, CultureInfo.InvariantCulture);
                if (col < cells.Length) cells[col] = cell.Groups["cell"].Value;
            }

            string Clean(string? s) => s is null ? "" : Spaces().Replace(WebUtility.HtmlDecode(TagRegex().Replace(s, " ")), " ").Trim();

            DateTime? date = DateTime.TryParse(Clean(cells[4]), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ? d : null;
            long? size = null;
            var sm = SizeRegex().Match(cells[6] ?? "");
            if (sm.Success) size = long.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture);
            var version = Clean(cells[5]);

            entries.Add(new CatalogEntry(
                row.Groups["id"].Value,
                Clean(cells[1]),
                Clean(cells[2]),
                Clean(cells[3]),
                date,
                string.IsNullOrWhiteSpace(version) || version == "n/a" ? null : version,
                size));
        }
        return entries;
    }

    /// <summary>
    /// Résout les URLs de téléchargement (.cab) d'une entrée du catalogue, TOUJOURS directement auprès de Microsoft (jamais
    /// par le cache de la console, même quand la recherche est passée par elle) : l'URL porte le SHA-1 du fichier
    /// (<see cref="Sha1FromUrl"/>), seule empreinte indépendante avec laquelle le poste vérifie le paquet, y compris
    /// quand les octets viennent ensuite de la console. Requête légère (une par paquet installé), qui ne subit pas la
    /// limitation des recherches.
    /// </summary>
    public static Task<List<string>> GetDownloadUrlsAsync(string updateId, CancellationToken ct = default) =>
        GetDownloadUrlsAsync(updateId, Http.Client, ct);

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateIdRegex();

    internal static async Task<List<string>> GetDownloadUrlsAsync(string updateId, HttpClient direct, CancellationToken ct)
    {
        if (!UpdateIdRegex().IsMatch(updateId))
            throw new ArgumentException($"Identifiant d'entrée du catalogue Microsoft invalide : {updateId}", nameof(updateId));
        var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("updateIDs", payload) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await direct.PostAsync($"{BaseUrl}/DownloadDialog.aspx", content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return ParseDownloadDialog(text);
    }

    /// <summary>
    /// Fiche d'une entrée (ScopedViewInline.aspx), TOUJOURS demandée directement à Microsoft : titre, version, fournisseur et
    /// fabricant du pilote. Confirme ce qu'annonçait la page de recherche (qui peut avoir été relayée, donc altérée, par la
    /// console) avant que ces valeurs ne servent à la politique, aux anneaux et au contrôle de l'INF. null si la fiche est
    /// injoignable ou illisible.
    /// </summary>
    public static Task<CatalogEntryDetails?> GetEntryDetailsAsync(string updateId, CancellationToken ct = default) =>
        GetEntryDetailsAsync(updateId, Http.Client, ct);

    internal static async Task<CatalogEntryDetails?> GetEntryDetailsAsync(string updateId, HttpClient direct, CancellationToken ct)
    {
        if (!UpdateIdRegex().IsMatch(updateId))
            throw new ArgumentException($"Identifiant d'entrée du catalogue Microsoft invalide : {updateId}", nameof(updateId));
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await direct.GetAsync($"{BaseUrl}/ScopedViewInline.aspx?updateid={updateId}", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return ParseEntryDetails(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), updateId);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Info($"Catalogue Microsoft : fiche de l'entrée {updateId} injoignable ({ex.Message}).");
            return null;
        }
    }

    [GeneratedRegex(@"<span id=""ScopedViewHandler_(?<key>titleText|version|driverProvider|manufacturer|UpdateID)""[^>]*>(?<value>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DetailFieldRegex();

    /// <summary>Champs de la fiche (« ScopedViewHandler_titleText », « _version », « _driverProvider », « _manufacturer »).</summary>
    internal static CatalogEntryDetails? ParseEntryDetails(string html, string updateId)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DetailFieldRegex().Matches(html))
            fields.TryAdd(m.Groups["key"].Value, Spaces().Replace(WebUtility.HtmlDecode(TagRegex().Replace(m.Groups["value"].Value, " ")), " ").Trim());
        // Fiche d'une autre entrée que celle demandée : inutilisable.
        if (fields.TryGetValue("UpdateID", out var id) && !id.Equals(updateId, StringComparison.OrdinalIgnoreCase)) return null;
        if (!fields.TryGetValue("titleText", out var title) || title.Length == 0) return null;
        string? Optional(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 && !v.Equals("n/a", StringComparison.OrdinalIgnoreCase) ? v : null;
        return new CatalogEntryDetails(updateId, title, Optional("version"), Optional("driverProvider"), Optional("manufacturer"));
    }

    [GeneratedRegex(@"(?:downloadInformation\[(?<u>\d+)\]\.)?files\[(?<f>\d+)\]\.(?<key>url|digest)\s*=\s*'(?<value>[^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadFileFieldRegex();

    /// <summary>
    /// Liens de la réponse de DownloadDialog.aspx (« downloadInformation[0].files[0].url = '...' » et
    /// « ....digest = '&lt;SHA-1 en Base64&gt;' ») : domaines officiels uniquement ; un lien dont le SHA-1 du nom de fichier
    /// diffère de l'empreinte publiée à côté est écarté.
    /// </summary>
    internal static List<string> ParseDownloadDialog(string text)
    {
        var urls = new List<string>();
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (Match m in DownloadFileFieldRegex().Matches(text))
        {
            var id = $"{m.Groups["u"].Value}:{m.Groups["f"].Value}";
            if (!fields.TryGetValue(id, out var f)) fields[id] = f = new(StringComparer.Ordinal);
            f.TryAdd(m.Groups["key"].Value.ToLowerInvariant(), m.Groups["value"].Value.Trim());
        }
        foreach (var f in fields.Values)
        {
            if (!f.TryGetValue("url", out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !OfficialHosts.IsOfficialDownload(uri)) continue;
            if (f.TryGetValue("digest", out var digest) && DigestToHex(digest) is { } published && Sha1FromUrl(url) is { } named &&
                !published.Equals(named, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Catalogue Microsoft : lien écarté, SHA-1 du nom ({named}) différent de l'empreinte publiée ({published}) : {url}");
                continue;
            }
            if (!urls.Contains(url, StringComparer.Ordinal)) urls.Add(url);
        }
        return urls;
    }

    /// <summary>Empreinte Base64 (champ « digest ») → hexadécimal ; null si illisible.</summary>
    private static string? DigestToHex(string base64)
    {
        try
        {
            return Convert.ToHexString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Les fichiers du catalogue sont nommés « {guid}_{sha1}.cab » : le SHA-1 du fichier est dans le nom,
    /// ce qui permet un contrôle d'intégrité indépendant.
    /// </summary>
    public static string? Sha1FromUrl(string url)
    {
        var name = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        var idx = name.LastIndexOf('_');
        if (idx < 0) return null;
        var hash = name[(idx + 1)..];
        return hash.Length == 40 && hash.All(Uri.IsHexDigit) ? hash : null;
    }

    // ------------------------------------------------------------------ applicabilité OS

    private static readonly (string Release, int Build)[] Win11Releases =
    {
        ("21H2", 22000), ("22H2", 22621), ("23H2", 22631), ("24H2", 26100), ("25H2", 26200), ("26H1", 28000),
    };

    private static readonly (string Release, int Build)[] Win10Releases =
    {
        ("1507", 10240), ("1511", 10586), ("1607", 14393), ("1703", 15063), ("1709", 16299), ("1803", 17134),
        ("1809", 17763), ("1903", 18362), ("1909", 18363), ("2004", 19041), ("20H2", 19042), ("21H1", 19043),
        ("21H2", 19044), ("22H2", 19045),
    };

    // ex. « Windows 11 Client, version 24H2 and later, Servicing Drivers » ou « Windows 10 and later drivers ».
    [GeneratedRegex(@"Windows (?<os>10|11)(?: Client| S)?(?:,\s*version (?<rel>\w+))?(?<later> and later)?", RegexOptions.IgnoreCase)]
    private static partial Regex ProductRegex();

    /// <summary>
    /// Score d'applicabilité d'une entrée pour l'OS courant : 2 = cible exactement cette version de Windows,
    /// 1 = compatible (autre version de Windows 10/11 ou « and later »), 0 = non applicable.
    /// </summary>
    public static int OsApplicability(string products, int osBuild)
    {
        var isWin11 = osBuild >= 22000;
        var best = 0;
        {
            foreach (Match m in ProductRegex().Matches(products))
            {
                var os = m.Groups["os"].Value;
                var rel = m.Groups["rel"].Success ? m.Groups["rel"].Value.ToUpperInvariant() : null;
                var later = m.Groups["later"].Success;
                var table = os == "11" ? Win11Releases : Win10Releases;

                if (os == "11" && !isWin11) continue; // pilote Windows 11 sur Windows 10 : non.
                if (rel is null)
                {
                    best = Math.Max(best, (os == "11") == isWin11 ? 2 : 1);
                    continue;
                }
                var minBuild = table.FirstOrDefault(r => r.Release == rel).Build;
                if (minBuild == 0) { best = Math.Max(best, 1); continue; }

                if (os == "10" && isWin11)
                {
                    best = Math.Max(best, 1); // les pilotes Windows 10 restent installables sous Windows 11.
                    continue;
                }
                if (later && osBuild >= minBuild) best = Math.Max(best, 2);
                else if (!later && Math.Abs(osBuild - minBuild) < 10) best = Math.Max(best, 2);
                else if (osBuild >= minBuild) best = Math.Max(best, 1);
            }
        }
        // « Windows Server » seul : 0 (non applicable à un poste client).
        return best;
    }
}
