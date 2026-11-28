namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Politique de confiance : SpokaPilote ne télécharge que depuis des domaines officiels (Microsoft et constructeurs)
/// et n'exécute que des fichiers signés par des éditeurs connus. C'est le cœur de la différence avec les
/// sites « agrégateurs » de pilotes.
/// </summary>
public sealed class TrustPolicy
{
    /// <summary>Domaines officiels de téléchargement (liste partagée avec la console centrale : <see cref="OfficialHosts"/>).</summary>
    public static readonly string[] BuiltInHosts = OfficialHosts.Download;

    /// <summary>
    /// Éditeurs approuvés : organisation (O=) du certificat de signature, ou son CN= s'il n'a pas d'O=. La comparaison est
    /// EXACTE après normalisation (<see cref="NormalizePublisher"/> : casse, ponctuation, espaces, « Inc » = « Incorporated »,
    /// « Corp » = « Corporation », « Ltd » = « Limited », « Co » = « Company ») ; la forme juridique compte : « Canon Inc. »
    /// est approuvé, ni « Canon Ltd » ni « Canonical Ltd. » ne le sont. Noms relevés sur les certificats réels des éditeurs.
    /// </summary>
    public static readonly string[] BuiltInPublishers =
    {
        // Microsoft : pilotes WHQL (CN « Microsoft Windows Hardware Compatibility Publisher »), Windows, Surface, .NET...
        "Microsoft Corporation",
        // Constructeurs PC
        "Dell Inc",
        "Dell Technologies Inc.",
        "HP Inc.",
        "Hewlett-Packard Company",
        "Lenovo",
        "Lenovo (Beijing) Limited",
        "Lenovo (United States) Inc.",
        // Fondeurs et composants
        "Intel Corporation",
        "Advanced Micro Devices, Inc.",
        "NVIDIA Corporation",
        "Realtek Semiconductor Corp.",
        "Synaptics Incorporated",
        "Qualcomm Incorporated",
        "Qualcomm Technologies, Inc.",
        "Qualcomm Atheros",
        "MediaTek Inc.",
        "Broadcom Corporation",
        "Broadcom Inc.",
        "Logitech Inc.",
        "Logitech Europe S.A.",
        // Imprimantes
        "KYOCERA Document Solutions Inc.",
        "Kyocera Corporation",
        "Canon Inc.",
        "Ricoh Company, Ltd.",
        "Xerox Corporation",
        "KONICA MINOLTA, INC.",
        "Brother Industries, Ltd.",
        "SEIKO EPSON CORPORATION",
        "Sharp Corporation",
        "Lexmark International, Inc.",
        "Toshiba Corporation",
        "Toshiba Tec Corporation",
        "Zebra Technologies Corporation",
    };

    /// <summary>
    /// Éditeur attendu des fichiers de chaque domaine officiel. Sert quand la signature est la SEULE vérification possible
    /// d'un fichier qui ne vient pas directement de la source (copie de la console sans empreinte publiée, catalogue .cab
    /// relayé) : un fichier de us.download.nvidia.com doit être signé par NVIDIA, pas seulement par « un » éditeur approuvé.
    /// </summary>
    private static readonly (string[] Hosts, string[] Publishers)[] VendorHosts =
    [
        (["downloads.dell.com", "dl.dell.com"], ["Dell Inc", "Dell Technologies Inc."]),
        (["ftp.hp.com", "ftp.ext.hp.com", "hpia.hpcloud.hp.com"], ["HP Inc.", "Hewlett-Packard Company"]),
        (["download.lenovo.com"], ["Lenovo", "Lenovo (Beijing) Limited", "Lenovo (United States) Inc."]),
        (["downloadmirror.intel.com"], ["Intel Corporation"]),
        (["drivers.amd.com"], ["Advanced Micro Devices, Inc."]),
        (["us.download.nvidia.com", "international.download.nvidia.com"], ["NVIDIA Corporation"]),
        (["catalog.update.microsoft.com", "www.catalog.update.microsoft.com", "catalog.s.download.windowsupdate.com",
          "download.windowsupdate.com", "*.download.windowsupdate.com", "download.microsoft.com", "*.download.microsoft.com"],
         ["Microsoft Corporation"]),
    ];

    /// <summary>
    /// Pages d'information officielles des constructeurs (fiches de téléchargement, notes de version) : affichées comme
    /// liens dans les rapports, jamais utilisées pour télécharger un paquet.
    /// </summary>
    public static readonly string[] OfficialInfoHosts =
    {
        "www.dell.com",
        "support.hp.com",
        "pcsupport.lenovo.com",
        "support.lenovo.com",
    };

    /// <summary>
    /// Lien affichable dans un rapport : HTTPS vers un domaine officiel (hôtes de téléchargement intégrés ou pages
    /// d'information des constructeurs). Renvoie l'URL normalisée, ou null si elle n'est pas officielle.
    /// </summary>
    public static string? OfficialLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host.ToLowerInvariant();
        var builtIn = new TrustPolicy(new Settings());
        return builtIn.IsTrustedHost(uri) || OfficialInfoHosts.Contains(host, StringComparer.Ordinal) ? uri.ToString() : null;
    }

    private readonly List<string> _hosts;
    private readonly HashSet<string> _publishers;

    public TrustPolicy(Settings settings)
    {
        // Une entrée vide ou blanche des réglages est ignorée : elle ne doit jamais approuver « tout le monde ».
        _hosts = BuiltInHosts.Concat(settings.ExtraTrustedHosts).Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant()).Where(h => h is not ("*" or "*.")).ToList();
        _publishers = BuiltInPublishers.Concat(settings.ExtraTrustedPublishers).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePublisher).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    public bool IsTrustedHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        foreach (var pattern in _hosts)
        {
            if (pattern.StartsWith("*."))
            {
                if (host.EndsWith(pattern[1..], StringComparison.Ordinal)) return true;
            }
            else if (host == pattern)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Le signataire (sujet du certificat, ex. « CN=Dell Inc, O=Dell Inc, ... ») est-il un éditeur approuvé ? Comparaison
    /// exacte de son organisation (O=, ou CN= sans O=) avec la liste, après normalisation.
    /// </summary>
    public bool IsTrustedPublisher(string? certificateSubject) =>
        PublisherName(certificateSubject) is { } name && _publishers.Contains(NormalizePublisher(name));

    /// <summary>Éditeurs attendus des fichiers de ce domaine officiel, ou null (domaine sans éditeur attendu connu).</summary>
    public static IReadOnlyList<string>? ExpectedPublishers(Uri uri)
    {
        foreach (var (hosts, publishers) in VendorHosts)
            if (OfficialHosts.Matches(uri.Host, hosts)) return publishers;
        return null;
    }

    /// <summary>
    /// Le signataire est-il l'éditeur attendu pour ce domaine (NVIDIA pour us.download.nvidia.com, Dell pour
    /// downloads.dell.com...) ? Faux pour un domaine sans éditeur attendu connu.
    /// </summary>
    public static bool IsExpectedPublisher(Uri uri, string? certificateSubject) =>
        ExpectedPublishers(uri) is { } expected && PublisherName(certificateSubject) is { } name &&
        expected.Any(p => NormalizePublisher(p) == NormalizePublisher(name));

    /// <summary>Deux sujets de certificat désignent-ils le même éditeur (même organisation après normalisation) ?</summary>
    public static bool SamePublisher(string? subjectA, string? subjectB) =>
        PublisherName(subjectA) is { } a && PublisherName(subjectB) is { } b && NormalizePublisher(a) == NormalizePublisher(b);

    /// <summary>
    /// Organisation du signataire : la valeur O= (une seule), ou à défaut CN=. null si absente, vide ou ambiguë (plusieurs O=).
    /// </summary>
    internal static string? PublisherName(string? certificateSubject)
    {
        if (string.IsNullOrWhiteSpace(certificateSubject)) return null;
        var orgs = RdnValues(certificateSubject, "O").ToList();
        if (orgs.Count > 1) return null;
        var name = orgs.Count == 1 ? orgs[0] : RdnValues(certificateSubject, "CN").FirstOrDefault();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Formes juridiques écrites de deux façons : abrégée et complète désignent la même société (« Synaptics Inc. » =
    /// « Synaptics Incorporated »). Toute autre forme (GmbH, S.A., LLC...) reste telle quelle.
    /// </summary>
    private static readonly Dictionary<string, string> LegalFormSpellings = new(StringComparer.Ordinal)
    {
        ["incorporated"] = "inc",
        ["corporation"] = "corp",
        ["limited"] = "ltd",
        ["company"] = "co",
    };

    /// <summary>
    /// Nom d'éditeur normalisé pour la comparaison : forme de compatibilité Unicode, minuscules, points retirés (« Inc. » =
    /// « Inc », « S.A. » = « SA »), virgules, points-virgules et guillemets remplacés par des espaces, espaces fusionnés,
    /// formes juridiques complètes ramenées à leur abréviation (Incorporated → inc, Corporation → corp, Limited → ltd,
    /// Company → co). La forme juridique est GARDÉE : « Canon Ltd » n'est pas « Canon Inc. », « NVIDIA GmbH » n'est pas
    /// « NVIDIA Corporation », « HP Co » n'est pas « HP Inc. » (sociétés distinctes, certificats distincts) ; et le nom
    /// lui-même n'est jamais tronqué : « canonical » reste différent de « canon ».
    /// </summary>
    internal static string NormalizePublisher(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (c == '.') continue;
            text.Append(c is ',' or ';' or '"' || char.IsWhiteSpace(c) || char.IsControl(c) ? ' ' : c);
        }
        var tokens = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => LegalFormSpellings.GetValueOrDefault(t, t));
        return string.Join(' ', tokens);
    }

    /// <summary>Force HTTPS (certains catalogues constructeurs référencent encore des chemins sans schéma ou en http).</summary>
    public static Uri NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[7..];
        else if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.TrimStart('/');
        return new Uri(url);
    }

    internal static string? ExtractRdn(string subject, string key) => RdnValues(subject, key).FirstOrDefault();

    private static IEnumerable<string> RdnValues(string subject, string key)
    {
        foreach (var part in SplitDn(subject))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            if (part[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                yield return part[(idx + 1)..].Trim().Trim('"');
        }
    }

    private static IEnumerable<string> SplitDn(string dn)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in dn)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == ',' && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
