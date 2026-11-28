using System.Security.Cryptography;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Install;

public sealed record DownloadProgress(string FileName, long Received, long? Total);

/// <summary>
/// Téléchargement contrôlé : HTTPS uniquement, domaines officiels uniquement, empreinte vérifiée. Chaque fichier est
/// écrit dans un dossier de travail propre à l'exécution (<see cref="WorkDirectory"/>, privé quand le processus est
/// élevé) : jamais dans un dossier partagé où un utilisateur aurait pu déposer d'avance un « .part » ou un fichier
/// « déjà en cache », puis le remplacer entre la vérification et l'exécution.
/// Avec une console centrale 4.0 dont le cache est actif, le fichier est d'abord demandé à la console
/// (<see cref="ConsoleCache"/>) seulement si la source en publie une empreinte obtenue sans la console ; la copie reçue
/// n'est gardée que si elle a cette empreinte. Sans empreinte (paquet Lenovo sans SHA-256, fichier de Microsoft sans
/// empreinte publiée...) : toujours le téléchargement direct.
/// </summary>
public sealed class Downloader : IDisposable
{
    private readonly TrustPolicy _trust;
    private readonly ConsoleCache? _cache;
    private readonly bool _useCurrentCache;
    private readonly HttpClient? _direct;

    public Downloader(TrustPolicy trust)
    {
        _trust = trust;
        _useCurrentCache = true;
    }

    /// <summary>Tests : cache de console et client HTTP direct imposés (null = aucun cache / client partagé).</summary>
    internal Downloader(TrustPolicy trust, ConsoleCache? cache, HttpClient? direct)
    {
        _trust = trust;
        _cache = cache;
        _direct = direct;
    }

    private HttpClient Direct => _direct ?? Http.Client;

    /// <summary>Dossier de travail créé à la demande quand l'appelant n'en fournit pas (supprimé par <see cref="Dispose"/>).</summary>
    private WorkDirectory? _ownWork;

    public void Dispose()
    {
        _ownWork?.Dispose();
        _ownWork = null;
    }

    /// <summary>
    /// Télécharge et vérifie un fichier dans <paramref name="directory"/> (dossier de travail de l'appelant, voir
    /// <see cref="WorkDirectory"/>), ou à défaut dans un dossier de travail propre à ce téléchargeur.
    /// </summary>
    public async Task<string> DownloadAsync(
        string url,
        string? fileName = null,
        string? sha256 = null,
        string? sha1 = null,
        string? md5 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        string? directory = null)
    {
        var uri = TrustPolicy.NormalizeUrl(url);
        if (!_trust.IsTrustedHost(uri))
            throw new SecurityException($"Téléchargement refusé : {uri.Host} ne fait pas partie des sources officielles autorisées.");

        fileName ??= Path.GetFileName(uri.AbsolutePath);
        directory ??= (_ownWork ??= WorkDirectory.Create("dl")).Path;
        var target = Path.Combine(directory, AppPaths.SafeName(fileName, 120));

        // Même fichier demandé deux fois pendant l'exécution (variantes d'un même paquet) : réutilisé s'il est vérifiable.
        if (File.Exists(target) && await VerifyHashesAsync(target, sha256, sha1, md5, ct).ConfigureAwait(false) is true)
        {
            Log.Info($"Déjà téléchargé : {fileName}");
            return target;
        }

        var tmp = target + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);
        if ((_useCurrentCache ? ConsoleCache.Current : _cache) is { } cache && CanVerifyIndependently(sha256, sha1, md5) &&
            await TryConsoleAsync(cache, uri, fileName, tmp, sha256, sha1, md5, progress, ct).ConfigureAwait(false))
        {
            File.Move(tmp, target, overwrite: true);
            return target;
        }

        Log.Info($"Téléchargement : {uri}");
        using var response = await Direct.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Une redirection vers un domaine non autorisé est également refusée.
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!_trust.IsTrustedHost(finalUri))
            throw new SecurityException($"Redirection refusée vers {finalUri.Host}.");

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    progress?.Report(new DownloadProgress(fileName, received, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report(new DownloadProgress(fileName, received, total));
        }

        var ok = await VerifyHashesAsync(tmp, sha256, sha1, md5, ct).ConfigureAwait(false);
        if (ok is false)
        {
            File.Delete(tmp);
            throw new SecurityException($"Empreinte invalide pour {fileName} : fichier corrompu ou altéré, supprimé.");
        }
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Une copie venue de la console ne sert que si le poste peut la vérifier sans faire confiance à la console : une
    /// empreinte publiée par la source et obtenue sans la console (catalogue signé Dell/HP, lien du Microsoft Update
    /// Catalog, service NVIDIA, catalogue Lenovo lu à la source). Une signature valide ne suffit PAS : une console
    /// compromise pourrait servir, à la place du fichier demandé, n'importe quel AUTRE fichier signé du même éditeur
    /// (ancienne version vulnérable d'un installeur Lenovo, par exemple). Sans empreinte : téléchargement direct.
    /// </summary>
    internal static bool CanVerifyIndependently(string? sha256, string? sha1, string? md5) =>
        !string.IsNullOrWhiteSpace(sha256) || !string.IsNullOrWhiteSpace(sha1) || !string.IsNullOrWhiteSpace(md5);

    private async Task<bool> TryConsoleAsync(ConsoleCache cache, Uri uri, string fileName, string tmp, string? sha256, string? sha1, string? md5,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!await cache.TryDownloadFileAsync(uri, tmp, sha256, sha1, md5,
                (received, total) => progress?.Report(new DownloadProgress(fileName, received, total)), ct).ConfigureAwait(false))
            return false;
        var rejection = await VerifyConsoleCopyAsync(tmp, sha256, sha1, md5, ct).ConfigureAwait(false);
        if (rejection is null) return true;
        try { File.Delete(tmp); } catch (IOException) { /* remplacé par le téléchargement direct */ }
        cache.ReportRejected(uri, rejection);
        return false;
    }

    /// <summary>
    /// Vérification d'une copie reçue de la console, AVANT tout usage : elle doit avoir l'empreinte publiée par la source
    /// (obtenue sans la console). Renvoie le motif du refus, ou null si la copie est acceptée. Sans empreinte, la copie est
    /// toujours refusée, même validement signée par l'éditeur attendu (voir <see cref="CanVerifyIndependently"/>). Les
    /// contrôles habituels de l'installation (signature, éditeur, ID matériel de l'INF) s'appliquent ensuite comme pour un
    /// téléchargement direct.
    /// </summary>
    internal static async Task<string?> VerifyConsoleCopyAsync(string path, string? sha256, string? sha1, string? md5, CancellationToken ct)
    {
        if (!CanVerifyIndependently(sha256, sha1, md5)) return "aucune empreinte publiée par la source : la console ne peut pas servir ce fichier";
        return await VerifyHashesAsync(path, sha256, sha1, md5, ct).ConfigureAwait(false) is true
            ? null
            : "empreinte différente de celle publiée par la source officielle";
    }

    /// <summary>true = au moins une empreinte fournie et toutes concordent ; false = discordance ; null = aucune empreinte connue.</summary>
    public static async Task<bool?> VerifyHashesAsync(string path, string? sha256, string? sha1, string? md5, CancellationToken ct)
    {
        if (sha256 is null && sha1 is null && md5 is null) return null;
        await using var fs = File.OpenRead(path);
        if (sha256 is not null)
        {
            var h = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!h.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
            fs.Position = 0;
        }
        if (sha1 is not null)
        {
            var h = Convert.ToHexString(await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!h.Equals(sha1, StringComparison.OrdinalIgnoreCase)) return false;
            fs.Position = 0;
        }
        if (md5 is not null)
        {
            var h = Convert.ToHexString(await MD5.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!h.Equals(md5, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

public sealed class SecurityException(string message) : Exception(message);
