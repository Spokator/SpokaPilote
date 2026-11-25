using System.Security.Cryptography;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

public sealed record DownloadProgress(string FileName, long Received, long? Total);

/// <summary>Téléchargement contrôlé : HTTPS uniquement, domaines officiels uniquement, empreinte vérifiée, cache.</summary>
public sealed class Downloader
{
    private readonly TrustPolicy _trust;

    public Downloader(TrustPolicy trust) => _trust = trust;

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

        Log.Info($"Téléchargement : {uri}");
        using var response = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Une redirection vers un domaine non autorisé est également refusée.
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!_trust.IsTrustedHost(finalUri))
            throw new SecurityException($"Redirection refusée vers {finalUri.Host}.");

        var total = response.Content.Headers.ContentLength;
        var tmp = target + ".part";
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
