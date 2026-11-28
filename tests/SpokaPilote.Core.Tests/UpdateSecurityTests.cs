using System.Net;
using System.Security.Cryptography;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Mise à jour automatique (lancée en SYSTEM par l'agent) : hôte GitHub exact, redirections contrôlées, empreinte SHA-256
/// publiée obligatoire, signature contrôlée si l'installeur est signé, revérification juste avant le lancement.
/// </summary>
public sealed class UpdateSecurityTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private const string Asset = "https://github.com/Spokator/SpokaPilote/releases/download/v9.0.0/SpokaPilote-Portable-9.0.0-x64.zip";

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location);
        return r;
    }

    private static string Target() => Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", $"update-{Guid.NewGuid():N}.zip");

    [Theory]
    [InlineData("https://github.com/Spokator/SpokaPilote/releases/download/v9.0.0/SpokaPilote-Setup-9.0.0.exe", true)]
    [InlineData("https://GitHub.com/Spokator/SpokaPilote/releases/download/v9.0.0/SpokaPilote-Setup-9.0.0.exe", true)]
    [InlineData("https://evilgithub.com/Spokator/SpokaPilote/releases/download/v9.0.0/SpokaPilote-Setup-9.0.0.exe", false)] // « se termine par github.com »
    [InlineData("https://gist.github.com/Spokator/SpokaPilote/releases/download/v9.0.0/x.exe", false)]
    [InlineData("https://github.com.evil.io/Spokator/SpokaPilote/releases/download/v9.0.0/x.exe", false)]
    [InlineData("http://github.com/Spokator/SpokaPilote/releases/download/v9.0.0/x.exe", false)]
    [InlineData("https://github.com:8443/Spokator/SpokaPilote/releases/download/v9.0.0/x.exe", false)]
    [InlineData("https://user@github.com/Spokator/SpokaPilote/releases/download/v9.0.0/x.exe", false)]
    [InlineData("https://github.com/Spokator/SpokaPrint/releases/download/v9.0.0/x.exe", false)]
    public void Release_url_must_be_exactly_on_github_com(string url, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsCurrentProductReleaseUrl(new Uri(url)));

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/2?X-Amz-Signature=x", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r", true)]
    [InlineData("https://evil.githubusercontent.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/Spokator/SpokaPilote/main/x.exe", false)]
    [InlineData("https://objects.githubusercontent.com.evil.io/x", false)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    public void Redirect_targets_are_an_exact_list(string url, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsReleaseDownloadHost(new Uri(url)));

    [Fact]
    public async Task Installer_without_a_published_digest_is_never_downloaded()
    {
        var stub = new Stub(_ => throw new InvalidOperationException("aucune requête attendue"));
        var update = new UpdateInfo(new Version(9, 0, 0), "v9.0.0",
            "https://github.com/Spokator/SpokaPilote/releases/download/v9.0.0/SpokaPilote-Setup-9.0.0.exe", "https://github.com/r", "", Size: 1000);
        var ex = await Assert.ThrowsAsync<UpdateIntegrityException>(() => UpdateChecker.DownloadAsync(update, null, new HttpClient(stub), default));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(stub.Requests);
        Assert.NotNull(UpdateChecker.CheckIntegrity(1000, null, 1000, new string('a', 64))); // jamais « taille seule »
    }

    [Fact]
    public async Task Redirects_are_followed_only_to_github_file_servers()
    {
        var data = new byte[5000];
        new Random(3).NextBytes(data);
        var sha = Convert.ToHexString(SHA256.HashData(data));
        var ok = new Stub(req => req.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        var target = Target();
        try
        {
            var path = await UpdateChecker.DownloadVerifiedAsync(Asset, data.Length, sha, target, null, new HttpClient(ok), default, "https://github.com/r");
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, ok.Requests.Count);
        }
        finally
        {
            File.Delete(target);
        }

        var evil = new Stub(req => req.RequestUri!.Host == "github.com"
            ? Redirect("https://evil.example/SpokaPilote-Portable-9.0.0-x64.zip")
            : throw new InvalidOperationException("la redirection ne doit pas être suivie"));
        var ex = await Assert.ThrowsAsync<UpdateIntegrityException>(() =>
            UpdateChecker.DownloadVerifiedAsync(Asset, data.Length, sha, Target(), null, new HttpClient(evil), default, "https://github.com/r"));
        Assert.Contains("evil.example", ex.Message);
        Assert.Single(evil.Requests);

        var loop = new Stub(_ => Redirect("https://objects.githubusercontent.com/x"));
        await Assert.ThrowsAsync<UpdateIntegrityException>(() =>
            UpdateChecker.DownloadVerifiedAsync(Asset, data.Length, sha, Target(), null, new HttpClient(loop), default, "https://github.com/r"));
    }

    [Fact]
    public void Installer_signature_rules()
    {
        const string spoka = "CN=SpokaPilote, O=SpokaPilote, C=FR";
        var valid = new SignatureResult(true, spoka, "OK");
        // Releases actuelles : non signées, acceptées sur la seule empreinte publiée.
        Assert.Null(UpdateChecker.CheckInstallerSignature(null, expectedPublisher: null));
        // Version en cours signée : l'installeur doit l'être aussi, par le même éditeur.
        Assert.NotNull(UpdateChecker.CheckInstallerSignature(null, spoka));
        Assert.Null(UpdateChecker.CheckInstallerSignature(valid, spoka));
        Assert.Contains("éditeur attendu", UpdateChecker.CheckInstallerSignature(new SignatureResult(true, "CN=Evil, O=Evil Ltd", "OK"), spoka));
        // Signé mais signature invalide (fichier modifié, certificat révoqué...) : refusé.
        Assert.Contains("invalide", UpdateChecker.CheckInstallerSignature(new SignatureResult(false, spoka, "signature altérée"), null));
        Assert.Null(UpdateChecker.CheckInstallerSignature(valid, null));
    }

    [Fact]
    public void Installer_is_reverified_just_before_launch()
    {
        var path = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", $"installer-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var update = new UpdateInfo(new Version(9, 0, 0), "v9.0.0", "https://github.com/x", "https://github.com/r", "", 3);
            Assert.Throws<UpdateIntegrityException>(() => UpdateChecker.LaunchInstaller(update, path, relaunch: false)); // sans empreinte
            var replaced = update with { Sha256 = new string('a', 64) };                                                 // fichier changé
            Assert.Contains("empreinte", Assert.Throws<UpdateIntegrityException>(() => UpdateChecker.LaunchInstaller(replaced, path, relaunch: false)).Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
