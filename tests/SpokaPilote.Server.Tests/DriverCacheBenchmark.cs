using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpokaPilote.Server.Cache;
using Xunit.Abstractions;

namespace SpokaPilote.Server.Tests;

/// <summary>Mesure lancée à la demande seulement (SPOKAPILOTE_CACHE_BENCH=1) : trop longue pour l'intégration continue.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPOKAPILOTE_CACHE_BENCH") != "1")
            Skip = "Mesure du cache de pilotes : SPOKAPILOTE_CACHE_BENCH=1 pour la lancer.";
    }
}

/// <summary>
/// Banc d'essai du cache : N postes demandent en même temps le même gros fichier à une vraie console (Kestrel, boucle
/// locale) ; la « source officielle » est un serveur local que seul ce test substitue à downloads.dell.com, par le
/// gestionnaire HTTP de la console (aucune exception à la liste officielle dans le produit).
/// Variables : SPOKAPILOTE_CACHE_BENCH_MB (500), SPOKAPILOTE_CACHE_BENCH_CLIENTS (200), SPOKAPILOTE_CACHE_BENCH_ORIGIN_MBPS (0 = illimité).
/// </summary>
public sealed class DriverCacheBenchmark(ITestOutputHelper output)
{
    private const string OfficialUrl = "https://downloads.dell.com/FOLDER-BENCH/Video_Driver_BENCH_WIN64_31.0.101.5590_A00.EXE";

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [BenchFact]
    public async Task Many_clients_request_the_same_large_file()
    {
        var megabytes = Env("SPOKAPILOTE_CACHE_BENCH_MB", 500);
        var clients = Env("SPOKAPILOTE_CACHE_BENCH_CLIENTS", 200);
        var originMbps = Env("SPOKAPILOTE_CACHE_BENCH_ORIGIN_MBPS", 0);
        var length = megabytes * 1024L * 1024;

        // Contenu synthétique : un bloc de 1 Mo pseudo-aléatoire répété (empreinte calculée une fois).
        var block = new byte[1 << 20];
        new Random(42).NextBytes(block);
        string sha256;
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (var i = 0; i < megabytes; i++) h.AppendData(block);
            sha256 = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- source « officielle » locale
        long originRequests = 0, originBytes = 0;
        var origin = WebApplication.CreateBuilder();
        origin.WebHost.UseUrls("http://127.0.0.1:0");
        origin.Logging.ClearProviders();
        await using var originApp = origin.Build();
        originApp.MapGet("/bench.bin", async (HttpContext http) =>
        {
            Interlocked.Increment(ref originRequests);
            http.Response.ContentLength = length;
            http.Response.ContentType = "application/octet-stream";
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < megabytes; i++)
            {
                await http.Response.Body.WriteAsync(block, http.RequestAborted);
                Interlocked.Add(ref originBytes, block.Length);
                if (originMbps > 0)
                {
                    var due = TimeSpan.FromSeconds((i + 1) * 8.0 / originMbps);
                    if (due > sw.Elapsed) await Task.Delay(due - sw.Elapsed, http.RequestAborted);
                }
            }
        });
        await originApp.StartAsync();
        var originAddress = new Uri(originApp.Urls.First());

        // ---------------------------------------------------------------- console (vrai Kestrel)
        var dataDir = Path.Combine(Path.GetTempPath(), "sp-cache-bench-" + Guid.NewGuid().ToString("N"));
        var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SPOKAPILOTE_DATA_DIR", dataDir)
                .UseSetting(Security.TokenService.AdminVariable, TestServerHost.AdminToken)
                .UseSetting(Security.TokenService.AgentVariable, TestServerHost.AgentToken)
                .UseSetting("SPOKAPILOTE_CACHE_MAX_GB", "50")
                .UseSetting("Logging:LogLevel:Default", "Warning");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new DriverCacheOrigin(new Redirect(originAddress, new SocketsHttpHandler())));
                s.AddSingleton(sp => new DriverCacheStore(sp.GetRequiredService<ServerOptions>(), sp.GetRequiredService<DriverCacheSettingsService>(),
                    sp.GetRequiredService<DriverCacheOrigin>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<DriverCacheStore>>(), disk: _ => (1L << 42, 1L << 43)));
            });
        });
        factory.UseKestrel(0);
        factory.StartServer();
        var console = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        output.WriteLine($"Console : {console} ; source : {originAddress} ; {megabytes} Mo × {clients} postes" +
                         (originMbps > 0 ? $" ; source limitée à {originMbps} Mbit/s" : ""));

        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = clients + 10, AutomaticDecompression = DecompressionMethods.None })
            {
                BaseAddress = console,
                Timeout = TimeSpan.FromMinutes(30),
            };
            http.DefaultRequestHeaders.Authorization = new("Bearer", TestServerHost.AgentToken);
            var route = $"api/cache/file?url={Uri.EscapeDataString(OfficialUrl)}&sha256={sha256}";

            foreach (var round in new[] { "1er passage (cache vide : un téléchargement partagé)", "2e passage (fichier en cache)" })
            {
                var originBefore = Interlocked.Read(ref originRequests);
                var originBytesBefore = Interlocked.Read(ref originBytes);
                long peakWorkingSet = 0;
                using var sampling = new CancellationTokenSource();
                var sampler = Task.Run(async () =>
                {
                    while (!sampling.IsCancellationRequested)
                    {
                        peakWorkingSet = Math.Max(peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);
                        try { await Task.Delay(250, sampling.Token); } catch (OperationCanceledException) { }
                    }
                });
                var firstBytes = new double[clients];
                var durations = new double[clients];
                var verified = 0;
                var wall = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, clients).Select(async i =>
                {
                    var sw = Stopwatch.StartNew();
                    using var response = await http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync();
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    using var hash = i < 2 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                    int n;
                    var first = true;
                    while ((n = await body.ReadAsync(buffer)) > 0)
                    {
                        if (first)
                        {
                            firstBytes[i] = sw.Elapsed.TotalMilliseconds;
                            first = false;
                        }
                        hash?.AppendData(buffer, 0, n);
                        total += n;
                    }
                    durations[i] = sw.Elapsed.TotalSeconds;
                    Assert.Equal(length, total);
                    if (hash is not null)
                    {
                        Assert.Equal(sha256, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                        Interlocked.Increment(ref verified);
                    }
                }));
                wall.Stop();
                sampling.Cancel();
                await sampler;
                Array.Sort(durations);
                Array.Sort(firstBytes);
                double P(double[] a, double q) => a[Math.Min(a.Length - 1, (int)Math.Ceiling(q * a.Length) - 1)];
                var servedGb = clients * (double)length / 1073741824;
                output.WriteLine($"{round} :");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  durée totale {wall.Elapsed.TotalSeconds:0.0} s ; {servedGb:0.0} Go servis, débit agrégé {servedGb * 1024 / wall.Elapsed.TotalSeconds:0} Mo/s"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  par poste : premier octet p50 {P(firstBytes, 0.5):0} ms / p95 {P(firstBytes, 0.95):0} ms ; fin p50 {P(durations, 0.5):0.0} s / p95 {P(durations, 0.95):0.0} s / max {durations[^1]:0.0} s"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  source officielle : {Interlocked.Read(ref originRequests) - originBefore} requête(s), {(Interlocked.Read(ref originBytes) - originBytesBefore) / 1048576.0:0} Mo ; empreinte vérifiée par {verified} postes"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  mémoire du processus (console + postes + source) : pic {peakWorkingSet / 1048576.0:0} Mo"));
            }
            Assert.Equal(1, Interlocked.Read(ref originRequests));
            var stats = factory.Services.GetRequiredService<DriverCacheStore>().Stats();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Statistiques de la console : {stats.Hits} réussites / {stats.Misses} échec(s) ({stats.HitRatio:P1}), {stats.BytesServedFromCache / 1073741824.0:0.0} Go évités"));
        }
        finally
        {
            await factory.DisposeAsync();
            await originApp.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Envoie vers la source locale ce que la console croit demander à downloads.dell.com (tests seulement).</summary>
    private sealed class Redirect(Uri target, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(target, "/bench.bin");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
