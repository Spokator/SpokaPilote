using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources.Oem;

namespace SpokaPilote.Core.Sources.Gpu;

/// <summary>Branche de pilotes NVIDIA GeForce retenue (réglage <see cref="Settings.NvidiaBranch"/>).</summary>
public enum NvidiaBranch
{
    /// <summary>Pilote Studio : publié moins souvent, validé pour la stabilité (applications professionnelles). Défaut pour un parc.</summary>
    Studio,
    /// <summary>Pilote Game Ready : le plus récent, optimisé pour les jeux, publié très fréquemment.</summary>
    GameReady,
}

/// <summary>Pilote renvoyé par le service officiel NVIDIA pour un identifiant de carte.</summary>
internal sealed record NvidiaDriverInfo(
    string Id,
    string Name,
    string Version,
    DateTime? Date,
    string DownloadUrl,
    string? DetailsUrl,
    bool IsStudio,
    bool IsBeta);

/// <summary>
/// Pilotes graphiques depuis le service officiel NVIDIA (gfwsl.geforce.com, « getDispDrvrByDevid », celui qu'utilisent
/// GeForce Experience et l'application NVIDIA) : la recherche se fait directement par identifiant PCI de la carte,
/// sans table de correspondance produit. Le service choisit lui-même le bon paquet (bureau / portable,
/// GeForce / RTX professionnelle, branche héritée pour les anciennes cartes). Paquet DCH « international »
/// téléchargé depuis us.download.nvidia.com, signé « NVIDIA Corporation ».
/// </summary>
public sealed partial class NvidiaSource : IDriverSource
{
    private const string LookupBase =
        "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

    /// <summary>
    /// Le paquet est une archive auto-extractible 7-Zip (module « 7zSfxMod » modifié par NVIDIA, élévation requise) :
    /// -y -gm2 masquent les dialogues, -nr extrait sans lancer setup.exe, -InstallPath fixe la destination.
    /// Vérifié sur le paquet 621.40 : en mode silencieux la destination doit être directement sous %TEMP% et ne pas
    /// exister (codes 8 et 12 sinon) — d'où <see cref="DriverCandidate.ExtractToTempFolder"/>.
    /// </summary>
    internal const string ExtractCommand = "\"%FILE%\" -y -gm2 -nr -InstallPath=\"%PACKAGEPATH%\"";

    /// <summary>
    /// Commande silencieuse documentée par NVIDIA (guide d'installation des pilotes Windows) :
    /// -s silencieux, -n sans redémarrage, « Display.Driver » = uniquement le pilote graphique
    /// (ni application NVIDIA / GeForce Experience, ni PhysX, ni télémétrie).
    /// </summary>
    internal const string SilentCommand = "\"%PACKAGEPATH%\\setup.exe\" -s -n Display.Driver -log:\"%LOGPATH%\\nvidia\" -loglevel:6";

    public string Name => "NVIDIA";
    public DriverSourceKind Kind => DriverSourceKind.Nvidia;

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseGpuVendorSources &&
        IsSupportedSystem(context.System) &&
        GpuAdapters.Find(context.Devices, GpuVendorIds.Nvidia).Count > 0;

    /// <summary>NVIDIA ne publie ses pilotes Windows que pour Windows 10/11 64 bits x64 (pas d'ARM64 ni de 32 bits).</summary>
    internal static bool IsSupportedSystem(SystemInfo system) =>
        system.Architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) && system.OsVersion.Major >= 10;

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var adapters = GpuAdapters.Find(context.Devices, GpuVendorIds.Nvidia);
        var branch = ParseBranch(context.Settings.NvidiaBranch);
        var found = new List<(NvidiaDriverInfo Driver, GpuAdapter Adapter)>();

        foreach (var group in adapters.GroupBy(a => a.DeviceId))
        {
            context.Report(Name, $"Recherche du pilote NVIDIA pour {group.First().Device.Name}...");
            NvidiaDriverInfo? driver = null;
            foreach (var studio in BranchAttempts(branch))
            {
                var json = await LookupAsync(BuildLookupUrl(group.Key, context.System, studio), ct).ConfigureAwait(false);
                driver = json is null ? null : ParseLookup(json);
                if (driver is not null) break;
            }
            if (driver is null)
            {
                Log.Info($"NVIDIA : aucun pilote proposé pour la carte DEV_{group.Key} (non prise en charge par le service).");
                continue;
            }
            if (!IsAcceptable(driver, context.System, out var why))
            {
                Log.Warn($"NVIDIA : pilote {driver.Version} écarté pour DEV_{group.Key} : {why}");
                continue;
            }
            found.AddRange(group.Select(a => (driver, a)));
        }

        var trust = new TrustPolicy(context.Settings);
        var results = new List<DriverCandidate>();
        foreach (var byPackage in found.GroupBy(f => f.Driver.DownloadUrl, StringComparer.OrdinalIgnoreCase))
        {
            var driver = byPackage.First().Driver;
            if (!trust.IsTrustedHost(new Uri(driver.DownloadUrl)))
            {
                Log.Warn($"NVIDIA : lien de téléchargement hors des domaines officiels autorisés, ignoré ({driver.DownloadUrl}).");
                continue;
            }
            var (size, md5) = await GetSizeAndMd5Async(driver.DownloadUrl, ct).ConfigureAwait(false);
            var candidate = BuildCandidate(driver, byPackage.Select(f => f.Adapter).ToList(), size, md5);
            if (candidate is not null) results.Add(candidate);
        }
        Log.Info($"NVIDIA : {results.Count} pilote(s) graphique(s) plus récent(s) que celui installé.");
        return results;
    }

    // ------------------------------------------------------------------ requête

    internal static NvidiaBranch ParseBranch(string? value) =>
        (value ?? "").Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant() switch
        {
            "gameready" or "grd" or "game" => NvidiaBranch.GameReady,
            _ => NvidiaBranch.Studio,
        };

    /// <summary>
    /// Branche Studio : on demande d'abord le pilote Studio, puis le pilote standard si la carte n'en a pas
    /// (cartes professionnelles RTX/Quadro : pilote « RTX Enterprise » ; anciennes GeForce : branche héritée).
    /// </summary>
    internal static IEnumerable<bool> BranchAttempts(NvidiaBranch branch) =>
        branch == NvidiaBranch.Studio ? new[] { true, false } : new[] { false };

    internal static string BuildLookupUrl(string deviceId, SystemInfo system, bool studio)
    {
        // osC/osB : version de Windows ; is6 : 64 bits ; lg 1033 : paquet « international » (toutes langues, dont le français)
        // servi par us.download.nvidia.com ; dch 1 : pilotes DCH (seul format publié pour Windows 10 1809+ et 11) ;
        // isCRD/upCRD : branche Studio (« Creator Ready Driver ») ; gIsB/prvMd 0 : jamais de bêta ni d'aperçu.
        var crd = studio ? "1" : "0";
        var query = "{" +
                    $"\"dIDa\":[\"{deviceId.ToUpperInvariant()}_{GpuVendorIds.Nvidia}\"]," +
                    $"\"osC\":\"{system.OsVersion.Major}.{system.OsVersion.Minor}\"," +
                    $"\"osB\":\"{system.OsBuild}\"," +
                    "\"is6\":\"1\",\"lg\":\"1033\",\"iLp\":\"0\",\"prvMd\":\"0\",\"gcV\":\"\",\"gIsB\":\"0\",\"dch\":\"1\"," +
                    $"\"upCRD\":\"{crd}\",\"isCRD\":\"{crd}\"" +
                    "}";
        return LookupBase + Uri.EscapeDataString(query);
    }

    /// <summary>Renvoie le JSON de réponse, ou null si le service ne connaît aucun pilote pour cette carte (HTTP 404).</summary>
    private static async Task<string?> LookupAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await Http.Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Taille (Content-Length) et empreinte MD5 du paquet, lues par une requête HEAD sur le CDN de NVIDIA.
    /// Le stockage Akamai NetStorage de NVIDIA expose le MD5 du fichier dans l'ETag (« "&lt;md5&gt;:&lt;horodatage&gt;" ») :
    /// c'est un contrôle d'intégrité du transfert, pas une signature — la confiance repose sur la signature Authenticode.
    /// </summary>
    private static async Task<(long? Size, string? Md5)> GetSizeAndMd5Async(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null);
            var server = string.Join(" ", response.Headers.Server.Select(s => s.ToString()));
            return (response.Content.Headers.ContentLength, Md5FromETag(response.Headers.ETag?.Tag, server));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Debug($"NVIDIA : taille du paquet inconnue ({ex.Message}).");
            return (null, null);
        }
    }

    [GeneratedRegex(@"^""?([0-9a-f]{32}):\d+(\.\d+)?""?$", RegexOptions.IgnoreCase)]
    private static partial Regex NetStorageETag();

    /// <summary>MD5 contenu dans un ETag Akamai NetStorage ; null pour tout autre format (aucune supposition).</summary>
    internal static string? Md5FromETag(string? etag, string? server)
    {
        if (etag is null || !(server ?? "").Contains("AkamaiNetStorage", StringComparison.OrdinalIgnoreCase)) return null;
        var m = NetStorageETag().Match(etag.Trim());
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    // ------------------------------------------------------------------ analyse

    /// <summary>Analyse la réponse du service ; null si aucun pilote n'est proposé.</summary>
    internal static NvidiaDriverInfo? ParseLookup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("criteria", out var criteria) &&
            criteria.TryGetProperty("overallState", out var state) &&
            !string.Equals(Text(state), "true", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!root.TryGetProperty("DriverAttributes", out var a) || a.ValueKind != JsonValueKind.Object) return null;

        var version = Text(a, "Version");
        if (string.IsNullOrWhiteSpace(version)) return null;

        // DownloadURLAdmin = paquet standard (us.download.nvidia.com). DownloadURL pointe en général vers la
        // variante « -g » réservée à l'application NVIDIA : n'est retenu qu'à défaut et si ce n'est pas cette variante.
        var url = Text(a, "DownloadURLAdmin");
        if (string.IsNullOrWhiteSpace(url))
        {
            var alt = Text(a, "DownloadURL");
            if (!string.IsNullOrWhiteSpace(alt) && !alt.EndsWith("-g.exe", StringComparison.OrdinalIgnoreCase)) url = alt;
        }
        if (string.IsNullOrWhiteSpace(url)) return null;

        DateTime? date = null;
        var rawDate = Text(a, "ReleaseDateTime");
        if (rawDate is { Length: >= 10 } &&
            DateTime.TryParseExact(rawDate[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            date = d;

        return new NvidiaDriverInfo(
            Id: Text(a, "ID") ?? version,
            Name: Text(a, "Name") ?? "NVIDIA Display Driver",
            Version: version.Trim(),
            Date: date,
            DownloadUrl: url.Trim(),
            DetailsUrl: Text(a, "DetailsURL"),
            IsStudio: Text(a, "IsCRD") == "1",
            IsBeta: Text(a, "IsBeta") == "1" || Text(a, "IsFeaturePreview") == "1");
    }

    /// <summary>Filtres de sécurité et de compatibilité (HTTPS, 64 bits, Windows 10/11, DCH, pas de bêta).</summary>
    internal static bool IsAcceptable(NvidiaDriverInfo driver, SystemInfo system, out string reason)
    {
        reason = "";
        if (driver.IsBeta)
        {
            reason = "version bêta ou aperçu";
            return false;
        }
        if (!Uri.TryCreate(driver.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "lien non HTTPS";
            return false;
        }
        var file = Path.GetFileName(uri.AbsolutePath).ToLowerInvariant();
        if (!file.EndsWith(".exe"))
        {
            reason = "paquet inattendu";
            return false;
        }
        if (!file.Contains("64bit"))
        {
            reason = "paquet non 64 bits";
            return false;
        }
        // Les noms de paquets annoncent les systèmes visés (« win10-win11 », « win11 »...).
        var mentionsOs = file.Contains("win10") || file.Contains("win11");
        if (mentionsOs && !file.Contains(system.IsWindows11 ? "win11" : "win10"))
        {
            reason = $"paquet non prévu pour {(system.IsWindows11 ? "Windows 11" : "Windows 10")}";
            return false;
        }
        if (!file.Contains("dch"))
        {
            reason = "paquet « Standard » (non DCH)";
            return false;
        }
        return true;
    }

    internal static DriverCandidate? BuildCandidate(NvidiaDriverInfo driver, IReadOnlyList<GpuAdapter> adapters, long? size, string? md5 = null)
    {
        var targets = adapters.Where(a => a.NeedsDriver || IsNewer(driver.Version, a.Device.DriverVersion) != false).ToList();
        if (targets.Count == 0) return null;

        var uri = new Uri(driver.DownloadUrl);
        var candidate = new DriverCandidate
        {
            Id = $"nvidia:{driver.Id}",
            Source = DriverSourceKind.Nvidia,
            PackageKind = DriverPackageKind.VendorInstaller,
            Title = FriendlyTitle(driver),
            Manufacturer = "NVIDIA",
            Category = "Carte graphique",
            Version = FormatVersion(driver.Version),
            Date = driver.Date,
            SizeBytes = size,
            DownloadUrl = driver.DownloadUrl,
            FileName = Path.GetFileName(uri.AbsolutePath),
            Md5 = md5,
            ExtractCommand = ExtractCommand,
            ExtractToTempFolder = true,
            SilentCommand = SilentCommand,
            // Codes documentés par NVIDIA : 0 = succès, 1 = succès, redémarrage nécessaire ; tout autre code = échec.
            SuccessExitCodes = new[] { 0 },
            RebootExitCodes = new[] { 1 },
            InfoUrl = driver.DetailsUrl,
            SourceItemId = driver.Id,
            Kind = CandidateKind.Update,
        };

        foreach (var adapter in targets)
        {
            var matches = OemHelpers.MatchDevices(new[] { adapter.Device }, $@"PCI\VEN_{adapter.VendorId}&DEV_{adapter.DeviceId}").ToList();
            OemHelpers.Attach(candidate, matches.Count > 0 ? matches : new List<(DeviceInfo, int)> { (adapter.Device, 0) });
            if (adapter.NeedsDriver) candidate.Kind = CandidateKind.MissingDriver;
            candidate.MatchedHardwareId ??= $@"PCI\VEN_{adapter.VendorId}&DEV_{adapter.DeviceId}";
        }
        return candidate;
    }

    private static string FriendlyTitle(NvidiaDriverInfo driver)
    {
        var name = driver.Name;
        var portable = driver.DownloadUrl.Contains("notebook", StringComparison.OrdinalIgnoreCase) &&
                       !driver.DownloadUrl.Contains("desktop", StringComparison.OrdinalIgnoreCase) ? " (portable)" : "";
        if (driver.IsStudio) return "Pilote graphique NVIDIA Studio" + portable;
        if (driver.DownloadUrl.Contains("/Quadro_Certified/", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RTX Driver", StringComparison.OrdinalIgnoreCase) || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
            return "Pilote graphique NVIDIA RTX Enterprise (cartes professionnelles)";
        if (name.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
            return "Pilote graphique NVIDIA – mise à jour de sécurité (branche héritée)" + portable;
        if (name.Contains("Game Ready", StringComparison.OrdinalIgnoreCase)) return "Pilote graphique NVIDIA Game Ready" + portable;
        return name + portable;
    }

    // ------------------------------------------------------------------ versions

    /// <summary>
    /// Version Windows d'un pilote NVIDIA → version commerciale : les 5 derniers chiffres des deux derniers blocs.
    /// ex. 31.0.15.6094 → 560.94 ; 32.0.16.1088 → 610.88 ; 30.0.14.7212 → 472.12. Null si le format ne correspond pas.
    /// </summary>
    public static string? FromWindowsVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var third) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var fourth) ||
            fourth > 9999)
            return null;
        var digits = $"{third}{fourth:D4}";
        if (digits.Length < 5) return null;
        digits = digits[^5..];
        return $"{int.Parse(digits[..3], CultureInfo.InvariantCulture)}.{digits[3..]}";
    }

    /// <summary>
    /// Fin de la version Windows correspondant à une version commerciale : 621.40 → « 16.2140 », 560.94 → « 15.6094 ».
    /// Les deux premiers blocs (ex. 32.0) dépendent du modèle de pilote Windows et ne se déduisent pas.
    /// </summary>
    public static string? ToWindowsSuffix(string? nvidiaVersion)
    {
        var v = ParseNvidia(nvidiaVersion);
        if (v is null) return null;
        var digits = $"{v.Value.Major:D3}{v.Value.Minor:D2}";
        if (digits.Length != 5) return null;
        return $"{10 + (digits[0] - '0')}.{digits[1..]}";
    }

    /// <summary>« 621.40 (Windows x.x.16.2140) » : format NVIDIA et correspondance Windows.</summary>
    public static string FormatVersion(string nvidiaVersion) =>
        ToWindowsSuffix(nvidiaVersion) is { } suffix ? $"{nvidiaVersion} (Windows x.x.{suffix})" : nvidiaVersion;

    /// <summary>true si <paramref name="nvidiaVersion"/> (ex. 621.40) est plus récente que la version Windows installée (ex. 32.0.16.1088).</summary>
    public static bool? IsNewer(string? nvidiaVersion, string? installedWindowsVersion)
    {
        var candidate = ParseNvidia(nvidiaVersion);
        var installed = ParseNvidia(FromWindowsVersion(installedWindowsVersion));
        if (candidate is null || installed is null) return null;
        return candidate.Value.Major != installed.Value.Major
            ? candidate.Value.Major > installed.Value.Major
            : candidate.Value.Minor > installed.Value.Minor;
    }

    private static (int Major, int Minor)? ParseNvidia(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Split('.');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            parts[1].Length != 2)
            return null;
        return (major, minor);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? Text(value) : null;

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => null,
    };
}
