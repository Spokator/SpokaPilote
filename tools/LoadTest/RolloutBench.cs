using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpokaPilote.LoadTest;

/// <summary>
/// Banc de la diffusion par anneaux (« rollout ») : ce que coûte, pour UN poste, l'application des anneaux.
/// <list type="bullet">
/// <item>politique servie à un poste 3.x (non identifié : anneau le plus prudent) — taille, nombre de règles, temps ;</item>
/// <item>politique servie à un poste 4.0 (identifié) : avec la liste des retenues (« rollout: 1 », consoles et postes 4.0
///   de développement) et en mode « décisions par proposition » (en-tête X-SpokaPilote-Rollout-Decisions) ;</item>
/// <item>POST /api/rollout/decide avec les propositions du poste (celles de son dernier rapport) : taille envoyée et reçue,
///   p50 / p95, répartition des décisions ;</item>
/// <item>pages de la console « Déploiement » (vue, liste paginée, recherche, fiche, aperçu d'un poste).</item>
/// </list>
/// <c>--setup</c> crée les anneaux suggérés, rattache les groupes (« Pilote VIP (manuel) » → Pilote, « Site 01 » →
/// Précoce, le reste → Général) et active la diffusion. <c>--save-dir</c> enregistre les documents d'un poste (mesure du
/// temps d'application côté poste : tests SpokaPilote.Core, RolloutClientMeasurement).
/// </summary>
public static class RolloutBench
{
    public const string DecisionsHeader = "X-SpokaPilote-Rollout-Decisions";

    internal static async Task<int> RunAsync(Options o)
    {
        var machines = o.Int("machines", 3000);
        var samples = Math.Min(o.Int("samples", 200), machines);
        var concurrency = o.Int("concurrency", 8);
        var round = o.Int("round", 2);
        // --candidates N : au moins N propositions par poste (parc synthétique, qui en a peu : propositions de rapports voisins).
        var minCandidates = o.Int("candidates", 0);
        var admin = o.Str("admin") ?? throw new ArgumentException("--admin requis");
        var agent = o.Str("agent") ?? throw new ArgumentException("--agent requis");
        var generator = new ReportGenerator(machines, o.Str("fleet", "synthetic"));
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency + 4 })
        {
            BaseAddress = new Uri(o.Str("url", "http://localhost:18080")), Timeout = TimeSpan.FromSeconds(o.Int("timeout", 120)),
        };
        var recorder = new Recorder();
        await using var sampler = new DockerSampler(o.Str("container"));

        if (o.Flag("setup")) await SetupAsync(http, admin);
        var elapsed = Stopwatch.StartNew();

        var summary = await GetJsonAsync(http, admin, "api/rollout/summary");
        Console.WriteLine($"Diffusion : {summary?.ToJsonString() ?? "(résumé indisponible)"}");

        // Préchauffage (JIT, premier calcul des politiques et de l'index des décisions) : non mesuré.
        await WarmUpAsync(http, agent, generator, round);

        var indices = Enumerable.Range(0, samples).Select(i => (int)((long)i * machines / samples + i % 7) % machines).Distinct().ToList();
        var docs = new Dictionary<string, DocStats>();
        long candidatesSent = 0, requestRaw = 0, requestWire = 0, responseRaw = 0, responseWire = 0;
        var statuses = new Dictionary<string, int>();
        var saveDir = o.Str("save-dir");
        if (saveDir is not null) Directory.CreateDirectory(saveDir);
        long savedScore = -1;

        await Parallel.ForEachAsync(indices, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, ct) =>
        {
            var name = ReportGenerator.MachineName(index);
            var id = ReportGenerator.MachineId(index);

            // Poste 3.x : ni identité, ni section « rollout ».
            var legacy = await GetPolicyAsync(http, agent, null, null, rollout: false, decisions: false, recorder, "GET /api/policy (poste 3.x)");
            // Poste 4.0 sans décisions (liste des retenues de son anneau).
            var rules40 = await GetPolicyAsync(http, agent, id, name, rollout: true, decisions: false, recorder, "GET /api/policy (4.0, liste des retenues)");
            // Poste 4.0 : décisions par proposition.
            var small40 = await GetPolicyAsync(http, agent, id, name, rollout: true, decisions: true, recorder, "GET /api/policy (4.0, mode décisions)");
            lock (docs)
            {
                Track(docs, "3.x", legacy);
                Track(docs, "4.0 liste", rules40);
                Track(docs, "4.0 décisions", small40);
            }

            var candidates = CandidatesOf(generator, index, round, minCandidates);
            var body = DecideBody(candidates);
            var raw = Encoding.UTF8.GetBytes(body);
            var gz = ReportGenerator.Gzip(raw);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/rollout/decide");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent);
            request.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine-Id", id);
            request.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine", name);
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            var content = new ByteArrayContent(gz);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            content.Headers.ContentEncoding.Add("gzip");
            request.Content = content;
            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, ct);
            var wire = await response.Content.ReadAsByteArrayAsync(ct);
            var ms = sw.Elapsed.TotalMilliseconds;
            recorder.Add($"POST /api/rollout/decide ({(o.Str("fleet", "synthetic"))})", ms, (int)response.StatusCode, wire.Length);
            var text = Decode(wire, response.Content.Headers.ContentEncoding.Contains("gzip"));
            lock (statuses)
            {
                candidatesSent += candidates.Count;
                requestRaw += raw.Length;
                requestWire += gz.Length;
                responseRaw += Encoding.UTF8.GetByteCount(text);
                responseWire += wire.Length;
                if (response.IsSuccessStatusCode && JsonNode.Parse(text)?["decisions"] is JsonArray list)
                    foreach (var d in list)
                    {
                        var s = d?["status"]?.GetValue<string>() ?? "?";
                        statuses[s] = statuses.GetValueOrDefault(s) + 1;
                    }
                else statuses[$"HTTP {(int)response.StatusCode}"] = statuses.GetValueOrDefault($"HTTP {(int)response.StatusCode}") + 1;
                // Documents du poste le plus chargé (anneau le plus tardif) : mesure côté poste.
                var score = (long)rules40.Raw * 1000 + candidates.Count;
                if (saveDir is not null && score > savedScore)
                {
                    savedScore = score;
                    File.WriteAllText(Path.Combine(saveDir, "policy-3x.json"), legacy.Text ?? "");
                    File.WriteAllText(Path.Combine(saveDir, "policy-40-rules.json"), rules40.Text ?? "");
                    File.WriteAllText(Path.Combine(saveDir, "policy-40-decisions.json"), small40.Text ?? "");
                    File.WriteAllText(Path.Combine(saveDir, "decide-request.json"), body);
                    File.WriteAllText(Path.Combine(saveDir, "decide-response.json"), text);
                    File.WriteAllText(Path.Combine(saveDir, "machine.txt"), $"{name}\n{id}\n");
                }
            }
        });

        // Pages « Déploiement » de la console.
        var firstId = (await GetJsonAsync(http, admin, "api/rollout/versions?status=all&limit=1"))?["items"]?[0]?["id"]?.GetValue<long>() ?? 1;
        var total = (await GetJsonAsync(http, admin, "api/rollout/versions?status=all&limit=1"))?["total"]?.GetValue<int>() ?? 0;
        var pages = new (string Label, string Path)[]
        {
            ("GET /api/rollout (vue)", "api/rollout"),
            ("GET /api/rollout/summary", "api/rollout/summary"),
            ("GET /api/rollout/versions (en cours, 50)", "api/rollout/versions?status=active&limit=50"),
            ("GET /api/rollout/versions (tout, page profonde)", $"api/rollout/versions?status=all&offset={Math.Max(0, total - 60)}&limit=50"),
            ("GET /api/rollout/versions?q= (recherche)", "api/rollout/versions?status=all&q=Intel&limit=50"),
            ("GET /api/rollout/versions/{id} (fiche)", $"api/rollout/versions/{firstId}"),
            ("GET /api/rollout/history", "api/rollout/history"),
            ("GET /api/rollout/preview?machine=", $"api/rollout/preview?machine={Uri.EscapeDataString(ReportGenerator.MachineName(indices[0]))}"),
        };
        foreach (var (label, path) in pages)
            for (var i = 0; i < o.Int("page-repeat", 20); i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
                req.Headers.AcceptEncoding.ParseAdd("gzip");
                var sw = Stopwatch.StartNew();
                using var r = await http.SendAsync(req);
                var bytes = await r.Content.ReadAsByteArrayAsync();
                recorder.Add(label, sw.Elapsed.TotalMilliseconds, (int)r.StatusCode, bytes.Length);
            }

        var preview = await GetJsonAsync(http, admin, pages[^1].Path);
        Console.WriteLine();
        Console.WriteLine(Report.Table(recorder.Summaries(), elapsed.Elapsed.TotalSeconds));
        Console.WriteLine("Documents servis (moyenne par poste) :");
        foreach (var (k, v) in docs)
            Console.WriteLine($"  {k,-15} : moyenne {Report.Size(v.Raw / Math.Max(1, v.Count))} ({Report.Size(v.Wire / Math.Max(1, v.Count))} transmis), " +
                              $"{v.Rules / Math.Max(1, v.Count)} règle(s) ; maximum {Report.Size(v.MaxRaw)} ({Report.Size(v.MaxWire)} transmis), {v.MaxRules} règle(s)");
        var n = Math.Max(1, indices.Count);
        Console.WriteLine($"Décisions : {candidatesSent / (double)n:0.0} propositions par poste ; requête {Report.Size(requestRaw / n)} ({Report.Size(requestWire / n)} gzip), " +
                          $"réponse {Report.Size(responseRaw / n)} ({Report.Size(responseWire / n)} transmis) ; " +
                          string.Join(", ", statuses.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value}")));
        if (preview is not null)
        {
            preview.AsObject().Remove("policy");
            if (preview["decisions"]?["items"] is JsonArray items && items.Count > 3)
                while (items.Count > 3) items.RemoveAt(items.Count - 1);
            Console.WriteLine($"Aperçu d'un poste : {preview.ToJsonString()}");
        }
        if (o.Str("container") is { } c)
            Console.WriteLine($"Mémoire du conteneur : max {sampler.MaxMemoryMb:0} Mo ; CPU moyen {sampler.AverageCpu:0} %, max {sampler.MaxCpu:0} %.");
        return 0;
    }

    private sealed class DocStats
    {
        public int Count;
        public long Raw, Wire, Rules;
        public int MaxRaw, MaxWire, MaxRules;
    }

    /// <summary>
    /// Propositions du dernier rapport du poste ; si <paramref name="min"/> est plus grand, complétées par celles des rapports
    /// de postes voisins (versions connues de la console) et d'un rapport futur (versions jamais vues), sans doublon.
    /// </summary>
    private static List<GenCandidate> CandidatesOf(ReportGenerator generator, int index, int round, int min)
    {
        var list = generator.Candidates(index, round, DateTimeOffset.UtcNow).ToList();
        for (var k = 1; list.Count < min && k < 200; k++)
        {
            var extra = k % 5 == 0
                ? generator.Candidates(index, round + k, DateTimeOffset.UtcNow)
                : generator.Candidates((index + k * 7) % generator.MachineCount, round, DateTimeOffset.UtcNow);
            foreach (var c in extra)
                if (list.Count < min && !list.Any(x => x.Title == c.Title && x.Version == c.Version)) list.Add(c);
        }
        return list;
    }

    /// <summary>Premiers appels (calcul des politiques de chaque anneau, index des décisions, JIT), affichés à part.</summary>
    private static async Task WarmUpAsync(HttpClient http, string agent, ReportGenerator generator, int round)
    {
        var sw = Stopwatch.StartNew();
        var warm = new Recorder();
        for (var i = 0; i < 30; i++)
        {
            var index = i * 97 % generator.MachineCount;
            var (name, id) = (ReportGenerator.MachineName(index), ReportGenerator.MachineId(index));
            await GetPolicyAsync(http, agent, null, null, false, false, warm, "préchauffage : GET /api/policy (3.x)");
            await GetPolicyAsync(http, agent, id, name, true, false, warm, "préchauffage : GET /api/policy (4.0 liste)");
            await GetPolicyAsync(http, agent, id, name, true, true, warm, "préchauffage : GET /api/policy (4.0 décisions)");
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/rollout/decide");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent);
            request.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine-Id", id);
            request.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine", name);
            request.Content = new StringContent(DecideBody(generator.Candidates(index, round, DateTimeOffset.UtcNow)), Encoding.UTF8, "application/json");
            var t = Stopwatch.StartNew();
            using var r = await http.SendAsync(request);
            await r.Content.ReadAsByteArrayAsync();
            warm.Add("préchauffage : POST /api/rollout/decide", t.Elapsed.TotalMilliseconds, (int)r.StatusCode, 0);
        }
        Console.WriteLine($"Préchauffage ({sw.Elapsed.TotalSeconds:0.0} s, premier appel de chaque type = calcul du document ou de l'index) :");
        foreach (var (label, s) in warm.Summaries()) Console.WriteLine($"  {label} : max {s.Max:0} ms, médiane {s.P50:0.0} ms");
    }

    private static void Track(Dictionary<string, DocStats> docs, string key, Doc d)
    {
        if (!docs.TryGetValue(key, out var s)) docs[key] = s = new DocStats();
        s.Count++;
        s.Raw += d.Raw;
        s.Wire += d.Wire;
        s.Rules += d.Rules;
        s.MaxRaw = Math.Max(s.MaxRaw, d.Raw);
        s.MaxWire = Math.Max(s.MaxWire, d.Wire);
        s.MaxRules = Math.Max(s.MaxRules, d.Rules);
    }

    private sealed record Doc(int Raw, int Wire, int Rules, string? Text);

    private static async Task<Doc> GetPolicyAsync(HttpClient http, string agent, string? id, string? name, bool rollout, bool decisions,
        Recorder recorder, string label)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/policy");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent);
        req.Headers.AcceptEncoding.ParseAdd("gzip");
        if (id is not null) req.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine-Id", id);
        if (name is not null) req.Headers.TryAddWithoutValidation("X-SpokaPilote-Machine", name);
        if (rollout) req.Headers.TryAddWithoutValidation("X-SpokaPilote-Rollout", "1");
        if (decisions) req.Headers.TryAddWithoutValidation(DecisionsHeader, "1");
        var sw = Stopwatch.StartNew();
        using var r = await http.SendAsync(req);
        var wire = await r.Content.ReadAsByteArrayAsync();
        recorder.Add(label, sw.Elapsed.TotalMilliseconds, (int)r.StatusCode, wire.Length);
        var text = Decode(wire, r.Content.Headers.ContentEncoding.Contains("gzip"));
        var rules = 0;
        try { rules = JsonNode.Parse(text)?["rules"]?.AsArray().Count ?? 0; } catch (JsonException) { }
        return new Doc(Encoding.UTF8.GetByteCount(text), wire.Length, rules, text);
    }

    private static string Decode(byte[] body, bool gzip)
    {
        if (!gzip) return Encoding.UTF8.GetString(body);
        using var input = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Corps envoyé par un poste 4.0 : source (nom de source du poste), titre, version, type de chaque proposition.</summary>
    public static string DecideBody(IEnumerable<GenCandidate> candidates) => new JsonObject
    {
        ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
        {
            ["source"] = SourceKind(c.Source), ["title"] = c.Title, ["version"] = c.Version, ["kind"] = KindName(c.Kind),
        }).ToArray()),
    }.ToJsonString();

    private static string SourceKind(string label) => label switch
    {
        "Windows Update" => "WindowsUpdate",
        "Microsoft Update Catalog" => "MicrosoftCatalog",
        "Dell (catalogue officiel)" => "Dell",
        "HP (catalogue officiel)" => "HP",
        "Lenovo (catalogue officiel)" => "Lenovo",
        _ => label,
    };

    private static string KindName(string label) => label switch
    {
        "Pilote manquant" => "MissingDriver",
        "Optionnel" => "Optional",
        "BIOS / Firmware" => "Firmware",
        _ => "Update",
    };

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, string token, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var r = await http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode?> SendJsonAsync(HttpClient http, string token, HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        Console.WriteLine($"{(int)r.StatusCode} {method} {path}");
        return r.IsSuccessStatusCode && text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    /// <summary>Anneaux suggérés, groupes rattachés, diffusion activée.</summary>
    private static async Task SetupAsync(HttpClient http, string admin)
    {
        await SendJsonAsync(http, admin, HttpMethod.Post, "api/rollout/rings/suggested", null);
        var overview = await GetJsonAsync(http, admin, "api/rollout") ?? throw new InvalidOperationException("GET /api/rollout impossible");
        var rings = overview["rings"]!.AsArray();
        var groups = (await GetJsonAsync(http, admin, "api/groups"))!.AsArray();
        long? Group(string name) => groups.FirstOrDefault(g => g?["name"]?.GetValue<string>() == name)?["id"]?.GetValue<long>();
        async Task Assign(int ring, string group)
        {
            var r = rings[ring]!;
            var g = Group(group);
            await SendJsonAsync(http, admin, HttpMethod.Put, $"api/rollout/rings/{r["id"]!.GetValue<long>()}", new
            {
                name = r["name"]!.GetValue<string>(), delayDays = r["delayDays"]!.GetValue<int>(), requireValidation = false,
                minSuccesses = r["minSuccesses"]!.GetValue<int>(), maxFailures = r["maxFailures"]!.GetValue<int>(),
                description = r["description"]?.GetValue<string>(), groups = g is null ? Array.Empty<long>() : [g.Value],
            });
        }
        await Assign(0, "Pilote VIP (manuel)");
        await Assign(1, "Site 01");
        await SendJsonAsync(http, admin, HttpMethod.Put, "api/rollout/settings", new
        {
            enabled = true, defaultRingId = rings[2]!["id"]!.GetValue<long>(), applyToMissingDrivers = false, familyRetentionDays = 365,
            breaker = new { enabled = true, minFailures = 3, minFailureRatePercent = 20, windowHours = 48, countRegressions = true },
        });
    }
}
