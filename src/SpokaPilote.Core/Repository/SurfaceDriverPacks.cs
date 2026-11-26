using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Repository;

/// <summary>Un modèle de la table Surface : page officielle du Centre de téléchargement + identifiants SMBIOS.</summary>
public sealed class SurfaceModel
{
    public string Name { get; set; } = "";
    /// <summary>Identifiant de la page « Download drivers and firmware » (www.microsoft.com/download/details.aspx?id=...).</summary>
    public int DownloadId { get; set; }
    /// <summary>amd64 ou arm64 (nomenclature des INF).</summary>
    public string Architecture { get; set; } = "amd64";
    /// <summary>« System Model » SMBIOS (Win32_ComputerSystem.Model), ex. « Surface Pro 9 ».</summary>
    public List<string> SystemModels { get; set; } = new();
    /// <summary>« System SKU » SMBIOS (Win32_ComputerSystem.SystemSKUNumber), ex. « Surface_Pro_9_for_Business_2038 ».</summary>
    public List<string> SystemSkus { get; set; } = new();

    public string PageUrl => SurfaceDriverPacks.PageUrl(DownloadId);

    /// <summary>Identifiants reconnus par la recherche : SKU d'abord, puis nom de modèle SMBIOS.</summary>
    public PackSystem ToPackSystem() =>
        new(Name, SystemSkus.Concat(SystemModels).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
}

public sealed class SurfaceCatalog
{
    public int Version { get; set; }
    public string? Updated { get; set; }
    public string? ModelSource { get; set; }
    public string? SkuSource { get; set; }
    public List<SurfaceModel> Models { get; set; } = new();
}

/// <summary>Fichier publié sur une page du Centre de téléchargement (bloc JSON « __DLCDetails__ » de la page).</summary>
public sealed record DownloadCenterFile(string Name, string Url, long? Size, DateTime? Published);

public sealed record DownloadCenterPage(int Id, string Title, IReadOnlyList<DownloadCenterFile> Files);

/// <summary>Pack Surface indisponible (page modifiée, fichier retiré) : message destiné à l'utilisateur.</summary>
public sealed class SurfacePackUnavailableException(string message) : Exception(message);

/// <summary>
/// Packs officiels Microsoft Surface (« Surface drivers and firmware », un MSI par modèle et version de Windows).
/// Microsoft ne publie pas de catalogue machine comme Dell, HP ou Lenovo : la table modèle → page de téléchargement
/// (surface-driverpacks.json, constituée depuis la documentation Microsoft Learn) est embarquée, et les liens réels
/// sont relus sur la page officielle de chaque modèle (mise en cache 24 h, relue au moment du téléchargement).
/// Les MSI ne sont accompagnés d'aucune empreinte publiée : leur intégrité repose sur la signature Authenticode
/// « Microsoft Corporation » et sur la taille annoncée par la page.
/// </summary>
public static class SurfaceDriverPacks
{
    /// <summary>Page anglaise : le bloc JSON y est toujours présent, quelle que soit la langue du poste.</summary>
    public static string PageUrl(int downloadId) => $"https://www.microsoft.com/en-us/download/details.aspx?id={downloadId}";

    private const string ResourceName = "SpokaPilote.Core.Repository.surface-driverpacks.json";
    private static SurfaceCatalog? _catalog;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SurfaceCatalog Catalog => _catalog ??= LoadEmbedded();

    private static SurfaceCatalog LoadEmbedded()
    {
        using var stream = typeof(SurfaceDriverPacks).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Ressource {ResourceName} absente.");
        using var reader = new StreamReader(stream);
        return ParseCatalog(reader.ReadToEnd());
    }

    internal static SurfaceCatalog ParseCatalog(string json)
    {
        var catalog = JsonSerializer.Deserialize<SurfaceCatalog>(json, JsonOptions) ?? new SurfaceCatalog();
        catalog.Models = catalog.Models
            .Where(m => m.DownloadId > 0 && m.Name.Length > 0 && m.Architecture is "amd64" or "arm64")
            .ToList();
        return catalog;
    }

    // ------------------------------------------------------------------ recherche

    /// <summary>Modèles de la table que la requête peut viser (avant de lire les pages : une requête HTTP par modèle).</summary>
    internal static List<SurfaceModel> Candidates(DriverPackQuery query, IEnumerable<SurfaceModel>? models = null)
    {
        var list = (models ?? Catalog.Models)
            .Where(m => query.Architecture is null || m.Architecture.Equals(query.Architecture, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var systems = list.Select(m => (Model: m, System: m.ToPackSystem())).ToList();
        var hits = systems.Where(s => OemDriverPacks.SystemMatches(s.System, query, useModelFallback: false)).Select(s => s.Model).ToList();
        if (hits.Count == 0 && query.SystemModel is not null)
            hits = systems.Where(s => OemDriverPacks.SystemMatches(s.System, query, useModelFallback: true)).Select(s => s.Model).ToList();
        return hits;
    }

    /// <summary>Packs des modèles correspondant à la requête (pages lues en parallèle).</summary>
    public static Task<List<OemDriverPack>> SearchAsync(DriverPackQuery query, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var candidates = Candidates(query);
        if (candidates.Count == 0) return Task.FromResult(new List<OemDriverPack>());
        return LoadAsync(candidates, refresh: false, progress, ct);
    }

    /// <summary>Lit la page officielle de chaque modèle et en tire les packs (un par MSI conforme à la convention de nommage).</summary>
    public static async Task<List<OemDriverPack>> LoadAsync(IReadOnlyList<SurfaceModel> models, bool refresh, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Pages officielles des packs Surface ({models.Count} modèle(s), Centre de téléchargement Microsoft)...");
        var packs = new List<OemDriverPack>();
        var errors = new List<string>();
        var gate = new object();
        await Parallel.ForEachAsync(models, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (model, token) =>
        {
            try
            {
                var page = await GetPageAsync(model.DownloadId, refresh, token).ConfigureAwait(false);
                var built = page.Files.Select(f => ToPack(model, f)).OfType<OemDriverPack>().ToList();
                lock (gate) packs.AddRange(built);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SurfacePackUnavailableException or TaskCanceledException && !token.IsCancellationRequested)
            {
                Log.Warn($"Pack Surface « {model.Name} » : {ex.Message}");
                lock (gate) errors.Add($"{model.Name} : {ex.Message}");
            }
        }).ConfigureAwait(false);

        if (packs.Count == 0 && errors.Count > 0)
            throw new HttpRequestException($"Pages Surface indisponibles ({string.Join(" ; ", errors.Take(3))}).");
        foreach (var e in errors) progress?.Report($"Page ignorée — {e}");
        Log.Info($"Packs Surface : {packs.Count} MSI sur {models.Count} page(s).");
        return packs;
    }

    /// <summary>
    /// Relit la page du modèle juste avant le téléchargement : Microsoft remplace le MSI à chaque mise à jour.
    /// Renvoie le pack avec le lien actuel, ou lève une exception explicite si le fichier n'est plus publié.
    /// </summary>
    public static async Task<OemDriverPack> RefreshAsync(OemDriverPack pack, CancellationToken ct = default)
    {
        var id = PageId(pack.InfoUrl) ?? throw new SurfacePackUnavailableException($"Page d'origine du pack {pack.FileName} inconnue.");
        var model = Catalog.Models.FirstOrDefault(m => m.DownloadId == id)
                    ?? new SurfaceModel { Name = pack.Systems.FirstOrDefault()?.Name ?? pack.Id, DownloadId = id, Architecture = pack.Architecture };
        DownloadCenterPage page;
        try
        {
            page = await GetPageAsync(id, refresh: true, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SurfacePackUnavailableException($"Page officielle {PageUrl(id)} inaccessible ({ex.Message}).");
        }

        var current = page.Files.FirstOrDefault(f => f.Name.Equals(pack.FileName, StringComparison.OrdinalIgnoreCase));
        if (current is not null && ToPack(model, current) is { } refreshed) return refreshed;

        var others = page.Files.Select(f => ToPack(model, f)).OfType<OemDriverPack>()
            .Where(p => p.Os == pack.Os && p.Architecture == pack.Architecture).Select(p => p.FileName).ToList();
        throw new SurfacePackUnavailableException(
            $"{pack.FileName} n'est plus publié sur la page officielle {PageUrl(id)}" +
            (others.Count > 0 ? $" ; version actuelle : {string.Join(", ", others)}. Relancez la commande pour la télécharger." : " et aucun remplaçant n'y figure pour ce Windows."));
    }

    internal static int? PageId(string? url)
    {
        var m = Regex.Match(url ?? "", @"[?&]id=(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private static async Task<DownloadCenterPage> GetPageAsync(int id, bool refresh, CancellationToken ct)
    {
        var cacheName = $"surface-dlc-{id}.html";
        if (refresh)
        {
            try { File.Delete(Path.Combine(AppPaths.Cache, cacheName)); } catch (IOException) { /* relu de toute façon */ }
        }
        string path;
        try
        {
            path = await Http.GetCachedFileAsync(PageUrl(id), cacheName, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new SurfacePackUnavailableException($"page {PageUrl(id)} retirée par Microsoft (HTTP {(int)ex.StatusCode})");
        }
        try
        {
            return ParsePage(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), id);
        }
        catch (SurfacePackUnavailableException)
        {
            // Page d'erreur ou de contrôle servie à la place de la page attendue : ne pas la garder en cache.
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    // ------------------------------------------------------------------ analyse de la page

    private static readonly Regex DetailsBlock = new(@"window\.__DLCDetails__\s*=\s*(\{.*?\})\s*</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Page « details.aspx » du Centre de téléchargement : les fichiers sont décrits dans le bloc JSON
    /// window.__DLCDetails__ (dlcDetailsView.downloadFile[] : name, url, size, datePublished).
    /// </summary>
    internal static DownloadCenterPage ParsePage(string html, int id)
    {
        var m = DetailsBlock.Match(html);
        if (!m.Success)
            throw new SurfacePackUnavailableException($"format inattendu de la page {PageUrl(id)} (bloc __DLCDetails__ absent)");

        JsonElement view;
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            view = doc.RootElement.GetProperty("dlcDetailsView").Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SurfacePackUnavailableException($"format inattendu de la page {PageUrl(id)} ({ex.Message})");
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (Str(view, "error") is { Length: > 0 } error)
            throw new SurfacePackUnavailableException($"page {PageUrl(id)} : {error}");

        var files = new List<DownloadCenterFile>();
        if (view.TryGetProperty("downloadFile", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                var name = Str(f, "name")?.Trim();
                var url = Str(f, "url")?.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                files.Add(new DownloadCenterFile(
                    name,
                    url,
                    long.TryParse(Str(f, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0 ? size : null,
                    DateTime.TryParse(Str(f, "datePublished"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null));
            }
        }
        return new DownloadCenterPage(id, Str(view, "downloadTitle") ?? "", files);
    }

    /// <summary>
    /// Convention de nommage Microsoft (depuis août 2019) : [Produit]_[Win10|Win11]_[build]_[version].[révision].msi,
    /// ex. SurfacePro9_Win11_22631_26.091.15297.0.msi ; certains anciens fichiers séparent la révision par « _ ».
    /// </summary>
    private static readonly Regex MsiName = new(@"^(?<product>.+?)_Win(?<os>10|11)_(?<build>\d{5})_(?<version>\d+(?:[._]\d+)+)\.msi$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static (string Os, int Build, string Version)? ParseFileName(string fileName)
    {
        var m = MsiName.Match(fileName.Trim());
        if (!m.Success) return null;
        return (m.Groups["os"].Value == "11" ? "win11" : "win10", int.Parse(m.Groups["build"].Value, CultureInfo.InvariantCulture),
                m.Groups["version"].Value.Replace('_', '.'));
    }

    /// <summary>Version commerciale de Windows correspondant au build déclaré par le nom du MSI (null si inconnue).</summary>
    internal static string? ReleaseForBuild(int build) => build switch
    {
        17763 => "1809",
        18362 => "1903",
        18363 => "1909",
        19041 => "2004",
        19042 => "20H2",
        19043 => "21H1",
        19044 => "21H2",
        19045 => "22H2",
        22000 => "21H2",
        22621 => "22H2",
        22631 => "23H2",
        26100 => "24H2",
        26200 => "25H2",
        28000 => "26H1",
        _ => null,
    };

    /// <summary>
    /// Pack correspondant à un fichier de la page, ou null : fichier hors convention (ex. pilote graphique NVIDIA seul
    /// de la Surface Laptop Studio 2), autre format que .msi, ou lien hors de download.microsoft.com.
    /// </summary>
    internal static OemDriverPack? ToPack(SurfaceModel model, DownloadCenterFile file)
    {
        var parsed = ParseFileName(file.Name);
        if (parsed is null)
        {
            Log.Debug($"Surface {model.Name} : {file.Name} ignoré (hors convention de nommage des packs pilotes et firmwares).");
            return null;
        }
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Surface {model.Name} : lien inattendu ignoré ({file.Url}).");
            return null;
        }
        var (os, build, version) = parsed.Value;
        return new OemDriverPack
        {
            Vendor = OemVendor.Microsoft,
            Id = Path.GetFileNameWithoutExtension(file.Name),
            Systems = new List<PackSystem> { model.ToPackSystem() },
            Os = os,
            Architecture = model.Architecture,
            OsReleases = ReleaseForBuild(build) is { } release ? new List<string> { release } : new List<string>(),
            OsBuild = build,
            Version = version,
            Date = file.Published,
            Url = uri.ToString(),
            Size = file.Size,
            InfoUrl = PageUrl(model.DownloadId),
        };
    }

    // ------------------------------------------------------------------ extraction

    /// <summary>
    /// Installation administrative Windows Installer : copie le contenu du MSI dans le dossier, SANS rien installer
    /// ni exécuter d'action personnalisée (méthode documentée par Microsoft pour extraire les pilotes Surface).
    /// </summary>
    internal static string MsiAdminArguments(string msi, string destination, string logPath) =>
        $"/a \"{msi}\" /qn TARGETDIR=\"{Path.TrimEndingDirectorySeparator(destination)}\" /L*v \"{logPath}\"";

    /// <summary>Codes de sortie de msiexec : 0 = succès ; les autres sont expliqués.</summary>
    internal static string DescribeMsiExitCode(int code) => code switch
    {
        1602 or 1223 => "annulée",
        1603 => "erreur fatale de Windows Installer",
        1618 => "une autre installation Windows Installer est en cours, réessayez plus tard",
        1619 => "MSI illisible ou corrompu",
        1620 => "MSI invalide",
        1625 => "interdite par une stratégie de groupe",
        1632 => "dossier temporaire inaccessible",
        _ => $"code {code}",
    };
}
