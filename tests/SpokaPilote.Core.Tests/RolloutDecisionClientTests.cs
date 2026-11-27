using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;
using Xunit.Abstractions;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Poste 4.0 face à une console en mode « décisions » : la politique ne porte que les suspensions, les retenues sont
/// demandées proposition par proposition (POST /api/rollout/decide) puis appliquées par l'analyse ; console injoignable →
/// dernières décisions connues, retenue prudente des autres ; console antérieure (sans décisions) → comportement précédent.
/// </summary>
public sealed class RolloutDecisionClientTests : IDisposable
{
    private static readonly DateTimeOffset Available = new(2026, 12, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Local);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sp-decide-{Guid.NewGuid():N}");

    public RolloutDecisionClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temporaire */ }
    }

    /// <summary>Politique servie en mode décisions (anneau « Général ») : une suspension, une règle de l'organisation qui autorise Intel.</summary>
    private static string PolicyJson(bool decisions = true, bool holdUnknown = true) => $$"""
        {
          "schema": 1, "name": "Parc siège", "version": 7,
          "rules": [
            { "id": "anneau-suspendu-9", "action": "block", "source": "WindowsUpdate", "title": "Realtek - Audio - 6.0.9600.1", "versions": "6.0.9600.1",
              "reason": "Pilote Realtek - Audio - 6.0.9600.1 : suspendu automatiquement" },
            { "id": "urgence", "action": "allow", "title": "Intel - Display - *", "reason": "Autorisation de l'organisation" },
            { "id": "gel", "action": "block", "title": "Conexant*", "reason": "Gel audio" }
          ],
          "rollout": { "ring": "Général", "ringPosition": 3, "ringCount": 3, "delayDays": 10, "enabled": true, "conservative": false,
            "machineKnown": true, "holdUnknown": {{(holdUnknown ? "true" : "false")}}, "applyToMissingDrivers": false,
            {{(decisions ? "\"decisions\": true," : "")}} "generatedAt": "2026-11-26T09:00:00.0000000+00:00", "availability": {} }
        }
        """;

    /// <summary>Console simulée : GET /api/policy, POST /api/rollout/decide (décisions calculées par <paramref name="decide"/>).</summary>
    private sealed class FakeConsole(string policy, Func<JsonNode, JsonObject>? decide) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();
        public bool Down { get; set; }
        public bool DecideDown { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    await gz.CopyToAsync(ms, cancellationToken);
                    bytes = ms.ToArray();
                }
                body = Encoding.UTF8.GetString(bytes);
            }
            Requests.Add((request, body));
            if (Down) throw new HttpRequestException("Console injoignable");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/policy") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(policy) };
            if (path == "/api/rollout/decide")
            {
                if (DecideDown) throw new HttpRequestException("Console injoignable");
                if (decide is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(decide(JsonNode.Parse(body!)!).ToJsonString()) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Décisions de la console simulée selon le titre de la proposition.</summary>
    private static JsonObject Decide(JsonNode request) => new()
    {
        ["ring"] = "Général",
        ["decisions"] = new JsonArray(request["candidates"]!.AsArray().Select(c =>
        {
            var title = c!["title"]!.GetValue<string>();
            JsonObject d = title switch
            {
                "Intel - Display - 31.0.101.5600" => new() { ["status"] = "held", ["availableAt"] = Available.ToString("O"), ["reason"] = "Pilote … : disponible le 06/12/2026" },
                "Dell BIOS 1.24.0" => new() { ["status"] = "held", ["reason"] = "Pilote Dell BIOS 1.24.0 : disponible pour votre anneau « Général » après validation" },
                "Realtek - Audio - 6.0.9600.1" => new() { ["status"] = "suspended", ["reason"] = "Pilote Realtek - Audio - 6.0.9600.1 : suspendu automatiquement" },
                "Contoso - Camera - 2.4.0.1" => new() { ["status"] = "unknown", ["reason"] = "Pilote Contoso - Camera - 2.4.0.1 : nouveau pilote, pas encore vu par la console" },
                "Futur - Statut - 1.0" => new() { ["status"] = "quarantaine" }, // statut d'une console plus récente
                _ => new() { ["status"] = "allowed" },
            };
            return (JsonNode)d;
        }).ToArray()),
    };

    private PolicyLoader Loader(FakeConsole console) => new(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton" }, new HttpClient(console))
    {
        RefreshReferenceList = false,
        CacheFile = Path.Combine(_dir, "policy-cache.json"),
        ReferenceCacheFile = Path.Combine(_dir, "ref.json"),
        DecisionCacheFile = Path.Combine(_dir, "rollout-decisions.json"),
        Identity = () => ("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-COMPTA-01"),
    };

    private static DriverCandidate Cand(string title, string version, DriverSourceKind source = DriverSourceKind.WindowsUpdate,
        CandidateKind kind = CandidateKind.Update) => new() { Id = title, Source = source, Title = title, Version = version, Kind = kind };

    private static List<DriverCandidate> Candidates() =>
    [
        Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600"),
        Cand("Intel - Display - 31.0.101.5590", "31.0.101.5590"),
        Cand("Dell BIOS 1.24.0", "1.24.0", DriverSourceKind.Dell, CandidateKind.Firmware),
        Cand("Realtek - Audio - 6.0.9600.1", "6.0.9600.1", kind: CandidateKind.MissingDriver),
        Cand("Contoso - Camera - 2.4.0.1", "2.4.0.1"),
        Cand("Conexant Audio 1.0", "1.0"),
    ];

    private static async Task<(LoadedPolicy Policy, List<DriverCandidate> Merged)> ScanAsync(PolicyLoader loader, List<DriverCandidate> candidates)
    {
        var policy = await loader.LoadAsync();
        var (merged, _) = await DriverScanEngine.ApplyPolicyAsync(loader, policy, candidates, [], CancellationToken.None, Now);
        return (policy, merged);
    }

    [Fact]
    public async Task The_scan_asks_the_console_for_its_candidates_and_applies_each_decision()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        var candidates = Candidates();
        var alternative = Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600", DriverSourceKind.MicrosoftCatalog);
        candidates[1].Alternatives.Add(alternative);
        var (policy, merged) = await ScanAsync(Loader(console), candidates);

        // La politique est demandée en mode décisions, avec l'identité du poste.
        var get = console.Requests.First(r => r.Request.RequestUri!.AbsolutePath == "/api/policy").Request;
        Assert.Equal("1", get.Headers.GetValues("X-SpokaPilote-Rollout-Decisions").Single());
        Assert.Equal("1", get.Headers.GetValues("X-SpokaPilote-Rollout").Single());
        // Une seule demande de décisions, compressée, avec les propositions (et l'alternative) sous leur nom de source du poste.
        var (post, body) = console.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.Equal("Bearer", post.Headers.Authorization!.Scheme);
        Assert.Equal("PC-COMPTA-01", post.Headers.GetValues("X-SpokaPilote-Machine").Single());
        Assert.Equal("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", post.Headers.GetValues("X-SpokaPilote-Machine-Id").Single());
        Assert.Contains("gzip", post.Content!.Headers.ContentEncoding);
        var sent = JsonNode.Parse(body!)!["candidates"]!.AsArray();
        Assert.Equal(7, sent.Count);
        Assert.Contains(sent, c => c!["source"]!.GetValue<string>() == "MicrosoftCatalog");
        Assert.Contains(sent, c => c!["source"]!.GetValue<string>() == "Dell" && c["kind"]!.GetValue<string>() == "Firmware");

        DriverCandidate Get(string title) => merged.Single(c => c.Title == title && c.Source != DriverSourceKind.MicrosoftCatalog);
        // Retenue datée : motif à l'heure locale, l'« allow » de l'organisation ne la lève pas.
        var held = Get("Intel - Display - 31.0.101.5600");
        Assert.True(held.HeldByRollout);
        Assert.Equal(Available, held.RolloutAvailableAt);
        Assert.Equal("Général", held.RolloutRing);
        Assert.StartsWith("Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le ", held.BlockedReason);
        // Autorisée : la règle de l'organisation s'applique ensuite normalement (allow), l'alternative retenue est écartée.
        var allowed = Get("Intel - Display - 31.0.101.5590");
        Assert.Null(allowed.BlockedReason);
        Assert.Empty(allowed.Alternatives);
        // Retenue sans date, suspension (pilote manquant compris), version jamais vue.
        Assert.Contains("après validation", Get("Dell BIOS 1.24.0").BlockedReason);
        Assert.True(Get("Dell BIOS 1.24.0").HeldByRollout);
        Assert.Contains("suspendu automatiquement", Get("Realtek - Audio - 6.0.9600.1").BlockedReason);
        Assert.True(Get("Realtek - Audio - 6.0.9600.1").HeldByRollout);
        Assert.Contains("pas encore vu par la console", Get("Contoso - Camera - 2.4.0.1").BlockedReason);
        // Blocage de l'organisation : affiché comme tel.
        Assert.Equal("Gel audio (règle « gel »)", Get("Conexant Audio 1.0").BlockedReason);
        Assert.False(Get("Conexant Audio 1.0").HeldByRollout);
        Assert.Contains("décisions de la console : 5 proposition(s) retenue(s) sur 7", policy.Summary);
        Assert.Empty(policy.Messages);
    }

    [Fact]
    public async Task An_unknown_status_from_a_newer_console_is_a_hold_never_an_authorization()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        var (_, merged) = await ScanAsync(Loader(console), [Cand("Futur - Statut - 1.0", "1.0")]);
        Assert.True(merged.Single().HeldByRollout);
    }

    [Fact]
    public async Task Without_the_console_the_last_known_decisions_apply_and_new_candidates_are_held()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        await ScanAsync(Loader(console), Candidates()); // décisions mises en cache
        Assert.True(File.Exists(Path.Combine(_dir, "rollout-decisions.json")));

        // 1. Politique reçue, demande de décisions en échec : cache, et retenue prudente de la proposition jamais décidée.
        console.DecideDown = true;
        var fresh = Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700");
        var (policy, merged) = await ScanAsync(Loader(console), [.. Candidates(), fresh]);
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.Equal(Available, merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").RolloutAvailableAt);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason); // autorisée la dernière fois
        Assert.True(merged.Single(c => c.Title == fresh.Title).HeldByRollout);
        Assert.Contains("console injoignable", merged.Single(c => c.Title == fresh.Title).BlockedReason);
        Assert.Contains(policy.Messages, m => m.Contains("Dernières décisions connues appliquées à 6 proposition(s)") && m.Contains("1 autre(s) retenue(s)"));

        // 2. Console entièrement injoignable : politique ET décisions depuis le cache, sans nouvel essai réseau pour les décisions.
        console.Down = true;
        console.Requests.Clear();
        (policy, merged) = await ScanAsync(Loader(console), [.. Candidates(), fresh]);
        Assert.True(policy.FromCache);
        Assert.DoesNotContain(console.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.True(merged.Single(c => c.Title == "Realtek - Audio - 6.0.9600.1").HeldByRollout); // suspension : cache et règle
        Assert.True(merged.Single(c => c.Title == fresh.Title).HeldByRollout);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason);

        // 3. Sans aucun cache de décisions : tout ce qui est soumis aux anneaux est retenu (jamais « tout autoriser »).
        File.Delete(Path.Combine(_dir, "rollout-decisions.json"));
        (_, merged) = await ScanAsync(Loader(console), Candidates());
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").HeldByRollout);
        Assert.False(merged.Single(c => c.Title == "Realtek - Audio - 6.0.9600.1" && c.Kind == CandidateKind.MissingDriver).BlockedReason is null);
    }

    [Fact]
    public async Task In_a_ring_without_delay_a_missing_decision_does_not_hold_anything()
    {
        var console = new FakeConsole(PolicyJson(holdUnknown: false), Decide) { DecideDown = true };
        var (_, merged) = await ScanAsync(Loader(console), [Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700")]);
        Assert.Null(merged.Single().BlockedReason);
    }

    [Fact]
    public async Task An_older_console_without_decisions_keeps_the_previous_behaviour()
    {
        // Console 4.0 antérieure : pas de « decisions » dans la section, retenues dans les règles ; aucune demande de décisions.
        var json = PolicyJson(decisions: false).Replace("""{ "id": "urgence",""", """
            { "id": "anneau-retenue-12", "action": "block", "source": "WindowsUpdate", "title": "Intel - Display - 31.0.101.5600", "kind": ["Update", "Optional", "Firmware"],
              "versions": "31.0.101.5600", "reason": "Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 06/12/2026 à 10:00" },
            { "id": "anneau-famille-1", "action": "pin", "source": "WindowsUpdate", "title": "Intel - Display - *", "kind": ["Update", "Optional", "Firmware"], "pinVersion": "31.0.101.5600" },
            { "id": "urgence",
            """);
        var console = new FakeConsole(json, decide: null);
        var (policy, merged) = await ScanAsync(Loader(console), Candidates());
        Assert.DoesNotContain(console.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.Null(policy.Effective.Decisions);
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason);
        Assert.True(merged.Single(c => c.Title == "Contoso - Camera - 2.4.0.1").HeldByRollout); // famille inconnue (règles)
    }

    [Fact]
    public async Task A_policy_file_or_share_never_triggers_a_decision_request()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, """{ "schema": 1, "name": "Fichier", "rules": [] }""");
        var console = new FakeConsole(PolicyJson(), Decide);
        var loader = new PolicyLoader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton", PolicySource = path }, new HttpClient(console))
        {
            RefreshReferenceList = false, CacheFile = Path.Combine(_dir, "c.json"), ReferenceCacheFile = Path.Combine(_dir, "r.json"),
            DecisionCacheFile = Path.Combine(_dir, "d.json"),
        };
        var (_, merged) = await ScanAsync(loader, Candidates());
        Assert.Empty(console.Requests);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").BlockedReason);
    }

    [Fact]
    public void The_cache_keeps_the_most_recent_decisions_of_the_same_console_only()
    {
        var path = Path.Combine(_dir, "cache.json");
        var c = Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600");
        RolloutDecisions.Save(path, "https://console.exemple.fr/api/policy",
            new Dictionary<string, RolloutDecision> { [RolloutDecisions.Key(c)] = new() { Status = RolloutDecision.Held, DecidedAt = Now } }, Now);
        Assert.Equal(RolloutDecision.Held, RolloutDecisions.FromCacheFile(path, "https://console.exemple.fr/api/policy", [c])!.For(c)!.Status);
        Assert.Null(RolloutDecisions.FromCacheFile(path, "https://autre.exemple.fr/api/policy", [c])); // autre console : ignoré
        // Même titre en majuscules, version écrite autrement : même proposition (comme la console).
        Assert.Equal(RolloutDecisions.Key(c), RolloutDecisions.Key(Cand("INTEL - DISPLAY - 31.0.101.5600", "31.0.101.5600.0")));
        var many = Enumerable.Range(0, RolloutDecisions.MaxCacheEntries + 10).ToDictionary(i => $"k{i}", i => new RolloutDecision { DecidedAt = Now.AddMinutes(i) });
        many["vieux"] = new RolloutDecision { DecidedAt = Now.AddDays(-400) };
        RolloutDecisions.Save(path, "https://console.exemple.fr/api/policy", many, Now.AddDays(1));
        var all = JsonNode.Parse(File.ReadAllText(path))!["decisions"]!.AsObject();
        Assert.Equal(RolloutDecisions.MaxCacheEntries, all.Count);
        Assert.False(all.ContainsKey("vieux"));
        Assert.False(all.ContainsKey("k0"));
    }
}

/// <summary>
/// Mesure côté poste (à la demande) : temps de lecture de la politique et d'application aux propositions d'un poste, avant
/// (politique avec la liste des retenues de tout le parc) et après (politique réduite aux suspensions + décisions). Utilise
/// les documents enregistrés par « SpokaPilote.LoadTest rollout --save-dir » : SPOKAPILOTE_ROLLOUT_BENCH_DIR=dossier.
/// Sans cette variable, rien n'est mesuré.
/// </summary>
public sealed class RolloutClientMeasurement(ITestOutputHelper output)
{
    /// <summary>
    /// Bout en bout contre une console réelle (à la demande) : le code du poste charge sa politique, demande ses décisions et
    /// les applique aux propositions enregistrées par le banc. Variables : SPOKAPILOTE_ROLLOUT_BENCH_URL, _TOKEN (jeton
    /// d'enrôlement) et SPOKAPILOTE_ROLLOUT_BENCH_DIR (dossier du banc : decide-request.json, machine.txt). Caches dans un
    /// dossier temporaire.
    /// </summary>
    [Fact]
    public async Task Live_console_end_to_end_client_time()
    {
        if (Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_URL") is not { Length: > 0 } url ||
            Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_TOKEN") is not { Length: > 0 } token ||
            Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_DIR") is not { Length: > 0 } dirs) return;
        var dir = dirs.Split(';')[0];
        var machine = File.ReadAllLines(Path.Combine(dir, "machine.txt"));
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-request.json")))!["candidates"]!.AsArray();
        var scratch = Path.Combine(Path.GetTempPath(), $"sp-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            for (var run = 0; run < 3; run++)
            {
                var candidates = request.Select((c, i) => new DriverCandidate
                {
                    Id = $"c{i}", Source = Enum.TryParse<DriverSourceKind>(c!["source"]!.GetValue<string>(), out var s) ? s : DriverSourceKind.WindowsUpdate,
                    Title = c["title"]!.GetValue<string>(), Version = c["version"]!.GetValue<string>(), Kind = Enum.Parse<CandidateKind>(c["kind"]!.GetValue<string>()),
                }).ToList();
                var loader = new PolicyLoader(new Settings { ServerUrl = url, ServerToken = token }, new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }))
                {
                    RefreshReferenceList = false, CacheFile = Path.Combine(scratch, "policy-cache.json"), ReferenceCacheFile = Path.Combine(scratch, "ref.json"),
                    DecisionCacheFile = Path.Combine(scratch, "rollout-decisions.json"), Identity = () => (machine[1], machine[0]),
                };
                var sw = Stopwatch.StartNew();
                var policy = await loader.LoadAsync();
                var load = sw.Elapsed.TotalMilliseconds;
                var (merged, _) = await DriverScanEngine.ApplyPolicyAsync(loader, policy, candidates, [], CancellationToken.None);
                output.WriteLine($"{machine[0]} : politique {load:0} ms ({policy.Organization?.Rules.Count ?? 0} règle(s), décisions : {policy.Organization?.Rollout?.Decisions}), " +
                                 $"décisions + application {sw.Elapsed.TotalMilliseconds - load:0} ms, total {sw.Elapsed.TotalMilliseconds:0} ms ; " +
                                 $"{merged.Count(c => c.HeldByRollout)}/{merged.Count} retenue(s) ; {string.Join(" ", policy.Messages)}");
            }
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { /* temporaire */ }
        }
    }

    [Fact]
    public void Client_side_policy_application_time_from_saved_documents()
    {
        if (Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_DIR") is not { Length: > 0 } dirs) return;
        foreach (var dir in dirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var request = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-request.json")))!["candidates"]!.AsArray();
            var candidates = request.Select((c, i) => new DriverCandidate
            {
                Id = $"c{i}", Source = Enum.TryParse<DriverSourceKind>(c!["source"]!.GetValue<string>(), out var s) ? s : DriverSourceKind.WindowsUpdate,
                Title = c["title"]!.GetValue<string>(), Version = c["version"]!.GetValue<string>(),
                Kind = Enum.Parse<CandidateKind>(c["kind"]!.GetValue<string>()),
            }).ToList();
            output.WriteLine($"{dir} : {candidates.Count} proposition(s)");
            foreach (var file in new[] { "policy-40-rules.json", "policy-3x.json", "policy-40-decisions.json" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                RolloutDecisions? decisions = null;
                if (file == "policy-40-decisions.json" && File.Exists(Path.Combine(dir, "decide-response.json")) &&
                    JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-response.json")))?["decisions"] is JsonArray list && list.Count == candidates.Count)
                    decisions = new RolloutDecisions(candidates.Zip(list).GroupBy(x => RolloutDecisions.Key(x.First)).ToDictionary(g => g.Key, g => new RolloutDecision
                    {
                        Status = g.First().Second!["status"]!.GetValue<string>(), Reason = g.First().Second!["reason"]?.GetValue<string>(),
                        AvailableAt = g.First().Second!["availableAt"] is { } at ? DateTimeOffset.Parse(at.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
                    }), fromCache: false);
                var times = new List<double>();
                var held = 0;
                var accepted = json.Length <= 2 * 1024 * 1024;
                for (var run = 0; run < (json.Length > 500_000 ? 1 : 3); run++)
                {
                    var sw = Stopwatch.StartNew();
                    var parsed = DriverPolicy.Parse(json);
                    if (!parsed.IsValid) { output.WriteLine($"  {file} : invalide ({parsed.Errors.FirstOrDefault()})"); break; }
                    var policy = DriverPolicy.Combine(parsed.Policy, PolicyLoader.LoadEmbeddedReference().Policy);
                    policy.Decisions = decisions;
                    var copy = candidates.Select(c => new DriverCandidate { Id = c.Id, Source = c.Source, Title = c.Title, Version = c.Version, Kind = c.Kind }).ToList();
                    var (merged, _) = DriverScanEngine.ApplyPolicy(copy, [], policy);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    held = merged.Count(c => c.BlockedReason is not null);
                }
                if (times.Count > 0)
                    output.WriteLine($"  {file} : {json.Length / 1024.0:0} Ko ({(accepted ? "accepté" : "REFUSÉ par PolicyLoader : > 2 Mo")}), " +
                                     $"lecture + application : {times.Min():0} ms (meilleur de {times.Count}), {held} proposition(s) bloquée(s) ou retenue(s)");
            }
        }
    }
}
