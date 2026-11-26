namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Politique de confiance : SpokaPilote ne télécharge que depuis des domaines officiels (Microsoft et constructeurs)
/// et n'exécute que des fichiers signés par des éditeurs connus. C'est le cœur de la différence avec les
/// sites « agrégateurs » de pilotes.
/// </summary>
public sealed class TrustPolicy
{
    public static readonly string[] BuiltInHosts =
    {
        // Microsoft (Windows Update / Microsoft Update Catalog)
        "catalog.update.microsoft.com",
        "www.catalog.update.microsoft.com",
        "catalog.s.download.windowsupdate.com",
        "download.windowsupdate.com",
        "*.download.windowsupdate.com",
        "download.microsoft.com",
        "*.download.microsoft.com",
        // Constructeurs PC
        "downloads.dell.com",
        "dl.dell.com",
        "ftp.hp.com",
        "ftp.ext.hp.com",
        "hpia.hpcloud.hp.com",
        "download.lenovo.com",
        // Fondeurs
        "downloadmirror.intel.com",
        "drivers.amd.com",
        // NVIDIA : miroir principal servi par son service de pilotes (DownloadURLAdmin) et miroir international
        // hébergeant les mêmes paquets signés « NVIDIA Corporation ».
        "us.download.nvidia.com",
        "international.download.nvidia.com",
    };

    public static readonly string[] BuiltInPublishers =
    {
        "Microsoft Windows Hardware Compatibility Publisher",
        "Microsoft Windows",
        "Microsoft Corporation",
        "Dell Inc",
        "Dell Technologies",
        "HP Inc",
        "Hewlett-Packard",
        "HP Inc.",
        "Lenovo",
        "Intel Corporation",
        "Advanced Micro Devices",
        "NVIDIA Corporation",
        "Realtek Semiconductor",
        "Synaptics",
        "Qualcomm",
        "MediaTek",
        "Broadcom",
        "Logitech",
        "Kyocera",
        "Canon",
        "Ricoh",
        "Xerox",
        "KONICA MINOLTA",
        "Brother Industries",
        "SEIKO EPSON",
        "Sharp",
        "Lexmark",
        "Toshiba",
        "Zebra Technologies",
    };

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
    private readonly List<string> _publishers;

    public TrustPolicy(Settings settings)
    {
        _hosts = BuiltInHosts.Concat(settings.ExtraTrustedHosts).Select(h => h.Trim().ToLowerInvariant()).ToList();
        _publishers = BuiltInPublishers.Concat(settings.ExtraTrustedPublishers).ToList();
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

    /// <summary>Le sujet du certificat (ex. « CN=Dell Inc, O=Dell Inc, ... ») appartient-il à un éditeur autorisé ?</summary>
    public bool IsTrustedPublisher(string? certificateSubject)
    {
        if (string.IsNullOrWhiteSpace(certificateSubject)) return false;
        var org = ExtractRdn(certificateSubject, "O") ?? ExtractRdn(certificateSubject, "CN") ?? certificateSubject;
        var cn = ExtractRdn(certificateSubject, "CN") ?? "";
        return _publishers.Any(p =>
            org.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
            cn.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Force HTTPS (certains catalogues constructeurs référencent encore des chemins sans schéma ou en http).</summary>
    public static Uri NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[7..];
        else if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.TrimStart('/');
        return new Uri(url);
    }

    internal static string? ExtractRdn(string subject, string key)
    {
        foreach (var part in SplitDn(subject))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            if (part[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim().Trim('"');
        }
        return null;
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
