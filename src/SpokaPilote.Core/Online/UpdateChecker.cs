using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Online;

/// <param name="Size">Taille de l'installeur annoncée par GitHub (0 = inconnue).</param>
/// <param name="Sha256">SHA-256 de l'installeur annoncé par GitHub (champ « digest »), en minuscules, ou null.</param>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string ReleaseUrl, string Notes, long Size = 0, string? Sha256 = null)
{
    /// <summary>Archive portable de cette architecture (SpokaPilote-Portable-X.Y.Z-arch.zip), ou null si la Release n'en a pas.</summary>
    public string? PortableUrl { get; init; }
    public long PortableSize { get; init; }
    public string? PortableSha256 { get; init; }
}

/// <summary>L'installeur téléchargé ne correspond pas à ce que GitHub annonce : il est supprimé et jamais exécuté.</summary>
public sealed class UpdateIntegrityException(string message) : Exception(message);

/// <summary>
/// Vérifie la dernière version publiée sur GitHub et télécharge son installeur. Le dépôt interrogé est celui du produit
/// en vigueur (<see cref="AppIdentity.UpdateRepository"/>) : un produit construit sur ce moteur ne reçoit jamais les
/// Releases de SpokaPilote.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Dépôt des Releases de SpokaPilote (identité par défaut).</summary>
    public const string Repository = "Spokator/SpokaPilote";
    /// <summary>Dépôt interrogé : celui du produit en vigueur.</summary>
    public static string CurrentRepository => AppIdentity.Current.UpdateRepository;
    /// <summary>Préfixe des archives portables. Ne contient jamais « Setup » ni « Installer » (voir <see cref="PortableAssetName"/>).</summary>
    public const string PortablePrefix = "SpokaPilote-Portable-";

    /// <summary>
    /// Nom de l'archive portable publiée (scripts\build-installer.ps1) : « SpokaPilote-Portable-3.1.0-x64.zip ».
    /// Contrat : ni « Setup » ni « Installer » dans le nom (les versions 1.0.x prennent le premier « Setup* » venu
    /// comme installeur) et extension .zip (jamais retenue comme installeur par <see cref="Parse(JsonElement, Version, string)"/>).
    /// </summary>
    public static string PortableAssetName(string version, string architecture) => $"{PortablePrefix}{version}-{architecture}.zip";
    public static string ReleasesPage => $"https://github.com/{CurrentRepository}/releases/latest";

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

    public static Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default) => CheckAsync(current, CurrentRepository, ct);

    /// <summary>Dernière Release publiée de <paramref name="repository"/> (« propriétaire/nom »), si plus récente.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, string repository, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repository, @"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$"))
            throw new ArgumentException($"Dépôt GitHub invalide : {repository}", nameof(repository));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
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

    /// <summary>Release GitHub (JSON de l'API) → installeur de cette architecture, si la version est plus récente.</summary>
    public static UpdateInfo? Parse(JsonElement release, Version current, string architecture)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current)) return null;

        string? url = null;
        long size = 0;
        string? sha256 = null;
        string? portableUrl = null;
        long portableSize = 0;
        string? portableSha256 = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var isArm = name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
                var isPortable = name.StartsWith(PortablePrefix, StringComparison.OrdinalIgnoreCase);
                // x64 : « SpokaPilote-Setup-X.exe » ; ARM64 : « SpokaPilote-Installer-X-arm64.exe ». L'installeur ARM64 ne
                // contient volontairement pas « Setup » : les versions 1.0.x prennent le premier « Setup*.exe » venu.
                // Une archive portable (.zip) n'est jamais un installeur.
                var isInstaller = !isPortable && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                  (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) || name.Contains("Installer", StringComparison.OrdinalIgnoreCase));
                if (isInstaller && url == null && isArm == (architecture == "arm64"))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    (size, sha256) = SizeAndDigest(asset);
                }
                else if (isPortable && portableUrl == null && name.EndsWith($"-{architecture}.zip", StringComparison.OrdinalIgnoreCase))
                {
                    portableUrl = asset.GetProperty("browser_download_url").GetString();
                    (portableSize, portableSha256) = SizeAndDigest(asset);
                }
            }
        }
        if (url == null) return null;

        var page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, url, page, notes, size, sha256)
        {
            PortableUrl = portableUrl,
            PortableSize = portableSize,
            PortableSha256 = portableSha256,
        };
    }

    private static (long Size, string? Sha256) SizeAndDigest(JsonElement asset) =>
        (asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
         ParseDigest(asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));

    /// <summary>« sha256:ABCD… » → « abcd… » ; tout autre format → null.</summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>null si l'installeur correspond à ce que GitHub annonce, sinon la raison.</summary>
    internal static string? CheckIntegrity(UpdateInfo update, long length, string sha256Hex) =>
        CheckIntegrity(update.Size, update.Sha256, length, sha256Hex);

    internal static string? CheckIntegrity(long expectedSize, string? expectedSha256, long length, string sha256Hex)
    {
        if (expectedSha256 is null)
            return "GitHub n'annonce pas l'empreinte SHA-256 du fichier";
        if (expectedSize > 0 && length != expectedSize)
            return $"fichier incomplet ({length:N0} octets reçus sur {expectedSize:N0})";
        if (!string.Equals(expectedSha256, sha256Hex, StringComparison.OrdinalIgnoreCase))
            return "l'empreinte du fichier ne correspond pas à celle publiée sur GitHub";
        return null;
    }

    /// <summary>
    /// Hôtes EXACTS d'un téléchargement de Release : github.com (adresse publiée dans la Release), puis les serveurs de
    /// fichiers vers lesquels GitHub redirige. Chaque redirection est vérifiée.
    /// </summary>
    internal static readonly string[] ReleaseDownloadHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    internal static bool IsReleaseDownloadHost(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        ReleaseDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adresse d'un fichier de Release du produit en vigueur : https://github.com/&lt;dépôt&gt;/releases/download/...</summary>
    internal static bool IsCurrentProductReleaseUrl(Uri uri) =>
        IsReleaseDownloadHost(uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{CurrentRepository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Client sans redirection automatique : chaque saut est contrôlé (<see cref="ReleaseDownloadHosts"/>).</summary>
    private static readonly Lazy<HttpClient> DownloadClient = new(() =>
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(AppIdentity.Current.ProductName, CurrentVersion.ToString(3)));
        return client;
    });

    /// <summary>
    /// Télécharge l'installeur dans un « .part », contrôle taille et SHA-256 (empreinte publiée par GitHub OBLIGATOIRE),
    /// puis la signature Authenticode s'il est signé (<see cref="CheckInstallerSignature(string)"/>), et seulement ensuite
    /// lui donne son nom définitif. En cas d'échec, le fichier partiel est supprimé : un installeur abîmé, altéré ou sans
    /// empreinte publiée n'est jamais lancé.
    /// </summary>
    public static Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default) =>
        DownloadAsync(update, progress, DownloadClient.Value, ct);

    internal static Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, HttpClient client, CancellationToken ct)
    {
        if (AppPaths.IsPortable)
            throw new InvalidOperationException("Mode portable : l'installeur n'est pas téléchargé (utilisez l'archive portable).");
        var identity = AppIdentity.Current;
        var target = Path.Combine(Path.GetTempPath(), identity.DataFolderName, $"{identity.ProductName}-Setup-{update.Version}.exe");
        return DownloadVerifiedAsync(update.DownloadUrl, update.Size, update.Sha256, target, progress, client, ct, update.ReleaseUrl);
    }

    /// <summary>
    /// Mode portable : télécharge l'archive portable de la nouvelle version dans <paramref name="folder"/> (sur la clé),
    /// contrôle taille et SHA-256 (empreinte publiée par GitHub obligatoire) et renvoie son chemin. Rien n'est extrait
    /// ni exécuté : le technicien remplace lui-même la copie portable, SpokaPilote fermé.
    /// </summary>
    public static Task<string> DownloadPortableAsync(UpdateInfo update, string folder, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (update.PortableUrl is null)
            throw new UpdateIntegrityException($"La version {update.Version.ToString(3)} ne publie pas d'archive portable pour {HostArchitecture} : voir {update.ReleaseUrl}.");
        if (update.PortableSha256 is null)
            throw new UpdateIntegrityException($"GitHub n'annonce pas l'empreinte SHA-256 de l'archive portable : téléchargez-la depuis {update.ReleaseUrl}.");
        var target = Path.Combine(folder, PortableAssetName(update.Version.ToString(3), HostArchitecture));
        return DownloadVerifiedAsync(update.PortableUrl, update.PortableSize, update.PortableSha256, target, progress, DownloadClient.Value, ct, update.ReleaseUrl);
    }

    internal static async Task<string> DownloadVerifiedAsync(string url, long expectedSize, string? expectedSha256, string target,
        IProgress<double>? progress, HttpClient client, CancellationToken ct, string releaseUrl)
    {
        var uri = new Uri(url);
        // Release GitHub du produit en vigueur uniquement (« https://github.com/<dépôt>/releases/download/... », hôte
        // exact) : un produit construit sur ce moteur ne télécharge jamais l'installeur d'un autre.
        if (!IsCurrentProductReleaseUrl(uri))
            throw new UpdateIntegrityException($"Adresse de mise à jour inattendue : seule une Release GitHub de {AppIdentity.Current.ProductName} est acceptée.");
        // Sans empreinte publiée, rien ne permet de vérifier le fichier : il n'est même pas téléchargé.
        if (expectedSha256 is null)
            throw new UpdateIntegrityException($"GitHub n'annonce pas l'empreinte SHA-256 de ce fichier : mise à jour refusée. Téléchargez-la depuis {releaseUrl}.");

        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var response = await GetFollowingReleaseRedirectsAsync(client, uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
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
            var isInstaller = !target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var problem = CheckIntegrity(expectedSize, expectedSha256, done, Convert.ToHexString(hash.GetHashAndReset()));
            if (problem is null && isInstaller) problem = CheckInstallerSignature(part);
            if (problem != null) throw new UpdateIntegrityException((isInstaller ? "Installeur refusé : " : "Archive portable refusée : ") + problem + ".");
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
    /// GET qui suit au plus 5 redirections, chacune vers un hôte exact de <see cref="ReleaseDownloadHosts"/> en HTTPS ;
    /// toute autre destination est refusée.
    /// </summary>
    private static async Task<HttpResponseMessage> GetFollowingReleaseRedirectsAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location) return response;
            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (hop >= 5) throw new UpdateIntegrityException("Mise à jour refusée : trop de redirections.");
            if (!IsReleaseDownloadHost(next))
                throw new UpdateIntegrityException($"Mise à jour refusée : redirection vers {next.Host}, hors des serveurs de fichiers de GitHub.");
            current = next;
        }
    }

    /// <summary>Signataire de l'exécutable en cours s'il est signé (signature valide) : éditeur attendu des mises à jour.</summary>
    private static readonly Lazy<string?> RunningPublisher = new(() =>
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return null;
            var sig = SignatureVerifier.Verify(exe);
            return sig.IsValid ? sig.Signer : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    /// <summary>Contrôle Authenticode de l'installeur téléchargé ; null = accepté, sinon la raison du refus.</summary>
    internal static string? CheckInstallerSignature(string path)
    {
        var signed = SignatureVerifier.GetSigner(path) is not null;
        return CheckInstallerSignature(signed ? SignatureVerifier.Verify(path) : null, RunningPublisher.Value);
    }

    /// <summary>
    /// Règle de signature de l'installeur (<paramref name="installer"/> null = non signé) :
    /// <list type="bullet">
    /// <item>signé : la signature doit être valide et, si la version en cours est elle-même signée
    ///   (<paramref name="expectedPublisher"/>), du MÊME éditeur ;</item>
    /// <item>non signé (Releases actuelles) : accepté sur la seule empreinte SHA-256 publiée par GitHub, sauf si la
    ///   version en cours est signée (une mise à jour ne fait jamais perdre la signature).</item>
    /// </list>
    /// </summary>
    internal static string? CheckInstallerSignature(SignatureResult? installer, string? expectedPublisher)
    {
        if (installer is null)
            return expectedPublisher is null ? null : $"installeur non signé, alors que la version en cours est signée par {expectedPublisher}";
        if (!installer.IsValid) return $"signature de l'installeur invalide ({installer.Detail})";
        if (expectedPublisher is not null && !TrustPolicy.SamePublisher(installer.Signer, expectedPublisher))
            return $"installeur signé par {installer.Signer}, éditeur attendu : {expectedPublisher}";
        return null;
    }

    /// <summary>
    /// Lance l'installeur en mode silencieux (il ferme SpokaPilote et met à jour). Avec <paramref name="relaunch"/>,
    /// l'installeur relance l'interface à la fin (paramètre /RELAUNCH=1 interprété par SpokaPilote.iss). Juste avant le
    /// lancement, le fichier est ouvert en lecture seule SANS partage en écriture ni suppression (il ne peut plus être
    /// remplacé), puis son SHA-256 et sa signature sont revérifiés : rien n'est lancé sans empreinte publiée concordante.
    /// </summary>
    public static void LaunchInstaller(UpdateInfo update, string installerPath, bool relaunch)
    {
        if (AppPaths.IsPortable)
            throw new InvalidOperationException("Mode portable : l'installeur n'est jamais lancé depuis une copie portable.");
        if (update.Sha256 is null)
            throw new UpdateIntegrityException($"GitHub n'annonce pas l'empreinte SHA-256 de l'installeur : il n'est pas lancé. Voir {update.ReleaseUrl}.");
        using var locked = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var problem = CheckIntegrity(update.Size, update.Sha256, locked.Length, Convert.ToHexString(SHA256.HashData(locked)))
                      ?? CheckInstallerSignature(installerPath);
        if (problem is not null) throw new UpdateIntegrityException($"Installeur refusé au lancement : {problem}.");
        var args = relaunch
            ? "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1"
            : "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        Process.Start(new ProcessStartInfo(installerPath, args) { UseShellExecute = true, Verb = "runas" });
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
