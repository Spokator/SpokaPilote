using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Online;

/// <param name="Size">Taille de l'installeur annoncée par GitHub (0 = inconnue).</param>
/// <param name="Sha256">SHA-256 de l'installeur annoncé par GitHub (champ « digest »), en minuscules, ou null.</param>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string ReleaseUrl, string Notes, long Size = 0, string? Sha256 = null);

/// <summary>L'installeur téléchargé ne correspond pas à ce que GitHub annonce : il est supprimé et jamais exécuté.</summary>
public sealed class UpdateIntegrityException(string message) : Exception(message);

/// <summary>Vérifie la dernière version publiée sur GitHub et télécharge son installeur.</summary>
public static class UpdateChecker
{
    public const string Repository = "Spokator/SpokaPilote";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var clean = info?.Split('+', '-')[0];
            return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.Client.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        return Parse(doc.RootElement, current);
    }

    /// <summary>« arm64 » ou « x64 » : l'installeur publié pour cette architecture.</summary>
    public static string HostArchitecture =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    internal static UpdateInfo? Parse(JsonElement release, Version current) => Parse(release, current, HostArchitecture);

    internal static UpdateInfo? Parse(JsonElement release, Version current, string architecture)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current)) return null;

        string? url = null;
        long size = 0;
        string? sha256 = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var isArm = name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && isArm == (architecture == "arm64"))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    if (asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                    sha256 = ParseDigest(asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);
                    break;
                }
            }
        }
        if (url == null) return null;

        var page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, url, page, notes, size, sha256);
    }

    /// <summary>« sha256:ABCD… » → « abcd… » ; tout autre format → null.</summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>null si l'installeur correspond à ce que GitHub annonce, sinon la raison.</summary>
    internal static string? CheckIntegrity(UpdateInfo update, long length, string sha256Hex)
    {
        if (update.Size > 0 && length != update.Size)
            return $"fichier incomplet ({length:N0} octets reçus sur {update.Size:N0})";
        if (update.Sha256 != null && !string.Equals(update.Sha256, sha256Hex, StringComparison.OrdinalIgnoreCase))
            return "l'empreinte du fichier ne correspond pas à celle publiée sur GitHub";
        return null;
    }

    /// <summary>
    /// Télécharge l'installeur dans un « .part », contrôle taille et SHA-256, puis seulement lui donne son nom
    /// définitif. En cas d'échec, le fichier partiel est supprimé : un installeur abîmé ou altéré n'est jamais lancé.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var uri = new Uri(update.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            throw new UpdateIntegrityException("Adresse de mise à jour inattendue : seule une Release GitHub de SpokaPilote est acceptée.");

        var target = Path.Combine(Path.GetTempPath(), "SpokaPilote", $"SpokaPilote-Setup-{update.Version}.exe");
        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var response = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(part))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }
            var problem = CheckIntegrity(update, done, Convert.ToHexString(hash.GetHashAndReset()));
            if (problem != null) throw new UpdateIntegrityException("Installeur refusé : " + problem + ".");
            File.Move(part, target, true);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { /* au mieux */ }
            throw;
        }
    }

    /// <summary>
    /// Lance l'installeur en mode silencieux (il ferme SpokaPilote et met à jour). Avec <paramref name="relaunch"/>,
    /// l'installeur relance l'interface à la fin (paramètre /RELAUNCH=1 interprété par SpokaPilote.iss).
    /// </summary>
    public static void LaunchInstaller(string installerPath, bool relaunch)
    {
        var args = relaunch
            ? "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1"
            : "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        Process.Start(new ProcessStartInfo(installerPath, args) { UseShellExecute = true, Verb = "runas" });
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
