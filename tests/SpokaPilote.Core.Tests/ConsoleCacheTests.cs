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

    [Fact]
    public async Task Unsigned_console_copy_without_published_hash_is_rejected_by_its_signature()
    {
        // Pas d'empreinte publiée : la copie de la console doit porter une signature Authenticode valide d'un éditeur approuvé.
        var official = Bytes(20_000, 3);
        var console = Console(_ => File(Bytes(20_000, 4)));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(official) });
        var downloader = MakeDownloader(Cache(console), direct);
        var path = await downloader.DownloadAsync(DellUrl(Unique(".exe")));
        Assert.Equal(official, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(1, console.Count("/api/cache/file"));
        Assert.Single(direct.Requests);

        var tmp = Path.Combine(AppPaths.Cache, Unique(".exe"));
        await System.IO.File.WriteAllBytesAsync(tmp, Bytes(100));
        Assert.Contains("signature", await downloader.VerifyConsoleCopyAsync(tmp, null, null, null, default));
        Assert.Contains("empreinte", await downloader.VerifyConsoleCopyAsync(tmp, new string('A', 64), null, null, default));
    }

    [Fact]
    public async Task Files_the_client_cannot_verify_alone_never_go_through_the_console()
    {
        var console = Console(_ => throw new InvalidOperationException("la console ne doit pas être sollicitée"));
        var direct = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(10)) });
        await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".zip")));
        Assert.Empty(console.Requests);
        Assert.True(Install.Downloader.CanVerifyIndependently("pack.zip", null, null, "abc"));
        Assert.True(Install.Downloader.CanVerifyIndependently("setup.EXE", null, null, null));
        Assert.False(Install.Downloader.CanVerifyIndependently("pack.zip", null, null, null));
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
        await MakeDownloader(cache, direct).DownloadAsync(DellUrl(Unique(".exe")));
        await MakeDownloader(cache, direct).DownloadAsync(DellUrl(Unique(".exe")));
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
        await MakeDownloader(Cache(console), direct).DownloadAsync(DellUrl(Unique(".exe")));
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

    [Fact]
    public async Task Catalog_from_the_console_is_dated_from_its_fetch_at_the_source()
    {
        var fetched = DateTimeOffset.UtcNow.AddHours(-2);
        var console = Console(_ => Catalog("<packages/>", fetched));
        var name = Unique(".xml");
        using (ConsoleCache.UseForTest(Cache(console)))
        {
            var url = $"https://download.lenovo.com/catalog/{name}";
            var path = await Http.GetCachedFileAsync(url, name, TimeSpan.FromHours(24));
            Assert.Equal("<packages/>", await System.IO.File.ReadAllTextAsync(path));
            Assert.InRange(System.IO.File.GetLastWriteTimeUtc(path), fetched.UtcDateTime.AddSeconds(-2), fetched.UtcDateTime.AddSeconds(2));
            await Http.GetCachedFileAsync(url, name, TimeSpan.FromHours(24)); // encore frais : pas de nouvelle demande
            Assert.Equal(1, console.Count("/api/cache/catalog"));
        }
    }

    [Fact]
    public async Task Catalog_missing_at_the_source_raises_a_404_like_a_direct_download()
    {
        var console = Console(_ =>
        {
            var r = Error(HttpStatusCode.NotFound, "hit");
            r.Headers.TryAddWithoutValidation("X-SpokaPilote-Origin-Status", "404");
            return r;
        });
        using (ConsoleCache.UseForTest(Cache(console)))
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                Http.GetCachedFileAsync("https://hpia.hpcloud.hp.com/ref/8A7F/8A7F_64_11.0.26h1.cab", Unique(".cab"), TimeSpan.FromHours(24)));
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        }
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
        Assert.Null(await cache.TryCatalogTextAsync(new Uri("https://gfwsl.geforce.com/lookup?x=1")));
        Assert.Empty(console.Requests);
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
