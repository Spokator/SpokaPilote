using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Repository;

/// <summary>Un modèle de PC couvert par un pack : nom commercial + identifiants (SKU Dell, plateforme HP, type-machine Lenovo,
/// SKU et nom de modèle SMBIOS Surface).</summary>
public sealed record PackSystem(string Name, IReadOnlyList<string> Ids);

/// <summary>
/// Pack complet de pilotes publié par le constructeur pour le déploiement (SCCM / MDT) : Dell Command | Deploy
/// Driver Pack, HP Client Driver Pack, Lenovo SCCM Driver Pack, MSI « drivers and firmware » Microsoft Surface.
/// Une entrée = un fichier pour un Windows et une architecture.
/// </summary>
public sealed class OemDriverPack
{
    public required OemVendor Vendor { get; init; }
    /// <summary>Identifiant officiel : releaseID Dell (ex. 16KXP), SoftPaq HP (ex. sp114161), nom du fichier Lenovo ou Surface.</summary>
    public required string Id { get; init; }
    public List<PackSystem> Systems { get; init; } = new();
    /// <summary>« win10 » ou « win11 ».</summary>
    public required string Os { get; init; }
    /// <summary>« amd64 » ou « arm64 » (nomenclature des INF).</summary>
    public required string Architecture { get; init; }
    /// <summary>Versions de Windows déclarées (ex. 22H2, 24H2).</summary>
    public List<string> OsReleases { get; init; } = new();
    /// <summary>Build de Windows visé, quand le constructeur le publie (Surface : 22631, 26100...).</summary>
    public int? OsBuild { get; init; }
    public string? Version { get; init; }
    public DateTime? Date { get; init; }
    public required string Url { get; init; }
    /// <summary>Taille en octets (Lenovo ne la publie pas : complétée par une requête HEAD). Surface : taille annoncée par la page, vérifiée.</summary>
    public long? Size { get; set; }
    public string? Sha256 { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    /// <summary>Notes de version ; pour Surface, page officielle du Centre de téléchargement (relue au téléchargement).</summary>
    public string? InfoUrl { get; init; }

    public string FileName => Uri.UnescapeDataString(Path.GetFileName(new Uri(Url).AbsolutePath));
    /// <summary>cab, exe, zip ou msi (Surface).</summary>
    public string Format => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
    public string VendorName => OemDriverPacks.VendorName(Vendor);
    public string OsLabel => $"Windows {(Os == "win11" ? "11" : "10")} {(Architecture == "arm64" ? "ARM64" : "x64")}";
    /// <summary>Nom du sous-dossier dans le dépôt, cohérent avec l'export d'un poste de référence (ex. Win11-amd64).</summary>
    public string FolderOs => $"Win{(Os == "win11" ? "11" : "10")}-{Architecture}";
    public bool HasHash => Sha256 is not null || Sha1 is not null || Md5 is not null;
    /// <summary>Microsoft ne publie pas d'empreinte des MSI Surface : intégrité garantie par la signature Authenticode Microsoft et la taille.</summary>
    public bool RequiresMicrosoftSignature => Vendor == OemVendor.Microsoft;
    public string SizeLabel => Size is > 0 ? $"{Size.Value / 1073741824.0:0.00} Go".Replace('.', ',') : "taille inconnue";
    public string LatestRelease => OsReleases.OrderByDescending(OemDriverPacks.ReleaseRank).FirstOrDefault() ?? "";
}

/// <summary>Résultat d'une recherche : le pack retenu et les modèles de la recherche qu'il couvre.</summary>
public sealed record DriverPackMatch(OemDriverPack Pack, IReadOnlyList<string> MatchedModels)
{
    public string ModelLabel => MatchedModels.FirstOrDefault() ?? Pack.Systems.FirstOrDefault()?.Name ?? Pack.Id;
}

/// <summary>Critères de recherche : identifiant exact du poste courant, ou texte libre (« Latitude 7420 », « 20XW »).</summary>
public sealed class DriverPackQuery
{
    public OemVendor? Vendor { get; set; }
    public string? SystemId { get; set; }
    /// <summary>
    /// Nom de modèle SMBIOS du poste courant (Surface : « Surface Pro 9 »), utilisé seulement si <see cref="SystemId"/>
    /// ne correspond à aucun pack (variante régionale absente de la table, par exemple).
    /// </summary>
    public string? SystemModel { get; set; }
    public string? Text { get; set; }
    /// <summary>win10 / win11, null = les deux.</summary>
    public string? Os { get; set; }
    /// <summary>amd64 / arm64, null = toutes.</summary>
    public string? Architecture { get; set; }
    /// <summary>Toutes les versions au lieu de la plus récente par modèle / Windows / architecture.</summary>
    public bool AllVersions { get; set; }

    /// <summary>Identifie le poste courant comme le font les catalogues constructeurs.</summary>
    public static DriverPackQuery ForSystem(SystemInfo system)
    {
        var id = system.Vendor switch
        {
            OemVendor.Dell => system.SystemSku,
            OemVendor.HP => system.BaseBoardProduct,
            OemVendor.Lenovo => system.Model.Length >= 4 ? system.Model[..4] : null,
            // Surface : « System SKU » SMBIOS (ex. Surface_Pro_9_for_Business_2038), la référence Microsoft pour
            // distinguer deux appareils de même « System Model » (Surface Pro 9 Intel / 5G).
            OemVendor.Microsoft => system.SystemSku,
            _ => null,
        };
        // « Microsoft Corporation » fabrique aussi des machines virtuelles Hyper-V (« Virtual Machine ») : Surface uniquement.
        var isSurface = system.Vendor == OemVendor.Microsoft && system.Model.Contains("Surface", StringComparison.OrdinalIgnoreCase);
        var supported = system.Vendor is OemVendor.Dell or OemVendor.HP or OemVendor.Lenovo || isSurface;
        return new DriverPackQuery
        {
            Vendor = supported ? system.Vendor : null,
            SystemId = !supported || string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
            SystemModel = isSurface ? system.Model.Trim() : null,
            // SKU illisible : le « System Model » SMBIOS (ex. « Surface Laptop Studio 2 ») est recherché tel quel.
            Text = isSurface && string.IsNullOrWhiteSpace(id) ? system.Model.Trim() : null,
            Architecture = system.Architecture == "arm64" ? "arm64" : "amd64",
        };
    }

    public string Describe() => SystemId is not null ? $"identifiant {SystemId}" : $"« {Text} »";
}

/// <summary>
/// Catalogues officiels des packs de pilotes « entreprise » des constructeurs :
/// Dell  : downloads.dell.com/catalog/DriverPackCatalog.cab (signé Dell, hachages MD5/SHA-1/SHA-256) ;
/// HP    : hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab (signé HP, SHA-256) ;
/// Lenovo: download.lenovo.com/cdrt/td/catalogv2.xml (catalogue des outils de déploiement Lenovo, SHA-256) ;
/// Surface : table embarquée modèle → page du Centre de téléchargement Microsoft, liens relus sur la page
/// (voir <see cref="SurfaceDriverPacks"/>).
/// Les catalogues et les pages sont mis en cache 24 h.
/// </summary>
public static class OemDriverPacks
{
    public const string DellCatalogUrl = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";
    public const string HpCatalogUrl = "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab";
    public const string LenovoCatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";

    public static readonly OemVendor[] SupportedVendors = { OemVendor.Dell, OemVendor.HP, OemVendor.Lenovo, OemVendor.Microsoft };

    public static string VendorName(OemVendor vendor) => vendor switch
    {
        OemVendor.Dell => "Dell",
        OemVendor.HP => "HP",
        OemVendor.Lenovo => "Lenovo",
        OemVendor.Microsoft => "Microsoft",
        _ => vendor.ToString(),
    };

    // ------------------------------------------------------------------ chargement des catalogues

    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static readonly Dictionary<OemVendor, (DateTime Stamp, List<OemDriverPack> Packs)> Loaded = new();

    public static async Task<List<OemDriverPack>> LoadAsync(OemVendor vendor, Settings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await LoadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Loaded.TryGetValue(vendor, out var cached) && DateTime.UtcNow - cached.Stamp < TimeSpan.FromHours(1))
                return cached.Packs;

            var trust = new TrustPolicy(settings);
            List<OemDriverPack> packs;
            switch (vendor)
            {
                case OemVendor.Dell:
                {
                    progress?.Report("Catalogue des packs Dell (DriverPackCatalog)...");
                    var cab = await Http.GetCachedFileAsync(DellCatalogUrl, "dell-DriverPackCatalog.cab", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
                    VerifyCatalogSignature(cab, trust);
                    var xml = await Archive.ExtractSingleFileCabAsync(cab, "dell-DriverPackCatalog", ct).ConfigureAwait(false);
                    packs = await Task.Run(() => ParseDell(xml), ct).ConfigureAwait(false);
                    break;
                }
                case OemVendor.HP:
                {
                    progress?.Report("Catalogue des packs HP (HPClientDriverPackCatalog)...");
                    var cab = await Http.GetCachedFileAsync(HpCatalogUrl, "hp-HPClientDriverPackCatalog.cab", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
                    VerifyCatalogSignature(cab, trust);
                    var xml = await Archive.ExtractSingleFileCabAsync(cab, "hp-HPClientDriverPackCatalog", ct).ConfigureAwait(false);
                    packs = await Task.Run(() => ParseHp(xml), ct).ConfigureAwait(false);
                    break;
                }
                case OemVendor.Lenovo:
                {
                    progress?.Report("Catalogue des packs Lenovo (catalogv2)...");
                    // XML non signé : toujours lu directement chez Lenovo (jamais relayé par la console, voir
                    // ConsoleCache.CanRelayCatalog) ; nom de cache propre à la lecture directe.
                    var xml = await Http.GetCachedFileAsync(LenovoCatalogUrl, "lenovo-direct-catalogv2.xml", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
                    packs = await Task.Run(() => ParseLenovo(xml), ct).ConfigureAwait(false);
                    break;
                }
                case OemVendor.Microsoft:
                    packs = await SurfaceDriverPacks.LoadAsync(SurfaceDriverPacks.Catalog.Models, refresh: false, progress, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Aucun catalogue de packs pour {vendor}.");
            }
            Log.Info($"Catalogue des packs {VendorName(vendor)} : {packs.Count} pack(s) Windows 10/11.");
            Loaded[vendor] = (DateTime.UtcNow, packs);
            return packs;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    /// <summary>Les catalogues Dell et HP sont des .cab signés par le constructeur : la signature doit être valide.</summary>
    private static void VerifyCatalogSignature(string cabPath, TrustPolicy trust)
    {
        var sig = SignatureVerifier.Verify(cabPath);
        if (!sig.IsValid || !trust.IsTrustedPublisher(sig.Signer))
        {
            File.Delete(cabPath);
            throw new SecurityException($"Catalogue refusé ({Path.GetFileName(cabPath)}) : {(sig.IsValid ? $"éditeur non approuvé ({sig.Signer})" : sig.Detail)}.");
        }
        Log.Debug($"Catalogue {Path.GetFileName(cabPath)} : {sig}");
    }

    // ------------------------------------------------------------------ recherche

    /// <summary>Recherche dans les catalogues nécessaires (déduits de la requête) puis filtre.</summary>
    public static async Task<List<DriverPackMatch>> SearchAsync(DriverPackQuery query, Settings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var vendors = query.Vendor is { } v ? new[] { v } : InferVendors(query.Text);
        var all = new List<OemDriverPack>();
        foreach (var vendor in vendors)
        {
            try
            {
                // Surface : une page par modèle ; seules celles que la requête peut viser sont lues.
                all.AddRange(vendor == OemVendor.Microsoft
                    ? await SurfaceDriverPacks.SearchAsync(query, progress, ct).ConfigureAwait(false)
                    : await LoadAsync(vendor, settings, progress, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && vendors.Length > 1)
            {
                Log.Warn($"Catalogue des packs {VendorName(vendor)} indisponible : {ex.Message}");
            }
        }
        var matches = Find(all, query);
        await FillMissingSizesAsync(matches.Select(m => m.Pack), ct).ConfigureAwait(false);
        return matches;
    }

    /// <summary>Taille des packs dont le catalogue ne la donne pas (Lenovo) : requête HEAD sur le serveur officiel.</summary>
    public static async Task FillMissingSizesAsync(IEnumerable<OemDriverPack> packs, CancellationToken ct = default)
    {
        var missing = packs.Where(p => p.Size is null).Distinct().Take(20).ToList();
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (pack, token) =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Head, pack.Url);
                using var response = await Http.Client.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0 and var length) pack.Size = length;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                Log.Debug($"Taille inconnue pour {pack.Url} : {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Marques déduites du texte (« Latitude » → Dell, « EliteBook » → HP, « ThinkPad » → Lenovo, « Surface » → Microsoft), sinon toutes.</summary>
    internal static OemVendor[] InferVendors(string? text)
    {
        var tokens = Tokens(text ?? "");
        if (tokens.Count == 0) return SupportedVendors;
        var found = new HashSet<OemVendor>();
        foreach (var t in tokens)
        {
            if (t is "dell" or "latitude" or "optiplex" or "precision" or "vostro" or "xps" or "alienware" or "inspiron") found.Add(OemVendor.Dell);
            else if (t is "hp" or "elitebook" or "probook" or "zbook" or "elitedesk" or "prodesk" or "elite" or "eliteone" or "proone" or "z2" or "engage") found.Add(OemVendor.HP);
            else if (t is "lenovo" or "thinkpad" or "thinkcentre" or "thinkstation" or "thinkbook") found.Add(OemVendor.Lenovo);
            else if (t is "microsoft" or "surface") found.Add(OemVendor.Microsoft);
        }
        return found.Count > 0 ? found.ToArray() : SupportedVendors;
    }

    /// <summary>
    /// Un modèle correspond-il à la requête ? Identifiant exact du poste (ou, avec <paramref name="useModelFallback"/>,
    /// son nom de modèle SMBIOS), identifiant saisi en entier (« 20WM »), sinon tous les mots du nom.
    /// </summary>
    internal static bool SystemMatches(PackSystem system, DriverPackQuery query, bool useModelFallback)
    {
        if (query.SystemId is not null)
        {
            var id = useModelFallback ? query.SystemModel : query.SystemId;
            return id is not null && system.Ids.Any(i => i.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        var textId = query.Text?.Trim();
        if (textId is { Length: >= 4 } && system.Ids.Any(i => i.Equals(textId, StringComparison.OrdinalIgnoreCase)))
            return true;
        var words = Tokens(query.Text ?? "").Where(t => !IsVendorWord(t)).ToList();
        var nameTokens = Tokens(system.Name);
        return words.Count > 0 && words.All(w => nameTokens.Contains(w));
    }

    /// <summary>Filtre les packs (identifiant exact ou mots du nom), puis ne garde que le plus récent par modèle / Windows / architecture.</summary>
    internal static List<DriverPackMatch> Find(IEnumerable<OemDriverPack> packs, DriverPackQuery query)
    {
        var candidates = packs.Where(pack =>
            (query.Vendor is not { } vendor || pack.Vendor == vendor) &&
            (query.Os is not { } os || pack.Os.Equals(os, StringComparison.OrdinalIgnoreCase)) &&
            (query.Architecture is not { } arch || pack.Architecture.Equals(arch, StringComparison.OrdinalIgnoreCase))).ToList();

        // (pack, modèle reconnu) : la clé de regroupement est le modèle, pas le fichier (un pack HP couvre plusieurs modèles).
        List<(OemDriverPack Pack, string Model)> Hits(bool fallback) => candidates
            .SelectMany(pack => pack.Systems.Where(system => SystemMatches(system, query, fallback)).Select(system => (pack, system.Name)))
            .ToList();
        var hits = Hits(fallback: false);
        // SKU absent de la table (variante régionale...) : repli sur le nom de modèle SMBIOS du poste.
        if (hits.Count == 0 && query is { SystemId: not null, SystemModel: not null }) hits = Hits(fallback: true);

        IEnumerable<(OemDriverPack Pack, string Model)> selected = hits;
        if (!query.AllVersions)
        {
            selected = hits
                .GroupBy(h => (h.Pack.Vendor, Model: h.Model.ToLowerInvariant(), h.Pack.Os, h.Pack.Architecture))
                .Select(g => g.OrderByDescending(h => h.Pack.Date ?? DateTime.MinValue)
                              .ThenByDescending(h => ReleaseRank(h.Pack.LatestRelease))
                              .First());
        }

        return selected
            .GroupBy(h => h.Pack)
            .Select(g => new DriverPackMatch(g.Key, g.Select(h => h.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(m => m.Pack.Vendor)
            .ThenBy(m => m.ModelLabel, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.Pack.Os)
            .ThenBy(m => m.Pack.Architecture)
            .ThenByDescending(m => m.Pack.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static bool IsVendorWord(string token) => token is "dell" or "hp" or "lenovo" or "microsoft";

    internal static List<string> Tokens(string text) =>
        Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0).ToList();

    /// <summary>Ordre des versions de Windows : 1909 &lt; 2004 &lt; 20H2 &lt; 21H1 &lt; ... &lt; 24H2.</summary>
    public static int ReleaseRank(string? release)
    {
        if (string.IsNullOrWhiteSpace(release)) return 0;
        var r = release.Trim().ToUpperInvariant();
        var m = Regex.Match(r, @"^(\d{2})H(\d)$");
        if (m.Success) return (2000 + int.Parse(m.Groups[1].Value)) * 10 + int.Parse(m.Groups[2].Value);
        m = Regex.Match(r, @"^(\d{2})(\d{2})$");
        if (m.Success) return (2000 + int.Parse(m.Groups[1].Value)) * 10 + (int.Parse(m.Groups[2].Value) <= 6 ? 1 : 2);
        return 0;
    }

    // ------------------------------------------------------------------ analyse des catalogues

    private static XmlReader OpenXml(string path) =>
        XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });

    /// <summary>DriverPackCatalog Dell : &lt;DriverPackage&gt; avec systèmes (systemID), OS (osCode/osArch) et hachages.</summary>
    internal static List<OemDriverPack> ParseDell(string xmlPath)
    {
        XDocument doc;
        using (var reader = OpenXml(xmlPath)) doc = XDocument.Load(reader);
        var root = doc.Root!;
        XNamespace ns = root.Name.Namespace;
        var baseLocation = root.Attribute("baseLocation")?.Value is { Length: > 0 } b ? b : "downloads.dell.com";

        var result = new List<OemDriverPack>();
        foreach (var dp in root.Elements(ns + "DriverPackage"))
        {
            if (!string.Equals(dp.Attribute("type")?.Value, "win", StringComparison.OrdinalIgnoreCase)) continue; // packs WinPE exclus
            var path = dp.Attribute("path")?.Value?.Trim();
            if (string.IsNullOrEmpty(path)) continue;

            var systems = dp.Element(ns + "SupportedSystems")?.Descendants(ns + "Model")
                .Select(m => (Name: (m.Attribute("name")?.Value ?? m.Element(ns + "Display")?.Value ?? "").Trim(), Id: m.Attribute("systemID")?.Value?.Trim() ?? ""))
                .Where(m => m.Name.Length > 0)
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PackSystem(g.Key, g.Select(x => x.Id).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList() ?? new();
            if (systems.Count == 0) continue;

            string? Hash(string algorithm) => dp.Element(ns + "Cryptography")?.Elements(ns + "Hash")
                .FirstOrDefault(h => string.Equals(h.Attribute("algorithm")?.Value, algorithm, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

            var url = $"https://{baseLocation.Trim('/')}/{path.TrimStart('/').Replace('\\', '/')}";
            var releaseId = dp.Attribute("releaseID")?.Value;
            var targets = dp.Element(ns + "SupportedOperatingSystems")?.Elements(ns + "OperatingSystem")
                .Select(os => (Os: os.Attribute("osCode")?.Value switch { "Windows11" => "win11", "Windows10" => "win10", _ => null },
                               Arch: os.Attribute("osArch")?.Value?.ToLowerInvariant() switch { "x64" => "amd64", "arm64" => "arm64", _ => null }))
                .Where(t => t.Os is not null && t.Arch is not null)
                .Distinct()
                .ToList() ?? new();

            foreach (var (os, arch) in targets)
            {
                result.Add(new OemDriverPack
                {
                    Vendor = OemVendor.Dell,
                    Id = releaseId ?? Path.GetFileNameWithoutExtension(path),
                    Systems = systems,
                    Os = os!,
                    Architecture = arch!,
                    Version = dp.Attribute("dellVersion")?.Value ?? dp.Attribute("vendorVersion")?.Value,
                    Date = ParseDate(dp.Attribute("dateTime")?.Value),
                    Url = url,
                    Size = long.TryParse(dp.Attribute("size")?.Value, out var size) ? size : null,
                    Sha256 = Hash("SHA256"),
                    Sha1 = Hash("SHA1"),
                    Md5 = Hash("MD5") ?? dp.Attribute("hashMD5")?.Value,
                    InfoUrl = dp.Element(ns + "ImportantInfo")?.Attribute("URL")?.Value is { } info ? TrustPolicy.NormalizeUrl(info).ToString() : null,
                });
            }
        }
        return result;
    }

    /// <summary>HPClientDriverPackCatalog : &lt;SoftPaqList&gt; (fichiers) + &lt;ProductOSDriverPackList&gt; (plateforme × Windows → SoftPaq).</summary>
    internal static List<OemDriverPack> ParseHp(string xmlPath)
    {
        XDocument doc;
        using (var reader = OpenXml(xmlPath)) doc = XDocument.Load(reader);
        var catalog = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "HPClientDriverPackCatalog");
        if (catalog is null) return new();
        XNamespace ns = catalog.Name.Namespace;
        string? V(XElement e, string name) => e.Element(ns + name)?.Value?.Trim() is { Length: > 0 } s ? s : null;

        var softpaqs = (catalog.Element(ns + "SoftPaqList")?.Elements(ns + "SoftPaq") ?? Enumerable.Empty<XElement>())
            .Where(sp => V(sp, "Id") is not null && V(sp, "Url") is not null)
            .GroupBy(sp => V(sp, "Id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Regroupement par (SoftPaq, Windows) : un même pack couvre souvent plusieurs modèles et versions de Windows.
        var rows = new Dictionary<(string Sp, string Os), (List<(string Name, List<string> Ids)> Systems, HashSet<string> Releases)>();
        foreach (var row in catalog.Element(ns + "ProductOSDriverPackList")?.Elements(ns + "ProductOSDriverPack") ?? Enumerable.Empty<XElement>())
        {
            var spId = V(row, "SoftPaqId");
            var osName = V(row, "OSName") ?? "";
            if (spId is null || !softpaqs.ContainsKey(spId)) continue;
            // « Windows 11 64-bit, 24H2 » ; les éditions IoT / LTSC ont leurs propres lignes, ignorées ici.
            var m = Regex.Match(osName, @"^Windows (10|11) 64-bit,\s*(\w+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var key = (spId, m.Groups[1].Value == "11" ? "win11" : "win10");
            if (!rows.TryGetValue(key, out var entry)) rows[key] = entry = (new(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            entry.Releases.Add(m.Groups[2].Value.ToUpperInvariant());
            var ids = (V(row, "SystemId") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(i => i.ToUpperInvariant()).ToList();
            var name = V(row, "SystemName") ?? string.Join(",", ids);
            var existing = entry.Systems.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing.Name is null) entry.Systems.Add((name, ids));
            else existing.Ids.AddRange(ids.Where(i => !existing.Ids.Contains(i, StringComparer.OrdinalIgnoreCase)));
        }

        var result = new List<OemDriverPack>();
        foreach (var ((spId, os), entry) in rows)
        {
            var sp = softpaqs[spId];
            result.Add(new OemDriverPack
            {
                Vendor = OemVendor.HP,
                Id = spId,
                Systems = entry.Systems.Select(s => new PackSystem(s.Name, s.Ids)).ToList(),
                Os = os,
                Architecture = "amd64",
                OsReleases = entry.Releases.OrderBy(ReleaseRank).ToList(),
                Version = V(sp, "Version"),
                Date = DateTime.TryParse(V(sp, "DateReleased"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Url = TrustPolicy.NormalizeUrl(V(sp, "Url")!).ToString(),
                Size = long.TryParse(V(sp, "Size"), out var size) && size > 0 ? size : null,
                Sha256 = V(sp, "SHA256"),
                Md5 = V(sp, "MD5"),
                InfoUrl = V(sp, "ReleaseNotesUrl") is { } notes ? TrustPolicy.NormalizeUrl(notes).ToString() : null,
            });
        }
        return result;
    }

    /// <summary>catalogv2.xml Lenovo : &lt;Model&gt; (types-machine) avec un &lt;SCCM os= version=&gt; par pack.</summary>
    internal static List<OemDriverPack> ParseLenovo(string xmlPath)
    {
        XDocument doc;
        using (var reader = OpenXml(xmlPath)) doc = XDocument.Load(reader);
        var result = new Dictionary<(string Url, string Os), OemDriverPack>();
        foreach (var model in doc.Root?.Elements("Model") ?? Enumerable.Empty<XElement>())
        {
            var name = model.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var types = model.Element("Types")?.Elements("Type").Select(t => t.Value.Trim().ToUpperInvariant()).Where(t => t.Length > 0).ToList() ?? new();
            var system = new PackSystem(name, types);

            foreach (var sccm in model.Elements("SCCM"))
            {
                var os = sccm.Attribute("os")?.Value?.Trim().ToLowerInvariant() switch { "win11" => "win11", "win10" => "win10", _ => null };
                var url = sccm.Value.Trim();
                if (os is null || url.Length == 0) continue; // « win11 IoT » et autres variantes exclues
                var normalized = TrustPolicy.NormalizeUrl(url).ToString();
                var release = sccm.Attribute("version")?.Value?.Trim().ToUpperInvariant();
                var crc = sccm.Attribute("crc")?.Value?.Trim();
                var date = ParseDate(sccm.Attribute("date")?.Value);

                if (result.TryGetValue((normalized, os), out var existing))
                {
                    if (!existing.Systems.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) existing.Systems.Add(system);
                    if (release is { Length: > 0 } && release != "*" && !existing.OsReleases.Contains(release)) existing.OsReleases.Add(release);
                    continue;
                }
                result[(normalized, os)] = new OemDriverPack
                {
                    Vendor = OemVendor.Lenovo,
                    Id = Path.GetFileNameWithoutExtension(new Uri(normalized).AbsolutePath),
                    Systems = new List<PackSystem> { system },
                    Os = os,
                    // Les packs SCCM Lenovo sont des packs x64 (l'attribut arch du modèle désigne le processeur Intel/AMD).
                    Architecture = "amd64",
                    OsReleases = release is { Length: > 0 } && release != "*" ? new List<string> { release } : new List<string>(),
                    Date = date,
                    Url = normalized,
                    Sha256 = crc is { Length: 64 } ? crc : null,
                    Md5 = sccm.Attribute("md5")?.Value?.Trim() is { Length: 32 } md5 ? md5 : null,
                };
            }
        }
        return result.Values.ToList();
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
