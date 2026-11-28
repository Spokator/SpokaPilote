using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Server.Cache;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>
/// Source officielle simulée (en mémoire, jamais de réseau) : la console croit parler à downloads.dell.com, au
/// Microsoft Update Catalog... Les adresses restent celles des vrais domaines : aucune exception à la liste officielle.
/// </summary>
internal sealed class FakeOrigin : HttpMessageHandler
{
    public sealed class Resource
    {
        public int Status { get; set; } = 200;
        public byte[] Body { get; set; } = [];
        public string? Location { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        /// <summary>Le corps s'arrête à mi-chemin jusqu'à l'ouverture de cette porte (téléchargement lent).</summary>
        public TaskCompletionSource? Gate { get; set; }
        /// <summary>Le flux échoue après ce nombre d'octets (connexion coupée).</summary>
        public int FailAfter { get; set; } = -1;
        public bool OmitLength { get; set; }
    }

    public ConcurrentDictionary<string, Resource> Resources { get; } = new();
    public ConcurrentQueue<(string Method, string Url)> Requests { get; } = new();
    public Func<HttpRequestMessage, Resource?>? Dynamic { get; set; }

    public int Count(string url) => Requests.Count(r => r.Url == url);

    public Resource Add(string url, byte[] body, string contentType = "application/octet-stream")
    {
        var r = new Resource { Body = body, ContentType = contentType };
        Resources[url] = r;
        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue((request.Method.Method, url));
        if (request.Content is not null) await request.Content.ReadAsStringAsync(cancellationToken);
        var r = Dynamic?.Invoke(request) ?? (Resources.TryGetValue(url, out var known) ? known : null);
        if (r is null) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("absent") };
        var response = new HttpResponseMessage((HttpStatusCode)r.Status);
        if (r.Location is not null) response.Headers.Location = new Uri(r.Location, UriKind.RelativeOrAbsolute);
        var content = new StreamContent(new SlowStream(r.Body, r.Gate, r.FailAfter));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(r.ContentType);
        if (!r.OmitLength) content.Headers.ContentLength = r.Body.Length;
        response.Content = content;
        return response;
    }

    private sealed class SlowStream(byte[] data, TaskCompletionSource? gate, int failAfter) : Stream
    {
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (gate is not null && _position >= data.Length / 2) await gate.Task.WaitAsync(cancellationToken);
            if (failAfter >= 0 && _position >= failAfter) throw new IOException("connexion coupée (simulée)");
            var limit = gate is not null && _position < data.Length / 2 ? data.Length / 2 : data.Length;
            if (failAfter >= 0) limit = Math.Min(limit, failAfter);
            var n = Math.Min(Math.Min(buffer.Length, 256 * 1024), limit - _position);
            if (n <= 0) return 0;
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
}

public sealed class DriverCacheTests : IDisposable
{
    private const string DellFile = "https://downloads.dell.com/FOLDER123/Network_Driver_ABC_WIN_1.0_A00.EXE";
    private readonly FakeOrigin _origin = new();
    private (long Free, long Total) _disk = (1L << 40, 2L << 40);
    private readonly TestServerHost _host;

    public DriverCacheTests() => _host = CreateHost(new Dictionary<string, string>());

    private TestServerHost CreateHost(Dictionary<string, string> settings) => new(b =>
    {
        foreach (var (k, v) in settings) b.UseSetting(k, v);
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton(new DriverCacheOrigin(_origin));
            s.AddSingleton(sp => new DriverCacheStore(sp.GetRequiredService<ServerOptions>(), sp.GetRequiredService<DriverCacheSettingsService>(),
                sp.GetRequiredService<DriverCacheOrigin>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DriverCacheStore>>(),
                disk: _ => _disk, maxDownloads: 4));
        });
    });

    public void Dispose()
    {
        _host.Dispose();
        _origin.Dispose();
    }

    private static byte[] Bytes(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string FileRoute(string url, string? sha256 = null) =>
        $"/api/cache/file?url={Uri.EscapeDataString(url)}" + (sha256 is null ? "" : $"&sha256={sha256}");

    private DriverCacheStore Store => _host.Factory.Services.GetRequiredService<DriverCacheStore>();

    // ------------------------------------------------------------------ liste des domaines officiels

    [Fact]
    public void Console_and_client_share_the_same_official_host_list()
    {
        // Même fichier source compilé dans les deux produits (lien dans SpokaPilote.Server.csproj).
        Assert.Contains("downloads.dell.com", OfficialHosts.Download);
        Assert.Contains("*.download.windowsupdate.com", OfficialHosts.Download);
        Assert.True(OfficialHosts.IsOfficialDownload(new Uri("https://au.download.windowsupdate.com/c/msdownload/x.cab")));
        Assert.False(OfficialHosts.IsOfficialDownload(new Uri("https://download.windowsupdate.com.evil.example/x.cab")));
        Assert.False(OfficialHosts.IsOfficialDownload(new Uri("https://gfwsl.geforce.com/x")));   // métadonnées seulement
        Assert.True(OfficialHosts.IsOfficialMetadata(new Uri("https://gfwsl.geforce.com/x")));
        var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SpokaPilote.Core", "Infrastructure", "OfficialHosts.cs");
        var project = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "server", "SpokaPilote.Server", "SpokaPilote.Server.csproj"));
        Assert.True(File.Exists(source));
        Assert.Contains(@"src\SpokaPilote.Core\Infrastructure\OfficialHosts.cs", project);
    }

    [Theory]
    [InlineData("https://evil.example.com/driver.exe")]
    [InlineData("http://downloads.dell.com/driver.exe")]              // HTTP
    [InlineData("https://downloads.dell.com:8443/driver.exe")]        // port inhabituel
    [InlineData("https://user:pass@downloads.dell.com/driver.exe")]   // identifiants
    [InlineData("https://downloads.dell.com.evil.example/driver.exe")]
    [InlineData("https://127.0.0.1/driver.exe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://gfwsl.geforce.com/driver.exe")]              // service de métadonnées : jamais un paquet
    public async Task Refuses_anything_but_official_https_hosts(string url)
    {
        var r = await _host.Client(AgentToken).GetAsync(FileRoute(url));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("refused", r.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Empty(_origin.Requests);
    }

    [Fact]
    public async Task Redirect_to_a_non_official_host_is_refused_and_never_followed()
    {
        _origin.Resources[DellFile] = new FakeOrigin.Resource { Status = 302, Location = "https://cdn.evil.example/payload.exe" };
        var r = await _host.Client(AgentToken).GetAsync(FileRoute(DellFile));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.DoesNotContain(_origin.Requests, q => q.Url.Contains("evil", StringComparison.Ordinal));
        Assert.Equal(0, Store.Stats().Files);
    }

    [Fact]
    public async Task Redirect_between_official_hosts_is_followed()
    {
        var data = Bytes(5000);
        _origin.Resources[DellFile] = new FakeOrigin.Resource { Status = 302, Location = "https://dl.dell.com/real/file.exe" };
        _origin.Add("https://dl.dell.com/real/file.exe", data);
        var r = await _host.Client(AgentToken).GetAsync(FileRoute(DellFile));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(data, await r.Content.ReadAsByteArrayAsync());
    }

    // ------------------------------------------------------------------ authentification

    [Fact]
    public async Task Requires_the_enrollment_token()
    {
        _origin.Add(DellFile, Bytes(100));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(null).GetAsync(FileRoute(DellFile))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client("mauvais-jeton").GetAsync(FileRoute(DellFile))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).GetAsync("/api/cache/status")).StatusCode);
        // Un compte en lecture seule ne se sert pas de la console comme relais de téléchargement.
        await _host.CreateUserAsync("lecteur", "readonly");
        var reader = await _host.LoginAsync("lecteur");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(FileRoute(DellFile))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/cache/status")).StatusCode);
        // Le poste ne voit ni les réglages ni la liste des fichiers.
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AgentToken).GetAsync("/api/settings/cache")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AgentToken).PostAsync("/api/cache/purge", null)).StatusCode);
    }

    [Fact]
    public async Task Health_announces_the_capability()
    {
        var health = await ReadJson(await _host.Client(null).GetAsync("/api/health"));
        Assert.Contains(health.GetProperty("capabilities").EnumerateArray(), c => c.GetString() == DriverCacheEndpoints.Capability);
    }

    // ------------------------------------------------------------------ téléchargement unique, stockage par empreinte

    [Fact]
    public async Task Concurrent_requests_share_one_download_and_receive_identical_bytes()
    {
        var data = Bytes(3 * 1024 * 1024 + 123);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _origin.Resources[DellFile] = new FakeOrigin.Resource { Body = data, Gate = gate };
        var client = _host.Client(AgentToken);

        var requests = Enumerable.Range(0, 20).Select(_ => client.GetAsync(FileRoute(DellFile, Sha256(data)), HttpCompletionOption.ResponseHeadersRead)).ToList();
        // Tous les postes reçoivent déjà leur réponse (en-têtes, premiers octets) pendant que la console télécharge :
        // on attend qu'ils aient tous rejoint le téléchargement en cours avant de libérer la source (pas de délai fixe,
        // qui échouait par intermittence sur une machine chargée).
        var joined = Task.WhenAll(requests);
        Assert.Same(joined, await Task.WhenAny(joined, Task.Delay(TimeSpan.FromSeconds(60))));
        gate.SetResult();
        var responses = await joined;
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(data, await r.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(1, _origin.Count(DellFile));
        Assert.Contains(responses, r => r.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single() == "wait");

        // Ensuite : servi depuis le disque.
        var again = await client.GetAsync(FileRoute(DellFile));
        Assert.Equal("hit", again.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Equal(data, await again.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, _origin.Count(DellFile));
        var stats = Store.Stats();
        Assert.Equal(1, stats.FileMisses);
        Assert.Equal(20, stats.FileHits);
        Assert.True(stats.BytesServedFromCache >= 20L * data.Length);
    }

    [Fact]
    public async Task Same_content_reached_by_two_urls_is_stored_once()
    {
        var data = Bytes(200_000);
        _origin.Add(DellFile, data);
        _origin.Add("https://dl.dell.com/mirror/same.exe", data);
        var client = _host.Client(AgentToken);
        Assert.Equal(data, await client.GetByteArrayAsync(FileRoute(DellFile)));
        Assert.Equal(data, await client.GetByteArrayAsync(FileRoute("https://dl.dell.com/mirror/same.exe")));
        var stats = Store.Stats();
        Assert.Equal(2, stats.Files);
        Assert.Equal(1, stats.Blobs);
        Assert.Equal(data.Length, stats.SizeBytes);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Store.Root, "blobs"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Index_survives_a_restart()
    {
        var data = Bytes(50_000);
        _origin.Add(DellFile, data);
        Assert.Equal(data, await _host.Client(AgentToken).GetByteArrayAsync(FileRoute(DellFile)));
        Store.SaveIfDirty();
        var root = Store.Root;
        var options = new ServerOptions { DataDirectory = _host.DataDir };
        using var reopened = new DriverCacheStore(options, _host.Factory.Services.GetRequiredService<DriverCacheSettingsService>(), new DriverCacheOrigin(_origin),
            TimeProvider.System, _host.Factory.Services.GetRequiredService<ILogger<DriverCacheStore>>(), root: root + "-copie", disk: _ => _disk);
        Assert.Equal(0, reopened.Stats().Files); // dossier différent : vide
        using var same = new DriverCacheStore(options, _host.Factory.Services.GetRequiredService<DriverCacheSettingsService>(), new DriverCacheOrigin(_origin),
            _host.Clock, _host.Factory.Services.GetRequiredService<ILogger<DriverCacheStore>>(), root: root, disk: _ => _disk);
        Assert.Equal(1, same.Stats().Files);
        Assert.Equal(DriverCacheStore.LookupKind.Hit, same.BeginFile(new Uri(DellFile), ExpectedHashes.None, null).Kind);
    }

    // ------------------------------------------------------------------ empreintes

    [Fact]
    public async Task Hash_mismatch_with_the_published_hash_is_rejected_and_not_cached()
    {
        var data = Bytes(100_000);
        _origin.Add(DellFile, data);
        var wrong = new string('a', 64);
        // Le contenu ne part pas en entier : la connexion est coupée (ou la réponse incomplète) avant la fin.
        var complete = false;
        try
        {
            var r = await _host.Client(AgentToken).GetAsync(FileRoute(DellFile, wrong));
            complete = r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsByteArrayAsync()).Length == data.Length;
        }
        // Selon le moment où la console coupe la connexion, l'hôte de test le signale par une erreur d'E/S ou par
        // « The application aborted the request » (OperationCanceledException) : les deux sont le comportement attendu.
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
        }
        Assert.False(complete);
        var stats = Store.Stats();
        Assert.Equal(0, stats.Files);
        Assert.Equal(1, stats.HashMismatches);
    }

    [Fact]
    public async Task Cached_copy_that_does_not_match_the_requested_hash_is_downloaded_again()
    {
        var v1 = Bytes(10_000, 1);
        var v2 = Bytes(10_000, 2);
        _origin.Add(DellFile, v1);
        var client = _host.Client(AgentToken);
        Assert.Equal(v1, await client.GetByteArrayAsync(FileRoute(DellFile)));
        _origin.Add(DellFile, v2); // la source publie un nouveau contenu à la même adresse
        Assert.Equal(v2, await client.GetByteArrayAsync(FileRoute(DellFile, Sha256(v2))));
        Assert.Equal(2, _origin.Count(DellFile));
        Assert.Equal(1, Store.Stats().Blobs); // l'ancienne version n'est plus référencée
    }

    [Fact]
    public async Task Interrupted_origin_transfer_aborts_the_response_and_caches_nothing()
    {
        var data = Bytes(2 * 1024 * 1024);
        _origin.Resources[DellFile] = new FakeOrigin.Resource { Body = data, FailAfter = 600_000 };
        var client = _host.Client(AgentToken);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var r = await client.GetAsync(FileRoute(DellFile));
            r.EnsureSuccessStatusCode();
            var body = await r.Content.ReadAsByteArrayAsync();
            if (body.Length != data.Length) throw new IOException("incomplet");
        });
        Assert.Equal(0, Store.Stats().Files);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Store.Root, "tmp")));
    }

    // ------------------------------------------------------------------ Range (reprise)

    [Fact]
    public async Task Cached_file_supports_range_requests()
    {
        var data = Bytes(100_000);
        _origin.Add(DellFile, data);
        var client = _host.Client(AgentToken);
        await client.GetByteArrayAsync(FileRoute(DellFile));

        using var request = new HttpRequestMessage(HttpMethod.Get, FileRoute(DellFile));
        request.Headers.Range = new RangeHeaderValue(1000, 1999);
        var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
        Assert.Equal(data.AsSpan(1000, 1000).ToArray(), await r.Content.ReadAsByteArrayAsync());

        using var resume = new HttpRequestMessage(HttpMethod.Get, FileRoute(DellFile));
        resume.Headers.Range = new RangeHeaderValue(90_000, null);
        resume.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{Sha256(data)}\""));
        var rest = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        Assert.Equal(data.AsSpan(90_000).ToArray(), await rest.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, _origin.Count(DellFile));
    }

    // ------------------------------------------------------------------ taille maximale, éviction, disque

    [Fact]
    public async Task Least_recently_used_files_are_evicted_beyond_the_maximum_size()
    {
        using var host = CreateHost(new Dictionary<string, string> { ["SPOKAPILOTE_CACHE_MAX_GB"] = "0.01" }); // ~10,7 Mo
        var client = host.Client(AgentToken);
        var urls = Enumerable.Range(1, 3).Select(i => $"https://downloads.dell.com/f{i}.exe").ToList();
        foreach (var (url, i) in urls.Select((u, i) => (u, i))) _origin.Add(url, Bytes(4 * 1024 * 1024, i + 10));

        await client.GetByteArrayAsync(FileRoute(urls[0]));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await client.GetByteArrayAsync(FileRoute(urls[1]));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await client.GetByteArrayAsync(FileRoute(urls[0])); // f1 redevient le plus récent
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await client.GetByteArrayAsync(FileRoute(urls[2])); // f2, le moins récemment demandé, est supprimé

        var store = host.Factory.Services.GetRequiredService<DriverCacheStore>();
        var stats = store.Stats();
        Assert.Equal(2, stats.Files);
        Assert.True(stats.SizeBytes <= stats.MaxBytes);
        Assert.Equal(DriverCacheStore.LookupKind.Hit, store.BeginFile(new Uri(urls[0]), ExpectedHashes.None, null).Kind);
        Assert.Equal(DriverCacheStore.LookupKind.Hit, store.BeginFile(new Uri(urls[2]), ExpectedHashes.None, null).Kind);
        Assert.Equal("miss", (await client.GetAsync(FileRoute(urls[1]))).Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
    }

    [Fact]
    public async Task File_larger_than_the_cache_is_sent_back_to_direct_download()
    {
        using var host = CreateHost(new Dictionary<string, string> { ["SPOKAPILOTE_CACHE_MAX_GB"] = "0.01" });
        _origin.Add(DellFile, Bytes(11 * 1024 * 1024));
        var r = await host.Client(AgentToken).GetAsync(FileRoute(DellFile));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(0, host.Factory.Services.GetRequiredService<DriverCacheStore>().Stats().Files);
    }

    [Fact]
    public async Task Disk_guard_stops_caching_when_the_volume_is_nearly_full()
    {
        _origin.Add(DellFile, Bytes(1000));
        _disk = (3L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024); // 3 Go libres < 10 Go (MinFreeGb par défaut)
        var r = await _host.Client(AgentToken).GetAsync(FileRoute(DellFile));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("bypass", r.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Empty(_origin.Requests);
        Assert.True(Store.Stats().DiskGuardActive);

        _disk = (50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).GetAsync(FileRoute(DellFile))).StatusCode);
    }

    [Fact]
    public async Task Too_many_simultaneous_origin_downloads_are_sent_back_to_direct_download()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = _host.Client(AgentToken);
        var pending = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 4; i++)
        {
            var url = $"https://downloads.dell.com/slow{i}.exe";
            _origin.Resources[url] = new FakeOrigin.Resource { Body = Bytes(100_000, i), Gate = gate };
            pending.Add(client.GetAsync(FileRoute(url), HttpCompletionOption.ResponseHeadersRead));
        }
        await WaitUntil(() => Store.Stats().DownloadsInProgress == 4);
        _origin.Add("https://downloads.dell.com/fifth.exe", Bytes(10));
        var refused = await client.GetAsync(FileRoute("https://downloads.dell.com/fifth.exe"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        gate.SetResult();
        foreach (var p in pending) Assert.Equal(100_000, (await (await p).Content.ReadAsByteArrayAsync()).Length);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    // ------------------------------------------------------------------ catalogues

    [Fact]
    public async Task Catalogs_are_kept_for_the_ttl_then_fetched_again()
    {
        const string url = "https://downloads.dell.com/catalog/CatalogPC.cab";
        var data = Bytes(30_000);
        _origin.Add(url, data, "application/vnd.ms-cab-compressed");
        var client = _host.Client(AgentToken);
        var route = $"/api/cache/catalog?url={Uri.EscapeDataString(url)}";

        var first = await client.GetAsync(route);
        Assert.Equal("miss", first.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Equal(data, await first.Content.ReadAsByteArrayAsync());
        Assert.True(DateTimeOffset.TryParse(first.Headers.GetValues(DriverCacheEndpoints.FetchedAtHeader).Single(), out _));
        var second = await client.GetAsync(route);
        Assert.Equal("hit", second.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Equal(1, _origin.Count(url));

        _host.Clock.Advance(TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(1)));
        var third = await client.GetAsync(route);
        Assert.Equal("miss", third.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Equal(2, _origin.Count(url));
    }

    [Fact]
    public async Task Concurrent_catalog_requests_reach_the_source_once()
    {
        const string url = "https://hpia.hpcloud.hp.com/ref/8A7F/8A7F_64_11.0.24h2.cab";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _origin.Resources[url] = new FakeOrigin.Resource { Body = Bytes(40_000), Gate = gate };
        var client = _host.Client(AgentToken);
        var all = Enumerable.Range(0, 30).Select(_ => client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString(url)}")).ToList();
        await Task.Delay(200);
        gate.SetResult();
        foreach (var r in await Task.WhenAll(all)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, _origin.Count(url));
    }

    [Fact]
    public async Task Missing_catalog_is_reported_as_an_origin_404_and_remembered()
    {
        const string url = "https://hpia.hpcloud.hp.com/ref/8A7F/8A7F_64_11.0.25h2.cab";
        var client = _host.Client(AgentToken);
        var r = await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString(url)}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("404", r.Headers.GetValues(DriverCacheEndpoints.OriginStatusHeader).Single());
        await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString(url)}");
        Assert.Equal(1, _origin.Count(url));
        _host.Clock.Advance(TimeSpan.FromMinutes(61));
        await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString(url)}");
        Assert.Equal(2, _origin.Count(url));
    }

    [Fact]
    public async Task Generic_catalog_route_refuses_unofficial_hosts_and_the_microsoft_catalog()
    {
        var client = _host.Client(AgentToken);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString("https://www.example.com/catalog.xml")}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString("https://www.catalog.update.microsoft.com/Search.aspx?q=x")}")).StatusCode);
        Assert.Empty(_origin.Requests);
    }

    private const string SearchUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=PCI%5CVEN_8086%26DEV_A0F0";

    private static string ResultsPage() =>
        "<table><tr id=\"0a1b2c3d-1111-2222-3333-444455556666_R1\"><td id=\"x_C1_R1\">Intel - net - 1.0</td></tr></table>";

    [Fact]
    public async Task Microsoft_catalog_search_pages_are_cached_by_normalized_query()
    {
        _origin.Add(SearchUrl, Encoding.UTF8.GetBytes(ResultsPage()), "text/html; charset=utf-8");
        var client = _host.Client(AgentToken);
        var a = await client.GetAsync($"/api/cache/catalog/mscatalog-search?q={Uri.EscapeDataString("PCI\\VEN_8086&DEV_A0F0")}");
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Contains("_R1", await a.Content.ReadAsStringAsync());
        // Même recherche, casse et espaces différents : même entrée.
        var b = await client.GetAsync($"/api/cache/catalog/mscatalog-search?q={Uri.EscapeDataString("  pci\\ven_8086&dev_a0f0 ")}");
        Assert.Equal("hit", b.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.Single(_origin.Requests);
    }

    [Fact]
    public async Task Too_many_search_requests_page_is_never_cached_and_pauses_the_console()
    {
        var throttled = true;
        _origin.Dynamic = req => req.RequestUri!.AbsoluteUri == SearchUrl
            ? new FakeOrigin.Resource
            {
                Body = Encoding.UTF8.GetBytes(throttled ? "<html><body>Too many search requests. Please try again later.</body></html>" : ResultsPage()),
                ContentType = "text/html",
            }
            : null;
        var client = _host.Client(AgentToken);
        var route = $"/api/cache/catalog/mscatalog-search?q={Uri.EscapeDataString("PCI\\VEN_8086&DEV_A0F0")}";

        var r = await client.GetAsync(route);
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        Assert.Equal(0, Store.Stats().CatalogEntries);

        // Pendant la pause : aucune requête vers Microsoft.
        Assert.Equal((HttpStatusCode)429, (await client.GetAsync(route)).StatusCode);
        Assert.Single(_origin.Requests);

        throttled = false;
        _host.Clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        var ok = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, Store.Stats().CatalogEntries);
    }

    [Fact]
    public async Task Unexpected_catalog_page_is_relayed_but_not_cached()
    {
        _origin.Add(SearchUrl, Encoding.UTF8.GetBytes("<html>Service en maintenance</html>"), "text/html");
        var client = _host.Client(AgentToken);
        var route = $"/api/cache/catalog/mscatalog-search?q={Uri.EscapeDataString("PCI\\VEN_8086&DEV_A0F0")}";
        var r = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("bypass", r.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        await client.GetAsync(route);
        Assert.Equal(2, _origin.Requests.Count);
    }

    /// <summary>
    /// 4.0.1 : la boîte de téléchargement du Microsoft Update Catalog (fichier + empreinte attendue) et les métadonnées non
    /// signées (XML Lenovo, réponses NVIDIA) ne sont plus relayées. Les postes 4.0.0 qui les demandent encore reçoivent un
    /// refus (403 « refused ») et passent en accès direct ; rien n'est demandé à la source par la console.
    /// </summary>
    [Fact]
    public async Task Download_dialog_and_unsigned_metadata_are_never_relayed()
    {
        var originCalls = 0;
        _origin.Dynamic = _ => { originCalls++; return null; };
        var client = _host.Client(AgentToken);

        var dialog = await client.GetAsync("/api/cache/catalog/mscatalog-download?updateId=0a1b2c3d-1111-2222-3333-444455556666");
        Assert.Equal(HttpStatusCode.Forbidden, dialog.StatusCode);
        Assert.Equal("refused", dialog.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());

        foreach (var url in new[]
                 {
                     "https://download.lenovo.com/catalog/21CB_Win11.xml",
                     "https://download.lenovo.com/pccbbs/mobiles/n32uj36w_2_.xml",
                     "https://download.lenovo.com/cdrt/td/catalogv2.xml",
                 })
        {
            var r = await client.GetAsync($"/api/cache/catalog?url={Uri.EscapeDataString(url)}");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("refused", r.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        }
        Assert.Equal(0, originCalls);
        Assert.True(DriverCacheEndpoints.IsRelayableCatalog(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab")));
        Assert.False(DriverCacheEndpoints.IsRelayableCatalog(new Uri("https://download.lenovo.com/catalog/21CB_Win11.xml")));
    }

    // ------------------------------------------------------------------ réglages, purge, audit, supervision

    [Fact]
    public async Task Settings_purge_and_metrics_are_admin_only_and_audited()
    {
        var data = Bytes(10_000);
        _origin.Add(DellFile, data);
        var agent = _host.Client(AgentToken);
        await agent.GetByteArrayAsync(FileRoute(DellFile));
        await agent.GetByteArrayAsync(FileRoute(DellFile));
        var admin = _host.Client(AdminToken);

        var view = await ReadJson(await admin.GetAsync("/api/settings/cache"));
        Assert.True(view.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.Equal(1, view.GetProperty("stats").GetProperty("files").GetInt32());
        Assert.Equal(DellFile, view.GetProperty("top")[0].GetProperty("url").GetString());

        var status = await ReadJson(await agent.GetAsync("/api/cache/status"));
        Assert.Equal(0.5, status.GetProperty("hitRatio").GetDouble());
        Assert.False(status.TryGetProperty("top", out _)); // aucun nom de fichier pour les postes

        var metrics = await ReadJson(await admin.GetAsync("/api/metrics"));
        Assert.Equal(data.Length, metrics.GetProperty("driverCache").GetProperty("sizeBytes").GetInt64());
        var prometheus = await (await admin.GetAsync("/api/metrics?format=prometheus")).Content.ReadAsStringAsync();
        Assert.Contains("spokapilote_cache_hits_total 1", prometheus);

        var put = await admin.PutAsync("/api/settings/cache", Json(new { enabled = false, catalogTtlHours = 6 }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var off = await agent.GetAsync(FileRoute(DellFile));
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Equal("disabled", off.Headers.GetValues(DriverCacheEndpoints.CacheHeader).Single());
        Assert.False((await ReadJson(await agent.GetAsync("/api/cache/status"))).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/settings/cache", Json(new { catalogTtlHours = 0 }))).StatusCode);

        var purge = await admin.PostAsync("/api/cache/purge?scope=all", null);
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.Equal(0, Store.Stats().SizeBytes);

        var audit = await ReadJson(await admin.GetAsync("/api/audit?category=settings"));
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.cache");
        var purgeAudit = await ReadJson(await admin.GetAsync("/api/audit?category=cache"));
        Assert.Contains(purgeAudit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("action").GetString() == "cache.purge");
    }

    [Fact]
    public async Task Environment_variables_lock_settings()
    {
        using var host = CreateHost(new Dictionary<string, string> { ["SPOKAPILOTE_CACHE_ENABLED"] = "false", ["SPOKAPILOTE_CACHE_MAX_GB"] = "50" });
        var admin = host.Client(AdminToken);
        var view = await ReadJson(await admin.GetAsync("/api/settings/cache"));
        Assert.False(view.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.Equal(50, view.GetProperty("settings").GetProperty("maxSizeGb").GetDouble());
        Assert.Contains(view.GetProperty("locked").EnumerateArray(), f => f.GetString() == "enabled");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/settings/cache", Json(new { enabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/settings/cache", Json(new { catalogTtlHours = 24 }))).StatusCode);
    }

    // ------------------------------------------------------------------ préchargement

    [Fact]
    public async Task Prefetch_downloads_catalogs_and_proposed_packages_of_the_selected_models()
    {
        var report = JsonNode.Parse(ReportFixture.Typical("PC-01").Json())!.AsObject();
        report["candidates"]![0]!["downloadUrl"] = DellFile;
        report["candidates"]![0]!["sizeBytes"] = 2000;
        var r = await _host.Client(AgentToken).PostAsync("/api/reports", Json(report.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        _origin.Add(DellFile, Bytes(2000));
        _origin.Add("https://downloads.dell.com/catalog/CatalogPC.cab", Bytes(3000));
        _origin.Add("https://downloads.dell.com/catalog/DriverPackCatalog.cab", Bytes(3000, 7));

        var prefetch = _host.Factory.Services.GetRequiredService<DriverCachePrefetch>();
        var status = await prefetch.RunAsync(["Latitude 7420"], all: false, packages: true);
        Assert.Equal(2, status.Catalogs);
        Assert.Equal(1, status.Files);
        Assert.Equal(0, status.Failed);
        Assert.Equal(1, Store.Stats().Files);

        // L'action d'administration est consignée.
        var accepted = await _host.Client(AdminToken).PostAsync("/api/cache/prefetch", Json(new { all = true }));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client(AdminToken).PostAsync("/api/cache/prefetch", Json(new { }))).StatusCode);
    }

    [Fact]
    public void Prefetch_reads_vendor_catalogs_from_reports()
    {
        var catalogs = new HashSet<string>();
        var packages = new Dictionary<string, long?>();
        DriverCachePrefetch.Collect("""
            { "system": { "manufacturer": "HP", "model": "HP EliteBook 840 G9", "baseBoardProduct": "8A7F", "osDisplayVersion": "24H2", "osBuild": 26100 },
              "candidates": [ { "downloadUrl": "https://ftp.hp.com/pub/softpaq/sp150001-150500/sp150123.exe", "sizeBytes": 1000, "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
                              { "downloadUrl": "https://ftp.hp.com/pub/softpaq/sp150001-150500/sp150124.exe", "sizeBytes": 1000 },
                              { "downloadUrl": "https://evil.example/x.exe" },
                              { "downloadUrl": "https://ftp.hp.com/blocked.exe", "blockedReason": "politique" } ] }
            """, catalogs, packages);
        Assert.Contains("https://hpia.hpcloud.hp.com/ref/8A7F/8A7F_64_11.0.24h2.cab", catalogs);
        Assert.Contains("https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab", catalogs);
        Assert.Equal(["https://ftp.hp.com/pub/softpaq/sp150001-150500/sp150123.exe"], packages.Keys);

        // 4.0.1 : les catalogues XML Lenovo (non signés) ne sont plus relayés, donc plus préchargés.
        catalogs.Clear();
        DriverCachePrefetch.Collect("""{ "system": { "manufacturer": "LENOVO", "model": "21CBCTO1WW", "osBuild": 19045 } }""", catalogs, null);
        Assert.Empty(catalogs);
    }
}
