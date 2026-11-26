using System.Security.Cryptography;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Install;

public sealed record DownloadProgress(string FileName, long Received, long? Total);

/// <summary>
/// Téléchargement contrôlé : HTTPS uniquement, domaines officiels uniquement, empreinte vérifiée, cache local.
/// Avec une console centrale 4.0 dont le cache est actif, le fichier est d'abord demandé à la console
/// (<see cref="ConsoleCache"/>) ; la copie reçue n'est gardée que si le poste peut la vérifier seul (empreinte publiée, ou
/// signature Authenticode valide d'un éditeur approuvé), sinon c'est le téléchargement direct.
/// </summary>
public sealed class Downloader
{
    /// <summary>Types de fichiers vérifiables par leur seule signature Authenticode quand la source ne publie aucune empreinte.</summary>
    internal static readonly string[] SignedFileExtensions = [".exe", ".msi", ".msp", ".cab", ".msu"];

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

    public async Task<string> DownloadAsync(
        string url,
        string? fileName = null,
        string? sha256 = null,
        string? sha1 = null,
        string? md5 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var uri = TrustPolicy.NormalizeUrl(url);
        if (!_trust.IsTrustedHost(uri))
            throw new SecurityException($"Téléchargement refusé : {uri.Host} ne fait pas partie des sources officielles autorisées.");

        fileName ??= Path.GetFileName(uri.AbsolutePath);
        var target = Path.Combine(AppPaths.Downloads, AppPaths.SafeName(fileName, 120));

        if (File.Exists(target) && await VerifyHashesAsync(target, sha256, sha1, md5, ct).ConfigureAwait(false) is true)
        {
            Log.Info($"Déjà en cache : {fileName}");
            return target;
        }

        var tmp = target + ".part";
        if ((_useCurrentCache ? ConsoleCache.Current : _cache) is { } cache && CanVerifyIndependently(fileName, sha256, sha1, md5) &&
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
    /// Une copie venue de la console ne sert que si le poste peut la vérifier sans faire confiance à la console :
    /// empreinte publiée par la source, ou fichier signé (Authenticode). Sinon (ex. .zip sans empreinte) : direct.
    /// </summary>
    internal static bool CanVerifyIndependently(string fileName, string? sha256, string? sha1, string? md5) =>
        sha256 is not null || sha1 is not null || md5 is not null ||
        SignedFileExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

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
    /// Vérification d'une copie reçue de la console, AVANT tout usage : empreinte publiée par la source, ou à défaut
    /// signature Authenticode valide d'un éditeur approuvé. Renvoie le motif du refus, ou null si la copie est acceptée.
    /// Les contrôles habituels de l'installation (signature, éditeur, ID matériel de l'INF) s'appliquent ensuite comme
    /// pour un téléchargement direct.
    /// </summary>
    internal async Task<string?> VerifyConsoleCopyAsync(string path, string? sha256, string? sha1, string? md5, CancellationToken ct)
    {
        var hashes = await VerifyHashesAsync(path, sha256, sha1, md5, ct).ConfigureAwait(false);
        if (hashes is true) return null;
        if (hashes is false) return "empreinte différente de celle publiée par la source officielle";
        var sig = SignatureVerifier.Verify(path);
        if (!sig.IsValid) return $"signature invalide ({sig.Detail})";
        return _trust.IsTrustedPublisher(sig.Signer) ? null : $"éditeur non approuvé ({sig.Signer})";
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
