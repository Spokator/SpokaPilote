namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Liste des domaines officiels, partagée par le client (<see cref="TrustPolicy"/>) et par la console centrale (cache de
/// pilotes, qui n'accepte de télécharger que ce que le client accepterait lui-même). Ce fichier est compilé tel quel dans
/// SpokaPilote.Server (lien de fichier dans le .csproj) : il ne doit dépendre que de la bibliothèque de base, sans rien de
/// propre à Windows.
/// </summary>
internal static class OfficialHosts
{
    /// <summary>Domaines de téléchargement des paquets (HTTPS uniquement ; « *. » = tout sous-domaine).</summary>
    public static readonly string[] Download =
    [
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
    ];

    /// <summary>
    /// Services de métadonnées interrogés en plus des domaines de téléchargement (jamais pour un paquet) : service de
    /// recherche des pilotes NVIDIA.
    /// </summary>
    public static readonly string[] MetadataOnly =
    [
        "gfwsl.geforce.com",
    ];

    /// <summary>Hôte du Microsoft Update Catalog (pages de recherche et de téléchargement, limitées par adresse IP publique).</summary>
    public const string MicrosoftCatalogHost = "www.catalog.update.microsoft.com";

    /// <summary>Le nom d'hôte correspond-il à l'un des motifs (« *.exemple.com » = tout sous-domaine) ?</summary>
    public static bool Matches(string host, IEnumerable<string> patterns)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().ToLowerInvariant();
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
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
    /// Adresse acceptable pour un téléchargement officiel : absolue, HTTPS, port par défaut, sans identifiants, vers un
    /// domaine de la liste (plus <paramref name="extraHosts"/>).
    /// </summary>
    public static bool IsOfficialDownload(Uri uri, IEnumerable<string>? extraHosts = null) =>
        IsWellFormed(uri) && Matches(uri.Host, extraHosts is null ? Download : Download.Concat(extraHosts));

    /// <summary>Adresse acceptable pour une métadonnée (catalogue, référence, recherche) : domaines de téléchargement + services de métadonnées.</summary>
    public static bool IsOfficialMetadata(Uri uri, IEnumerable<string>? extraHosts = null) =>
        IsWellFormed(uri) && Matches(uri.Host, (extraHosts is null ? Download : Download.Concat(extraHosts)).Concat(MetadataOnly));

    private static bool IsWellFormed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.HostNameType == UriHostNameType.Dns;
}
