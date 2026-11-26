using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SpokaPilote.LoadTest;

// Test de charge de la console centrale. Exemples :
//   SpokaPilote.LoadTest seed   --url http://localhost:18080 --agent <jeton> --machines 20000 --history 20 --concurrency 64
//   SpokaPilote.LoadTest groups --url http://localhost:18080 --admin <jeton> --machines 20000
//   SpokaPilote.LoadTest bench  --url http://localhost:18080 --admin <jeton> --agent <jeton> --machines 20000 --api paged --duration 120 --container sp-lt
//   SpokaPilote.LoadTest burst  --url http://localhost:18080 --agent <jeton> --machines 20000 --concurrency 200 --duration 60
// Toutes les données sont fictives (noms PC-Sxx-nnnnn, identifiants dérivés du numéro du poste).

var mode = args.FirstOrDefault() ?? "help";
var opts = Options.Parse(args.Skip(1).ToArray());
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
try
{
    return mode switch
    {
        "seed" => await Scenarios.SeedAsync(opts),
        "groups" => await Scenarios.GroupsAsync(opts),
        "bench" => await Scenarios.BenchAsync(opts),
        "burst" => await Scenarios.BurstAsync(opts),
        "sample" => Scenarios.Sample(opts),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Erreur : {ex}");
    return 1;
}

static int Help()
{
    Console.WriteLine("Modes : seed | groups | bench | burst | sample (voir l'en-tête de Program.cs).");
    return 2;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            o._values[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
        return o;
    }

    public string Str(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;
    public string? Str(string key) => _values.GetValueOrDefault(key);
    public int Int(string key, int fallback) => _values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public double Dbl(string key, double fallback) => _values.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public bool Flag(string key) => _values.TryGetValue(key, out var v) && v != "false";
}

internal static class Scenarios
{
    private static HttpClient Client(Options o, int connections)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler) { BaseAddress = new Uri(o.Str("url", "http://localhost:18080")), Timeout = TimeSpan.FromSeconds(o.Int("timeout", 120)) };
    }

    // ------------------------------------------------------------------ envoi d'un rapport

    private static async Task<(int Status, double Ms, TimeSpan? RetryAfter)> UploadAsync(HttpClient http, string agentToken, byte[] gzipped, int index,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/reports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ReportGenerator.MachineIp(index));
        var content = new ByteArrayContent(gzipped);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, ct);
            await response.Content.ReadAsByteArrayAsync(ct);
            return ((int)response.StatusCode, sw.Elapsed.TotalMilliseconds, response.Headers.RetryAfter?.Delta);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, sw.Elapsed.TotalMilliseconds, null);
        }
    }

    // ------------------------------------------------------------------ seed : création du parc

    /// <summary>
    /// Crée le parc : <c>history</c> tours ; à chaque tour, chaque poste envoie un rapport (le plus ancien d'abord, un jour
    /// d'écart entre deux rapports). 3 % des postes cessent d'envoyer 8 jours avant la fin (« non vus »).
    /// </summary>
    public static async Task<int> SeedAsync(Options o)
    {
        var machines = o.Int("machines", 20000);
        var history = o.Int("history", 20);
        var concurrency = o.Int("concurrency", 64);
        var fromRound = o.Int("from-round", 0);
        var agent = o.Str("agent") ?? throw new ArgumentException("--agent requis");
        var generator = new ReportGenerator(machines);
        using var http = Client(o, concurrency);
        var recorder = new Recorder();
        var end = DateTimeOffset.UtcNow.AddHours(-1);
        long rawBytes = 0, sentBytes = 0, done = 0;
        var total = Stopwatch.StartNew();

        for (var round = fromRound; round < history; round++)
        {
            var order = Enumerable.Range(0, machines).OrderBy(i => (i * 2654435761u + (uint)round) % 1000003).ToArray();
            var sw = Stopwatch.StartNew();
            var roundStart = done;
            using var progress = new Timer(_ =>
                Console.WriteLine($"  tour {round + 1}/{history} : {done - roundStart}/{machines} — {(done - roundStart) / sw.Elapsed.TotalSeconds:0} rapports/s"),
                null, 15000, 15000);
            await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
            {
                if (i % 33 == 5 && round >= history - 8) return; // poste qui ne donne plus signe de vie
                var at = end.AddDays(-(history - 1 - round)).AddMinutes(-(i % 600));
                var json = generator.Generate(i, round, at);
                var gz = ReportGenerator.Gzip(json);
                Interlocked.Add(ref rawBytes, json.Length);
                Interlocked.Add(ref sentBytes, gz.Length);
                for (var attempt = 0; ; attempt++)
                {
                    var (status, ms, retry) = await UploadAsync(http, agent, gz, i, ct);
                    recorder.Add(status == 200 ? "POST /api/reports" : $"POST /api/reports (tentative {attempt + 1})", ms, status, gz.Length);
                    if (status == 200 || attempt >= 8 || status is 400 or 401 or 403 or 413) break;
                    await Task.Delay(retry is { } r && r < TimeSpan.FromSeconds(10) ? r : TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
                }
                Interlocked.Increment(ref done);
            });
            Console.WriteLine($"Tour {round + 1}/{history} terminé en {sw.Elapsed.TotalSeconds:0} s ({(done - roundStart) / sw.Elapsed.TotalSeconds:0} rapports/s).");
        }
        var elapsed = total.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"Parc créé : {machines} postes × {history} rapports, {done} envois en {elapsed:0} s — {done / elapsed:0.0} rapports/s.");
        Console.WriteLine($"Taille moyenne d'un rapport : {Report.Size(rawBytes / Math.Max(done, 1))} (envoyé gzip : {Report.Size(sentBytes / Math.Max(done, 1))}).");
        Console.WriteLine(Report.Table(recorder.Summaries(), elapsed));
        if (o.Str("container") is { } c) Console.WriteLine(await DockerSampler.DatabaseFiles(c));
        return 0;
    }

    // ------------------------------------------------------------------ groupes

    /// <summary>Groupes réalistes : un par site (règle sur le nom), par constructeur, par sous-réseau, et un groupe manuel.</summary>
    public static async Task<int> GroupsAsync(Options o)
    {
        var admin = o.Str("admin") ?? throw new ArgumentException("--admin requis");
        using var http = Client(o, 8);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        async Task<long?> Create(string name, string match, params (string Field, string Op, string Value)[] conditions)
        {
            var body = JsonSerializer.Serialize(new
            {
                name,
                rules = new { match, conditions = conditions.Select(c => new { field = c.Field, @operator = c.Op, value = c.Value }) },
            });
            using var r = await http.PostAsync("api/groups", new StringContent(body, Encoding.UTF8, "application/json"));
            var text = await r.Content.ReadAsStringAsync();
            Console.WriteLine($"{(int)r.StatusCode} {name}");
            return r.IsSuccessStatusCode ? JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt64() : null;
        }
        for (var s = 0; s < ReportGenerator.Sites; s++) await Create($"Site {s:D2}", "all", ("name", "startsWith", $"PC-S{s:D2}-"));
        await Create("Dell", "all", ("manufacturer", "contains", "Dell"));
        await Create("ThinkPad", "all", ("model", "contains", "ThinkPad"));
        await Create("HP EliteBook", "all", ("model", "wildcard", "*EliteBook*"));
        await Create("Windows 10", "all", ("os", "contains", "Windows 10"));
        await Create("Sous-réseau 10.3", "all", ("subnet", "inSubnet", "10.3.0.0/16"));
        await Create("Siège (sites 0 à 4)", "any", ("name", "startsWith", "PC-S00-"), ("name", "startsWith", "PC-S01-"),
            ("name", "startsWith", "PC-S02-"), ("name", "startsWith", "PC-S03-"), ("name", "startsWith", "PC-S04-"));
        var manual = await Create("Pilote VIP (manuel)", "all");
        if (manual is { } id)
        {
            var names = Enumerable.Range(0, Math.Min(500, o.Int("machines", 20000))).Select(i => ReportGenerator.MachineName(i * 37 % o.Int("machines", 20000)));
            using var r = await http.PostAsync($"api/groups/{id}/members",
                new StringContent(JsonSerializer.Serialize(new { add = names }), Encoding.UTF8, "application/json"));
            Console.WriteLine($"{(int)r.StatusCode} membres manuels");
        }
        return 0;
    }

    // ------------------------------------------------------------------ bench : charge mixte

    /// <summary>
    /// Charge mixte pendant <c>duration</c> secondes : agents qui interrogent leur file (débit fixe, aléa), envois de
    /// rapports (débit fixe), et des utilisateurs de la console qui enchaînent les pages (boucle fermée).
    /// </summary>
    public static async Task<int> BenchAsync(Options o)
    {
        var machines = o.Int("machines", 20000);
        var duration = TimeSpan.FromSeconds(o.Int("duration", 120));
        var agentRate = o.Dbl("agent-rate", machines / 600.0);   // un passage toutes les 10 minutes par poste
        var uploadRate = o.Dbl("upload-rate", 2);
        var users = o.Int("users", 5);
        var think = TimeSpan.FromMilliseconds(o.Int("think-ms", 300));
        var api = o.Str("api", "legacy");
        var admin = o.Str("admin") ?? throw new ArgumentException("--admin requis");
        var agent = o.Str("agent") ?? throw new ArgumentException("--agent requis");
        var generator = new ReportGenerator(machines);
        using var http = Client(o, 2000);
        var recorder = new Recorder();
        await using var sampler = new DockerSampler(o.Str("container"));

        // Groupes existants (pour les pages de groupe).
        var groupIds = new List<long>();
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/groups");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var r = await http.SendAsync(req);
            if (r.IsSuccessStatusCode)
                foreach (var g in JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.EnumerateArray()) groupIds.Add(g.GetProperty("id").GetInt64());
        }
        Console.WriteLine($"Charge : {agentRate:0.0} interrogations d'agent/s, {uploadRate:0.0} rapports/s, {users} utilisateurs, API « {api} », {groupIds.Count} groupes, {duration.TotalSeconds:0} s.");

        using var cts = new CancellationTokenSource(duration);
        var rng = new Random(1);
        var inflight = 0;
        var background = new List<Task>();

        // Débit fixe (boucle ouverte) : une requête toutes les 1/rate secondes, sans attendre la précédente.
        Task OpenLoop(double rate, Func<int, Task> action) => Task.Run(async () =>
        {
            if (rate <= 0) return;
            var sw = Stopwatch.StartNew();
            long n = 0;
            var tasks = new List<Task>();
            while (!cts.IsCancellationRequested)
            {
                var due = TimeSpan.FromSeconds(n / rate);
                var wait = due - sw.Elapsed;
                if (wait > TimeSpan.Zero) { try { await Task.Delay(wait, cts.Token); } catch (OperationCanceledException) { break; } }
                n++;
                if (Volatile.Read(ref inflight) > 1500) { recorder.Add("(abandon : trop de requêtes en cours)", 0, 0, 0); continue; }
                int index;
                lock (rng) index = rng.Next(machines);
                Interlocked.Increment(ref inflight);
                tasks.Add(Task.Run(async () => { try { await action(index); } finally { Interlocked.Decrement(ref inflight); } }));
                tasks.RemoveAll(t => t.IsCompleted);
            }
            await Task.WhenAll(tasks);
        });

        background.Add(OpenLoop(agentRate, async i =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/agent/jobs");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent);
            req.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine-Id", ReportGenerator.MachineId(i));
            req.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine", ReportGenerator.MachineName(i));
            await Timed(http, req, recorder, "GET /api/agent/jobs (agent)", CancellationToken.None);
        }));
        var round = 1000;
        background.Add(OpenLoop(uploadRate, async i =>
        {
            var gz = ReportGenerator.Gzip(generator.Generate(i, Interlocked.Increment(ref round), DateTimeOffset.UtcNow));
            var (status, ms, _) = await UploadAsync(http, agent, gz, i, CancellationToken.None);
            recorder.Add("POST /api/reports (envoi)", ms, status, gz.Length);
        }));

        // Pages de la console : chaque utilisateur enchaîne la liste, dans un ordre décalé.
        var pages = ConsolePages(api, machines, groupIds);
        for (var u = 0; u < users; u++)
        {
            var offset = u * 3;
            background.Add(Task.Run(async () =>
            {
                var local = new Random(offset + 11);
                for (var k = offset; !cts.IsCancellationRequested; k++)
                {
                    var (label, path) = pages[k % pages.Count];
                    var url = path(local);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
                    req.Headers.AcceptEncoding.ParseAdd("gzip");
                    await Timed(http, req, recorder, label, CancellationToken.None);
                    try { await Task.Delay(think, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }));
        }
        await Task.WhenAll(background);

        Console.WriteLine();
        Console.WriteLine(Report.Table(recorder.Summaries(), duration.TotalSeconds));
        if (o.Str("container") is { } c)
        {
            Console.WriteLine($"Mémoire du conteneur : max {sampler.MaxMemoryMb:0} Mo, fin {sampler.LastMemoryMb:0} Mo ; CPU moyen {sampler.AverageCpu:0} %, max {sampler.MaxCpu:0} %.");
            Console.WriteLine(await DockerSampler.DatabaseFiles(c));
        }
        if (o.Str("out") is { } path) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recorder.Summaries().ToDictionary(x => x.Label, x => x.Summary)));
        return 0;
    }

    private static List<(string Label, Func<Random, string> Path)> ConsolePages(string api, int machines, List<long> groups)
    {
        string Machine(Random r) => Uri.EscapeDataString(ReportGenerator.MachineName(r.Next(machines)));
        long Group(Random r) => groups.Count == 0 ? 1 : groups[r.Next(groups.Count)];
        var legacy = new List<(string, Func<Random, string>)>
        {
            ("GET /api/fleet (vue d'ensemble, complète)", _ => "api/fleet"),
            ("GET /api/machines (liste complète)", _ => "api/machines"),
            ("GET /api/machines/{nom} (fiche)", r => $"api/machines/{Machine(r)}"),
            ("GET /api/groups", _ => "api/groups"),
            ("GET /api/groups/{id}", r => $"api/groups/{Group(r)}"),
            ("GET /api/fleet?group= (vue d'un groupe)", r => $"api/fleet?group={Group(r)}"),
            ("GET /api/machines?group= (liste d'un groupe)", r => $"api/machines?group={Group(r)}"),
            ("GET /api/jobs?state=all&limit=500", _ => "api/jobs?state=all&limit=500"),
        };
        if (api == "legacy") return legacy;
        var paged = new List<(string, Func<Random, string>)>
        {
            ("GET /api/fleet?top=12 (vue d'ensemble)", _ => "api/fleet?top=12"),
            ("GET /api/fleet?group=&top=12", r => $"api/fleet?group={Group(r)}&top=12"),
            ("GET /api/machines?page (tri état, 100)", _ => "api/machines?page=1&pageSize=100&sort=status"),
            ("GET /api/machines?page (page 50, tri nom)", _ => "api/machines?page=50&pageSize=100&sort=name&dir=desc"),
            ("GET /api/machines?q= (recherche)", r => $"api/machines?page=1&pageSize=100&q=S{r.Next(20):D2}-1&sort=lastReport&dir=desc"),
            ("GET /api/machines?status=todo&group=", r => $"api/machines?page=1&pageSize=100&status=todo&group={Group(r)}&sort=health"),
            ("GET /api/missing-drivers?limit=100", _ => "api/missing-drivers?limit=100"),
            ("GET /api/export/missing-drivers (CSV)", _ => "api/export/missing-drivers"),
            ("GET /api/export/machines (CSV, parc)", _ => "api/export/machines"),
        };
        // Les routes historiques restent mesurées (scripts, compatibilité).
        return paged.Concat(legacy).ToList();
    }

    private static async Task Timed(HttpClient http, HttpRequestMessage req, Recorder recorder, string label, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await r.Content.ReadAsByteArrayAsync(ct);
            recorder.Add(label, sw.Elapsed.TotalMilliseconds, (int)r.StatusCode, bytes.Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            recorder.Add(label, sw.Elapsed.TotalMilliseconds, 0, 0);
        }
    }

    // ------------------------------------------------------------------ burst : afflux d'envois

    /// <summary>
    /// Afflux (ex. démarrage de milliers de postes après une coupure) : <c>concurrency</c> envois simultanés en continu.
    /// Avec <c>--honor-retry</c>, chaque envoi refusé (503 + Retry-After) est retenté après le délai indiqué, comme le client.
    /// </summary>
    public static async Task<int> BurstAsync(Options o)
    {
        var machines = o.Int("machines", 20000);
        var concurrency = o.Int("concurrency", 200);
        var duration = TimeSpan.FromSeconds(o.Int("duration", 60));
        var agent = o.Str("agent") ?? throw new ArgumentException("--agent requis");
        var honorRetry = o.Flag("honor-retry");
        var generator = new ReportGenerator(machines);
        using var http = Client(o, concurrency + 10);
        var recorder = new Recorder();
        await using var sampler = new DockerSampler(o.Str("container"));
        // Rapports pré-générés (le coût de génération ne doit pas brider le débit mesuré).
        var pool = Enumerable.Range(0, 400).AsParallel().Select(i => ReportGenerator.Gzip(generator.Generate(i * 50 % machines, 5000 + i, DateTimeOffset.UtcNow))).ToArray();
        using var cts = new CancellationTokenSource(duration);
        long ok = 0;
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
        {
            for (var k = w; !cts.IsCancellationRequested; k += concurrency)
            {
                var slot = k % pool.Length;
                var (status, ms, retry) = await UploadAsync(http, agent, pool[slot], slot * 50 % machines, CancellationToken.None);
                recorder.Add("POST /api/reports (afflux)", ms, status, pool[slot].Length);
                if (status == 200) Interlocked.Increment(ref ok);
                else if (honorRetry && retry is { } r)
                {
                    try { await Task.Delay(r, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        var seconds = sw.Elapsed.TotalSeconds;
        Console.WriteLine(Report.Table(recorder.Summaries(), seconds));
        Console.WriteLine($"Rapports acceptés : {ok} en {seconds:0} s — {ok / seconds:0.0} rapports/s.");
        if (o.Str("container") is not null)
            Console.WriteLine($"Mémoire du conteneur : max {sampler.MaxMemoryMb:0} Mo ; CPU moyen {sampler.AverageCpu:0} %, max {sampler.MaxCpu:0} %.");
        return 0;
    }

    /// <summary>Écrit un rapport d'exemple (vérification de la forme et de la taille).</summary>
    public static int Sample(Options o)
    {
        var g = new ReportGenerator(o.Int("machines", 20000));
        var json = g.Generate(o.Int("index", 7), o.Int("round", 0), DateTimeOffset.UtcNow);
        var path = o.Str("out", "sample-report.json");
        File.WriteAllBytes(path, json);
        Console.WriteLine($"{path} : {Report.Size(json.Length)} (gzip : {Report.Size(ReportGenerator.Gzip(json).Length)}).");
        return 0;
    }
}
