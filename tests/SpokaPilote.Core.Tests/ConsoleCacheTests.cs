using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Cache de pilotes de la console, côté poste : la console n'est qu'un transport. Une copie altérée est refusée et
/// remplacée par le fichier officiel ; une console absente, lente, ancienne ou en erreur ne fait jamais échouer un
/// téléchargement ; les pages « Too many search requests » ne sont jamais gardées.
/// </summary>
public sealed class ConsoleCacheTests
{
    private static readonly Uri ConsoleUri = new("https://console.exemple.fr/");

    /// <summary>Console ou source simulée : réponse calculée par requête, requêtes enregistrées.</summary>
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public int Count(string pathPart) => Requests.Count(r => r.RequestUri!.PathAndQuery.Contains(pathPart, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Status(bool enabled = true, bool catalogs = true) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"enabled\":{(enabled ? "true" : "false")},\"catalogs\":{(catalogs ? "true" : "false")},\"sizeBytes\":13314398617," +
                                    "\"maxBytes\":21474836480,\"files\":12,\"hitRatio\":0.93,\"bytesSaved\":5000}", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage File(byte[] data, string cache = "hit", Stream? stream = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream ?? new MemoryStream(data)) };
        r.Content.Headers.ContentLength = data.Length;
        r.Headers.TryAddWithoutValidation("X-SpokaPilote-Cache", cache);
        if (cache == "hit")
        {
            r.Headers.ETag = new EntityTagHeaderValue($"\"{Sha256(data)}\"");
            r.Headers.AcceptRanges.Add("bytes");
        }
        return r;
    }

    private static HttpResponseMessage Error(HttpStatusCode code, string? cacheHeader = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("{\"error\":\"x\"}", Encoding.UTF8, "application/json") };
        if (cacheHeader is not null) r.Headers.TryAddWithoutValidation("X-SpokaPilote-Cache", cacheHeader);
        return r;
    }

    private static Stub Console(Func<HttpRequestMessage, HttpResponseMessage> file) => new(req =>
        req.RequestUri!.AbsolutePath.EndsWith("/api/cache/status", StringComparison.Ordinal) ? Status() : file(req));

    private static ConsoleCache Cache(HttpMessageHandler handler, ConsoleCacheMode mode = ConsoleCacheMode.Auto) =>
        new(ConsoleUri, "jeton-agent", mode, catalogs: true, handler);

    private static byte[] Bytes(int n, int seed = 1)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Unique(string ext) => $"test-{Guid.NewGuid():N}{ext}";
    private static string DellUrl(string name) => $"https://downloads.dell.com/FOLDER1/{name}";

    private static Downloader MakeDownloader(ConsoleCache? cache, Stub direct) => new(new TrustPolicy(new Settings()), cache, new HttpClient(direct));

    // ------------------------------------------------------------------ fichiers

    [Fact]
    public async Task Console_copy_matching_the_published_hash_is_used_without_direct_download()
    {
        var official = Bytes(50_000);
        var console = Console(_ => File(official));
        var direct = new Stub(_ => throw new InvalidOperationException("pas de téléchargement direct attendu"));
        var path = await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(official));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Empty(direct.Requests);
        var asked = Assert.Single(console.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/cache/file", StringComparison.Ordinal));
        Assert.Equal("Bearer jeton-agent", asked.Headers.Authorization!.ToString());
        Assert.Contains(Uri.EscapeDataString("https://downloads.dell.com/FOLDER1/"), asked.RequestUri!.Query);
        Assert.Contains("sha256=" + Sha256(official), asked.RequestUri!.Query);
    }

    [Fact]
    public async Task Tampered_console_copy_is_rejected_and_replaced_by_the_official_file()
    {
        var official = Bytes(50_000, 1);
        var tampered = Bytes(50_000, 2); // même taille, contenu altéré par une console compromise
        var console = Console(_ => File(tampered));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(official) });
        var path = await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")), md5: Convert.ToHexString(MD5.HashData(official)));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Single(direct.Requests);
    }

    [Theory]
    [InlineData("https://download.lenovo.com/pccbbs/mobiles/n32ul16w.exe")]                // paquet Lenovo sans SHA-256 (CRC court)
    [InlineData("https://download.microsoft.com/download/x/SurfacePro9_Win11.msi")]        // fichier Microsoft sans empreinte publiée
    [InlineData("https://downloads.dell.com/FOLDER1/setup.exe")]
    public async Task File_without_an_independently_obtained_hash_never_goes_through_the_console(string url)
    {
        // Une console compromise servirait, à la place du fichier demandé, n'importe quel AUTRE fichier validement signé
        // du même éditeur (ancienne version vulnérable d'un installeur Lenovo : BYOVD). Sans empreinte : direct.
        var official = Bytes(20_000, 3);
        var signedByMicrosoft = await System.IO.File.ReadAllBytesAsync(typeof(object).Assembly.Location);
        var console = Console(_ => File(signedByMicrosoft));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(official) });
        var path = await MakeDownloader(Cache(console), direct).DownloadAsync(url, Unique(Path.GetExtension(url)));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(0, console.Count("/api/cache/file"));
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task Console_copy_is_accepted_only_with_the_published_hash_never_by_its_signature()
    {
        var tmp = Path.Combine(AppPaths.Cache, Unique(".exe"));
        await System.IO.File.WriteAllBytesAsync(tmp, Bytes(100));
        Assert.Contains("empreinte", await Downloader.VerifyConsoleCopyAsync(tmp, null, null, null, default));
        Assert.Contains("empreinte", await Downloader.VerifyConsoleCopyAsync(tmp, new string('A', 64), null, null, default));
        Assert.Null(await Downloader.VerifyConsoleCopyAsync(tmp, Sha256(Bytes(100)), null, null, default));

        // Fichier réellement et validement signé « O=Microsoft Corporation », pour une adresse Microsoft : refusé sans
        // empreinte (la 4.0.0 l'acceptait sur sa seule signature).
        var signed = Path.Combine(AppPaths.Cache, Unique(".exe"));
        System.IO.File.Copy(typeof(object).Assembly.Location, signed);
        Assert.True(SignatureVerifier.Verify(signed).IsValid);
        Assert.NotNull(await Downloader.VerifyConsoleCopyAsync(signed, null, null, null, default));
    }

    [Fact]
    public void Revocation_policy_refuses_revoked_certificates_but_tolerates_offline_checks()
    {
        Assert.Equal(SignatureVerifier.RevocationOutcome.Valid, SignatureVerifier.ClassifyRevocation(0));
        Assert.Equal(SignatureVerifier.RevocationOutcome.Rejected, SignatureVerifier.ClassifyRevocation(unchecked((int)0x800B010C))); // CERT_E_REVOKED
        Assert.Equal(SignatureVerifier.RevocationOutcome.Rejected, SignatureVerifier.ClassifyRevocation(unchecked((int)0x80092010))); // CRYPT_E_REVOKED
        Assert.Equal(SignatureVerifier.RevocationOutcome.Unavailable, SignatureVerifier.ClassifyRevocation(unchecked((int)0x80092013))); // hors ligne
        Assert.Equal(SignatureVerifier.RevocationOutcome.Unavailable, SignatureVerifier.ClassifyRevocation(unchecked((int)0x800B010E)));
        Assert.Equal(SignatureVerifier.RevocationOutcome.Rejected, SignatureVerifier.ClassifyRevocation(unchecked((int)0x800B0100))); // non signé
    }

    [Fact]
    public async Task Files_the_client_cannot_verify_alone_never_go_through_the_console()
    {
        var console = Console(_ => throw new InvalidOperationException("la console ne doit pas être sollicitée"));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(10)) });
        await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".zip")));
        await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")));
        Assert.Empty(console.Requests);
        Assert.True(Install.Downloader.CanVerifyIndependently(null, null, "abc"));
        Assert.True(Install.Downloader.CanVerifyIndependently(null, "abc", null));
        Assert.False(Install.Downloader.CanVerifyIndependently(null, null, null));
        Assert.False(Install.Downloader.CanVerifyIndependently("", " ", null));
    }

    [Fact]
    public async Task Unreachable_console_falls_back_to_direct_and_is_left_alone_for_a_while()
    {
        var console = new Stub(_ => throw new HttpRequestException("Connexion refusée"));
        var cache = Cache(console);
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(1000)) });
        var downloader = MakeDownloader(cache, direct);
        await downloader.DownloadAsync(DellUrl(Unique(".exe")), sha1: Convert.ToHexString(SHA1.HashData(Bytes(1000))));
        var asked = console.Requests.Count;
        await downloader.DownloadAsync(DellUrl(Unique(".exe")), sha1: Convert.ToHexString(SHA1.HashData(Bytes(1000))));
        Assert.Equal(asked, console.Requests.Count); // plus sollicitée pendant 5 minutes
        Assert.Equal(2, direct.Requests.Count);
        Assert.NotNull(cache.UnavailableReason);
    }

    [Fact]
    public async Task Stalled_console_transfer_falls_back_to_direct()
    {
        var official = Bytes(100_000);
        var console = Console(_ => File(official, "wait", new StallingStream(official, 10_000)));
        var cache = Cache(console);
        cache.StallTimeout = TimeSpan.FromMilliseconds(300);
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(official) });
        var path = await MakeDownloader(cache, direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(official));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task Interrupted_transfer_of_a_cached_file_resumes_with_a_range_request()
    {
        var official = Bytes(200_000);
        var console = Console(req =>
        {
            if (req.Headers.Range is { } range)
            {
                var from = (int)range.Ranges.Single().From!.Value;
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(official[from..]) };
                partial.Content.Headers.ContentLength = official.Length - from;
                Assert.Equal($"\"{Sha256(official)}\"", req.Headers.IfRange!.EntityTag!.Tag);
                return partial;
            }
            return File(official, "hit", new FailingStream(official, 80_000));
        });
        var direct = new Stub(_ => throw new InvalidOperationException("pas de téléchargement direct attendu"));
        var path = await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(official));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(2, console.Count("/api/cache/file"));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "bypass")]   // cache plein, disque, trop de téléchargements
    [InlineData(HttpStatusCode.Forbidden, "refused")]           // hors sources officielles de la console
    [InlineData(HttpStatusCode.BadGateway, "error")]            // source injoignable depuis la console
    public async Task Console_refusals_fall_back_for_that_file_only(HttpStatusCode code, string header)
    {
        var data = Bytes(500);
        var console = Console(_ => Error(code, header));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        var downloader = MakeDownloader(Cache(console), direct);
        await downloader.DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(data));
        await downloader.DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(data));
        Assert.Equal(2, console.Count("/api/cache/file")); // toujours sollicitée pour les fichiers suivants
        Assert.Equal(2, direct.Requests.Count);
    }

    [Fact]
    public async Task Console_older_than_4_0_is_detected_and_skipped()
    {
        var console = new Stub(_ => Error(HttpStatusCode.NotFound)); // « Route d'API inconnue »
        var cache = Cache(console);
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(10)) });
        await MakeDownloader(cache, direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(Bytes(10)));
        await MakeDownloader(cache, direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(Bytes(10)));
        Assert.Equal(1, console.Count("/api/cache/status"));
        Assert.Equal(0, console.Count("/api/cache/file"));
        Assert.Contains("4.0", cache.UnavailableReason);
        Assert.Null(await cache.GetStatusAsync());
    }

    [Fact]
    public async Task Cache_disabled_on_the_console_is_respected()
    {
        var console = new Stub(req => req.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal) ? Status(enabled: false) : File(Bytes(1)));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(10)) });
        await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")), sha256: Sha256(Bytes(10)));
        Assert.Equal(1, console.Count("/api/cache/status"));
        Assert.Equal(0, console.Count("/api/cache/file"));
    }

    [Theory]
    [InlineData("off", "https://console", "jeton", false)]
    [InlineData("auto", "https://console", null, false)]      // sans jeton : pas de cache
    [InlineData("auto", null, "jeton", false)]                // sans console
    [InlineData("auto", "http://192.168.1.10", "jeton", false)] // HTTP hors localhost : refusé comme pour ServerClient
    [InlineData("auto", "https://console", "jeton", true)]
    [InlineData(null, "https://console", "jeton", true)]
    [InlineData("ON", "https://console", "jeton", true)]
    public void Settings_decide_whether_the_console_cache_is_used(string? mode, string? url, string? token, bool expected)
    {
        var settings = new Settings { UseConsoleCache = mode!, ServerUrl = url, ServerToken = token };
        Assert.Equal(expected, ConsoleCache.From(settings) is not null);
        Assert.Equal("auto", new Settings().UseConsoleCache);
        Assert.True(new Settings().ConsoleCacheCatalogs);
        Assert.True(ConsoleCache.IsValidMode("Auto"));
        Assert.False(ConsoleCache.IsValidMode("peut-être"));
    }

    [Fact]
    public void Client_trusts_exactly_the_shared_official_host_list()
    {
        Assert.Same(OfficialHosts.Download, TrustPolicy.BuiltInHosts);
        var trust = new TrustPolicy(new Settings());
        foreach (var host in OfficialHosts.Download.Where(h => !h.StartsWith('*')))
            Assert.True(trust.IsTrustedHost(new Uri($"https://{host}/x")));
    }

    [Fact]
    public async Task Server_status_line_reads_the_console_statistics()
    {
        var status = await Cache(Console(_ => Error(HttpStatusCode.NotFound))).GetStatusAsync();
        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.Equal(0.93, status.HitRatio);
        Assert.Equal(13314398617, status.SizeBytes);
    }

    // ------------------------------------------------------------------ catalogues

    private static HttpResponseMessage Catalog(string body, DateTimeOffset fetchedAt, string cache = "hit")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        r.Headers.TryAddWithoutValidation("X-SpokaPilote-Cache", cache);
        r.Headers.TryAddWithoutValidation("X-SpokaPilote-Fetched-At", fetchedAt.ToString("O"));
        return r;
    }

    private static HttpResponseMessage CatalogBytes(byte[] body, DateTimeOffset fetchedAt)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentLength = body.Length;
        r.Headers.TryAddWithoutValidation("X-SpokaPilote-Cache", "hit");
        r.Headers.TryAddWithoutValidation("X-SpokaPilote-Fetched-At", fetchedAt.ToString("O"));
        return r;
    }

    [Fact]
    public async Task Signed_cab_catalog_from_the_console_is_dated_from_its_fetch_at_the_source()
    {
        // .cab signé par l'éditeur attendu de son domaine (Microsoft pour download.microsoft.com) : relayé et daté.
        var signed = await System.IO.File.ReadAllBytesAsync(typeof(object).Assembly.Location);
        var fetched = DateTimeOffset.UtcNow.AddHours(-2);
        var console = Console(_ => CatalogBytes(signed, fetched));
        var name = Unique(".cab");
        var url = $"https://download.microsoft.com/download/catalog/{name}";
        var cache = Cache(console);
        // Même version servie directement par la source il y a peu (le fichier de test peut avoir été signé il y a plus de 30 jours).
        cache.Freshness = new CatalogFreshness(FreshnessFile());
        cache.Freshness.RecordDirect(new Uri(url), SignatureVerifier.GetSigningTime(typeof(object).Assembly.Location));
        using (ConsoleCache.UseForTest(cache))
        {
            var path = await Http.GetCachedFileAsync(url, name, TimeSpan.FromHours(24));
            Assert.Equal(signed, await System.IO.File.ReadAllBytesAsync(path));
            Assert.InRange(System.IO.File.GetLastWriteTimeUtc(path), fetched.UtcDateTime.AddSeconds(-2), fetched.UtcDateTime.AddSeconds(2));
            await Http.GetCachedFileAsync(url, name, TimeSpan.FromHours(24)); // encore frais : pas de nouvelle demande
            Assert.Equal(1, console.Count("/api/cache/catalog"));
        }
    }

    [Theory]
    [InlineData("https://download.lenovo.com/catalog/20XW_Win11.xml")]                 // catalogue Lenovo (non signé)
    [InlineData("https://download.lenovo.com/pccbbs/mobiles/n32ww12w_2_.xml")]        // descripteur : lignes de commande
    [InlineData("https://download.lenovo.com/cdrt/td/catalogv2.xml")]                 // packs Lenovo
    [InlineData("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup")]
    [InlineData("https://www.catalog.update.microsoft.com/DownloadDialog.aspx")]
    [InlineData("https://www.microsoft.com/en-us/download/details.aspx?id=105847")]   // page Surface (hors domaines)
    public async Task Unsigned_metadata_is_never_requested_from_the_console(string url)
    {
        // Ces métadonnées désignent les installeurs, leurs empreintes et leurs lignes de commande et rien ne les
        // authentifie : une console compromise ne doit pas pouvoir les fournir. Lues directement à la source.
        var console = Console(_ => Catalog("<Package><Install><Cmdline>x.exe &amp; calc</Cmdline></Install></Package>", DateTimeOffset.UtcNow));
        var uri = new Uri(url);
        Assert.False(ConsoleCache.CanRelayCatalog(uri));
        Assert.Null(await Cache(console).TryCatalogFileAsync(uri, Path.Combine(AppPaths.Cache, Unique(".tmp"))));
        Assert.Empty(console.Requests);
        Assert.True(ConsoleCache.CanRelayCatalog(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab")));
        Assert.True(ConsoleCache.CanRelayCatalog(new Uri("https://hpia.hpcloud.hp.com/ref/8723/8723_64_11.0.23h2.cab")));
    }

    [Fact]
    public async Task Cab_catalog_signed_by_another_vendor_is_rejected()
    {
        // Signature Microsoft valide, mais CatalogPC.cab doit être signé par Dell.
        var signed = await System.IO.File.ReadAllBytesAsync(typeof(object).Assembly.Location);
        var console = Console(_ => CatalogBytes(signed, DateTimeOffset.UtcNow));
        var target = Path.Combine(AppPaths.Cache, Unique(".cab"));
        Assert.Null(await Cache(console).TryCatalogFileAsync(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), target));
        Assert.False(System.IO.File.Exists(target));
        Assert.Equal(1, console.Count("/api/cache/catalog"));
    }

    // ------------------------------------------------------------------ erreurs locales

    [Fact]
    public async Task Locked_local_file_falls_back_to_direct_without_blaming_the_console()
    {
        var data = Bytes(5000);
        var console = Console(_ => File(data));
        var cache = Cache(console);
        var locked = Path.Combine(AppPaths.Cache, Unique(".part"));
        await using (new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.False(await cache.TryDownloadFileAsync(new Uri(DellUrl("a.exe")), locked, Sha256(data), null, null));
        Assert.Null(cache.UnavailableReason); // la console reste sollicitée
        var free = Path.Combine(AppPaths.Cache, Unique(".part"));
        Assert.True(await cache.TryDownloadFileAsync(new Uri(DellUrl("b.exe")), free, Sha256(data), null, null));
        Assert.Equal(data, await System.IO.File.ReadAllBytesAsync(free));
    }

    [Fact]
    public async Task Access_denied_on_the_local_file_falls_back_to_direct()
    {
        var console = Console(_ => File(Bytes(100)));
        var cache = Cache(console);
        var directory = Directory.CreateDirectory(Path.Combine(AppPaths.Cache, Unique(".part"))).FullName; // ouverture : accès refusé
        Assert.False(await cache.TryDownloadFileAsync(new Uri(DellUrl("c.exe")), directory, null, null, "abc"));
        Assert.Null(cache.UnavailableReason);

        var signed = await System.IO.File.ReadAllBytesAsync(typeof(object).Assembly.Location);
        var catalogs = Cache(Console(_ => CatalogBytes(signed, DateTimeOffset.UtcNow)));
        var lockedCab = Path.Combine(AppPaths.Cache, Unique(".cab"));
        await using (new FileStream(lockedCab, FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.Null(await catalogs.TryCatalogFileAsync(new Uri("https://download.microsoft.com/download/c.cab"), lockedCab));
        Assert.Null(catalogs.UnavailableReason);
    }

    [Fact]
    public async Task Catalog_said_missing_by_the_console_is_asked_at_the_source()
    {
        // « Absent de la source » n'est qu'une affirmation de la console : une console compromise ferait ainsi disparaître
        // la référence HP d'une plateforme. null = l'appelant (Http.GetCachedFileAsync) la demande directement à HP.
        var console = Console(_ =>
        {
            var r = Error(HttpStatusCode.NotFound, "hit");
            r.Headers.TryAddWithoutValidation("X-SpokaPilote-Origin-Status", "404");
            return r;
        });
        var target = Path.Combine(AppPaths.Cache, Unique(".cab"));
        Assert.Null(await Cache(console).TryCatalogFileAsync(new Uri("https://hpia.hpcloud.hp.com/ref/8A7F/8A7F_64_11.0.26h1.cab"), target));
        Assert.Equal(1, console.Count("/api/cache/catalog"));
    }

    // ------------------------------------------------------------------ rejeu d'un ancien catalogue signé

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string FreshnessFile() => Path.Combine(AppPaths.Cache, "freshness-" + Unique(".json"));

    /// <summary>Catalogue .cab validement signé par Microsoft (bibliothèque du runtime), et sa date de signature réelle.</summary>
    private static (byte[] Bytes, DateTimeOffset SignedAt) SignedCatalog()
    {
        var path = typeof(object).Assembly.Location;
        var signedAt = SignatureVerifier.GetSigningTime(path);
        Assert.NotNull(signedAt);
        return (System.IO.File.ReadAllBytes(path), signedAt.Value);
    }

    private static async Task<bool> RelayedAsync(ConsoleCache cache, Uri official)
    {
        var target = Path.Combine(AppPaths.Cache, Unique(".cab"));
        var result = await cache.TryCatalogFileAsync(official, target);
        Assert.Equal(result is not null, System.IO.File.Exists(target)); // refusé : rien n'est gardé
        return result is { Status: ConsoleCatalogStatus.Ok };
    }

    [Fact]
    public void Signing_time_is_read_from_the_authenticode_timestamp()
    {
        var (_, signedAt) = SignedCatalog();
        Assert.InRange(signedAt, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow.AddDays(1));
        var unsigned = Path.Combine(AppPaths.Cache, Unique(".cab"));
        System.IO.File.WriteAllBytes(unsigned, Bytes(1000));
        Assert.Null(SignatureVerifier.GetSigningTime(unsigned));
        Assert.Null(SignatureVerifier.GetSigningTime(Path.Combine(AppPaths.Cache, Unique(".cab")))); // absent
    }

    [Fact]
    public async Task Recently_signed_catalog_is_relayed_and_becomes_the_reference()
    {
        var (bytes, signedAt) = SignedCatalog();
        var official = new Uri($"https://download.microsoft.com/download/catalog/{Unique(".cab")}");
        var clock = new FixedClock(signedAt.AddDays(3));
        var cache = new ConsoleCache(ConsoleUri, "jeton-agent", ConsoleCacheMode.Auto, catalogs: true, Console(_ => CatalogBytes(bytes, clock.Now)), clock)
        {
            Freshness = new CatalogFreshness(FreshnessFile(), clock),
        };
        Assert.True(await RelayedAsync(cache, official));
        Assert.True(cache.Freshness.IsOlderThanAccepted(official, signedAt.AddSeconds(-1)));
        Assert.False(cache.Freshness.IsOlderThanAccepted(official, signedAt));
    }

    [Fact]
    public async Task Catalog_older_than_the_last_accepted_one_is_refused_as_a_replay()
    {
        // La source a déjà servi au poste un catalogue signé APRÈS celui que la console resservirait : rejeu refusé,
        // même récent et validement signé.
        var (bytes, signedAt) = SignedCatalog();
        var official = new Uri($"https://download.microsoft.com/download/catalog/{Unique(".cab")}");
        var clock = new FixedClock(signedAt.AddDays(2));
        var state = FreshnessFile();
        var cache = new ConsoleCache(ConsoleUri, "jeton-agent", ConsoleCacheMode.Auto, catalogs: true, Console(_ => CatalogBytes(bytes, clock.Now)), clock)
        {
            Freshness = new CatalogFreshness(state, clock),
        };
        cache.Freshness.RecordDirect(official, signedAt.AddDays(1));
        Assert.False(await RelayedAsync(cache, official));
        Assert.Contains("plus ancien que le dernier accepté", cache.Freshness.CheckConsoleCopy(official, signedAt));
        // État conservé d'une exécution à l'autre (même fichier), quelle que soit la casse de l'adresse.
        Assert.NotNull(new CatalogFreshness(state, clock).CheckConsoleCopy(new Uri(official.AbsoluteUri.ToUpperInvariant().Replace("HTTPS:", "https:")), signedAt));
    }

    [Fact]
    public async Task Catalog_older_than_the_maximum_age_needs_a_recent_confirmation_by_the_source()
    {
        var (bytes, signedAt) = SignedCatalog();
        var official = new Uri($"https://download.microsoft.com/download/catalog/{Unique(".cab")}");
        var clock = new FixedClock(signedAt + CatalogFreshness.MaxAge + TimeSpan.FromDays(10));
        var console = Console(_ => CatalogBytes(bytes, clock.Now)); // la console prétend l'avoir obtenu à l'instant
        var cache = new ConsoleCache(ConsoleUri, "jeton-agent", ConsoleCacheMode.Auto, catalogs: true, console, clock)
        {
            Freshness = new CatalogFreshness(FreshnessFile(), clock),
        };
        // Jamais confirmé par la source : direct.
        Assert.False(await RelayedAsync(cache, official));
        Assert.Contains("30 jours", cache.Freshness.CheckConsoleCopy(official, signedAt));

        // La source sert directement cette même version (catalogue rarement republié, comme CatalogPC.cab) : la console
        // peut la relayer pendant 30 jours à compter de cette confirmation...
        cache.Freshness.RecordDirect(official, signedAt);
        Assert.True(await RelayedAsync(cache, official));
        clock.Now += CatalogFreshness.MaxAge - TimeSpan.FromDays(1);
        Assert.True(await RelayedAsync(cache, official));
        // ... puis il faut une nouvelle confirmation directe.
        clock.Now += TimeSpan.FromDays(2);
        Assert.False(await RelayedAsync(cache, official));
    }

    [Fact]
    public void Catalog_without_signing_time_is_never_relayed()
    {
        var freshness = new CatalogFreshness(FreshnessFile());
        Assert.Contains("date de signature", freshness.CheckConsoleCopy(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), null));
        freshness.RecordDirect(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), null); // sans date : rien n'est retenu
        Assert.NotNull(freshness.CheckConsoleCopy(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), null));
    }

    [Fact]
    public async Task Unsigned_cab_catalog_from_the_console_is_rejected()
    {
        var console = Console(_ => File(Bytes(4000), "hit"));
        var target = Path.Combine(AppPaths.Cache, Unique(".cab"));
        var result = await Cache(console).TryCatalogFileAsync(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), target);
        Assert.Null(result); // l'appelant télécharge alors directement depuis Dell
        Assert.False(System.IO.File.Exists(target));
    }

    [Fact]
    public async Task Catalogs_are_not_requested_when_disabled_on_the_client()
    {
        var console = Console(_ => Catalog("x", DateTimeOffset.UtcNow));
        var cache = new ConsoleCache(ConsoleUri, "jeton", ConsoleCacheMode.Auto, catalogs: false, console);
        Assert.Null(await cache.TryCatalogSearchAsync("PCI\\VEN_8086&DEV_15F3"));
        Assert.Null(await cache.TryCatalogFileAsync(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), Path.Combine(AppPaths.Cache, Unique(".cab"))));
        Assert.Empty(console.Requests);
    }

    // ------------------------------------------------------------------ Microsoft Update Catalog

    private const string DialogUpdateId = "55e4c288-1590-4fed-b4c1-248ebd64da45";
    private static readonly byte[] CatalogCab = Bytes(30_000, 7);

    private static string CatalogUrl(byte[] content) =>
        $"https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2026/01/16270d4d-ca9d-4fb6-a0f4-e4354f3f98bb_{Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant()}.cab";

    private static string Dialog(string url, string? digest = null) => $"""
        downloadInformation[0] = new Object();
        downloadInformation[0].updateID ='{DialogUpdateId}';
        downloadInformation[0].enTitle ='Realtek Driver Update (10.79.50.1003)';
        downloadInformation[0].files = new Array();
        downloadInformation[0].files[0] = new Object();
        downloadInformation[0].files[0].url = '{url}';
        downloadInformation[0].files[0].digest = '{digest ?? Convert.ToBase64String(SHA1.HashData(CatalogCab))}';
        downloadInformation[0].files[0].sha256 = '{Convert.ToBase64String(SHA256.HashData(CatalogCab))}';
        """;

    [Fact]
    public async Task Catalog_download_links_are_always_resolved_directly_at_Microsoft()
    {
        // La console répondrait par un lien de son choix (SHA-1 de son choix dans le nom) : elle n'est jamais interrogée.
        var forged = Bytes(30_000, 8);
        var console = Console(req => Catalog(Dialog(CatalogUrl(forged), Convert.ToBase64String(SHA1.HashData(forged))), DateTimeOffset.UtcNow));
        var microsoft = new Stub(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("www.catalog.update.microsoft.com", req.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Dialog(CatalogUrl(CatalogCab))) };
        });
        using (ConsoleCache.UseForTest(Cache(console)))
        {
            var urls = await MicrosoftCatalogClient.GetDownloadUrlsAsync(DialogUpdateId, new HttpClient(microsoft), default);
            Assert.Equal(CatalogUrl(CatalogCab), Assert.Single(urls));
        }
        Assert.Empty(console.Requests);
        Assert.Single(microsoft.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => MicrosoftCatalogClient.GetDownloadUrlsAsync("x\"},{\"updateID\":\"y", new HttpClient(microsoft), default));
    }

    [Fact]
    public void Catalog_link_whose_name_disagrees_with_the_published_digest_is_dropped()
    {
        Assert.Single(MicrosoftCatalogClient.ParseDownloadDialog(Dialog(CatalogUrl(CatalogCab))));
        Assert.Empty(MicrosoftCatalogClient.ParseDownloadDialog(Dialog(CatalogUrl(CatalogCab), Convert.ToBase64String(SHA1.HashData(Bytes(10))))));
        Assert.Empty(MicrosoftCatalogClient.ParseDownloadDialog(Dialog("https://evil.example/x_0000000000000000000000000000000000000000.cab")));
    }

    [Fact]
    public async Task Catalog_package_served_by_the_console_is_checked_against_the_SHA1_obtained_from_Microsoft()
    {
        // La console sert d'autres octets que le paquet publié : la copie est refusée (SHA-1 du lien obtenu chez
        // Microsoft) et le fichier officiel est téléchargé directement.
        var url = Assert.Single(MicrosoftCatalogClient.ParseDownloadDialog(Dialog(CatalogUrl(CatalogCab))));
        var console = Console(_ => File(Bytes(30_000, 9)));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CatalogCab) });
        var path = await MakeDownloader(Cache(console), direct).DownloadAsync(url, Unique(".cab"), sha1: MicrosoftCatalogClient.Sha1FromUrl(url));
        Assert.Equal(CatalogCab, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(1, console.Count("/api/cache/file"));
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task Rate_limited_search_reported_by_the_console_pauses_the_client()
    {
        var console = Console(_ =>
        {
            var r = Error((HttpStatusCode)429, "throttled");
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return r;
        });
        try
        {
            using (ConsoleCache.UseForTest(Cache(console)))
                await Assert.ThrowsAsync<CatalogRateLimitedException>(() => MicrosoftCatalogClient.SearchOnceAsync("PCI\\VEN_TEST&DEV_" + Guid.NewGuid().ToString("N")[..4], default));
            Assert.True(MicrosoftCatalogClient.IsRateLimited);
            Assert.InRange(MicrosoftCatalogClient.RateLimitedUntil, DateTime.UtcNow.AddSeconds(100), DateTime.UtcNow.AddSeconds(125));
        }
        finally
        {
            MicrosoftCatalogClient.ResetRateLimit();
        }
    }

    [Fact]
    public async Task Throttle_page_relayed_by_a_console_is_never_cached_locally()
    {
        var query = "PCI\\VEN_TEST&DEV_" + Guid.NewGuid().ToString("N")[..6];
        var console = Console(_ => Catalog("<html>Too many search requests</html>", DateTimeOffset.UtcNow, "bypass"));
        try
        {
            using (ConsoleCache.UseForTest(Cache(console)))
                await Assert.ThrowsAsync<CatalogRateLimitedException>(() => MicrosoftCatalogClient.SearchOnceAsync(query, default));
            Assert.False(System.IO.File.Exists(Path.Combine(AppPaths.Cache, "catalog", AppPaths.SafeName(query, 120) + ".json")));
        }
        finally
        {
            MicrosoftCatalogClient.ResetRateLimit();
        }
    }

    [Fact]
    public async Task Search_results_from_the_console_are_parsed_and_cached_locally()
    {
        var query = "PCI\\VEN_TEST&DEV_" + Guid.NewGuid().ToString("N")[..6];
        const string page = """
            <tr id="0a1b2c3d-1111-2222-3333-444455556666_R1"><td id="x_C1_R1">Intel - Net - 1.2.3</td><td id="x_C2_R1">Windows 11 Client, version 24H2 and later, Servicing Drivers</td>
            <td id="x_C3_R1">Drivers (Networking)</td><td id="x_C4_R1">11/20/2026</td><td id="x_C5_R1">1.2.3</td><td id="x_C6_R1"><span id="x_originalSize">1000</span></td></tr>
            """;
        var fetched = DateTimeOffset.UtcNow.AddHours(-1);
        var console = Console(_ => Catalog(page, fetched));
        using (ConsoleCache.UseForTest(Cache(console)))
        {
            var entries = await MicrosoftCatalogClient.SearchOnceAsync(query, default);
            Assert.Equal("Intel - Net - 1.2.3", Assert.Single(entries).Title);
            Assert.Equal(1, console.Count("mscatalog-search"));
            Assert.Single(await MicrosoftCatalogClient.SearchOnceAsync(query, default)); // cache local
            Assert.Equal(1, console.Count("mscatalog-search"));
        }
    }

    // ------------------------------------------------------------------ flux simulés

    /// <summary>Flux qui s'arrête définitivement après quelques octets (console figée).</summary>
    private sealed class StallingStream(byte[] data, int stallAt) : Stream
    {
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= stallAt) await Task.Delay(Timeout.Infinite, cancellationToken);
            var n = Math.Min(buffer.Length, stallAt - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Flux coupé après <paramref name="failAt"/> octets (connexion perdue).</summary>
    private sealed class FailingStream(byte[] data, int failAt) : Stream
    {
        private int _position;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= failAt) throw new IOException("connexion perdue (simulée)");
            var n = Math.Min(buffer.Length, failAt - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
