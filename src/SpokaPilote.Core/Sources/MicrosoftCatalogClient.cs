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

    [GeneratedRegex(@"files\[\d+\]\.url\s*=\s*'(?<url>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static async Task<List<CatalogEntry>> SearchOnceAsync(string query, CancellationToken ct)
    {
        var cacheFile = Path.Combine(AppPaths.Ensure(Path.Combine(AppPaths.Cache, "catalog")), AppPaths.SafeName(query, 120) + ".json");
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(12))
        {
            try { return JsonSerializer.Deserialize<List<CatalogEntry>>(await File.ReadAllTextAsync(cacheFile, ct)) ?? new(); }
            catch { /* cache corrompu : on relance la recherche */ }
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
                try { await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(entries), ct); } catch { /* cache facultatif */ }
            }
            return entries;
        }
        finally
        {
            Throttle.Release();
        }
    }

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

    /// <summary>Résout les URLs de téléchargement (.cab) d'une entrée du catalogue.</summary>
    public static async Task<List<string>> GetDownloadUrlsAsync(string updateId, CancellationToken ct = default)
    {
        var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("updateIDs", payload) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await Http.Client.PostAsync($"{BaseUrl}/DownloadDialog.aspx", content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return DownloadUrlRegex().Matches(text).Select(m => m.Groups["url"].Value).Distinct().ToList();
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
