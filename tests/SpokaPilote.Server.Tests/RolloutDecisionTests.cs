using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SpokaPilote.Server.Rollout;
using Xunit.Abstractions;
using Legacy = SpokaPilote.Server.Tests.Legacy31;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Parc synthétique en mémoire : beaucoup de familles (5 sources dont une non reconnue), plusieurs versions majeures.</summary>
internal static class BigFleet
{
    public static readonly RingRecord[] Rings = [RolloutSamples.Pilot, RolloutSamples.Early, RolloutSamples.General];
    public static readonly string[] Sources = ["WindowsUpdate", "Dell", "HP", "Lenovo", "Catalogue constructeur"];

    /// <summary>
    /// <paramref name="families"/> familles de 1 à 4 versions, vues pour la première fois au cours des 30 derniers jours (une
    /// partie a fini sa diffusion, une partie est en cours), sur 1 à 3 versions majeures ; quelques suspensions et approbations.
    /// </summary>
    public static (RolloutSnapshot Snapshot, List<VersionPlan> Plans) Build(int families, int seed = 7)
    {
        var rng = new Random(seed);
        var versions = new List<RolloutVersion>();
        var now = RolloutSamples.T0;
        for (var f = 0; f < families; f++)
        {
            var source = Sources[f % Sources.Length];
            var count = 1 + rng.Next(4);
            var majors = 1 + rng.Next(3);
            for (var k = 0; k < count; k++)
            {
                var major = 10 + rng.Next(majors) * 5;
                var version = $"{major}.{rng.Next(5)}.{rng.Next(100)}.{1000 + k * 10 + rng.Next(9)}";
                var title = $"Fabricant{f} - Pilote{f} - {version}";
                if (versions.Any(v => v.Source == source && v.Title == title)) continue;
                var first = now.AddDays(-rng.NextDouble() * 30);
                versions.Add(new RolloutVersion
                {
                    Id = versions.Count + 1, Source = source, Title = title, Version = version, Family = RolloutKeys.FamilyPattern(title), Kind = "Update",
                    FirstSeen = first, LastSeen = now.AddDays(-rng.NextDouble()), Suspended = rng.Next(200) == 0,
                    SuspendReason = "suspendu automatiquement : 3 échecs", ApprovedAt = rng.Next(150) == 0 ? now.AddDays(-1) : null,
                });
            }
        }
        var s = RolloutSamples.Snapshot(Rings, versions);
        return (s, RolloutPlanner.Plan(s, now));
    }

    public static Legacy.Models.DriverSourceKind LegacySource(string source) =>
        Enum.TryParse<Legacy.Models.DriverSourceKind>(source, out var k) ? k : Legacy.Models.DriverSourceKind.MicrosoftCatalog;

    public static Legacy.Models.DriverCandidate Candidate(string source, string title, string version, string kind = "Update") => new()
    {
        Id = title + version, Source = LegacySource(source), Title = title, Version = version, Kind = Enum.Parse<Legacy.Models.CandidateKind>(kind),
    };

    public static Legacy.Policy.DriverPolicy Parse31(string json)
    {
        var parsed = Legacy.Policy.DriverPolicy.Parse(json);
        Assert.True(parsed.IsValid, string.Join(" ; ", parsed.Errors.Take(3)));
        return parsed.Policy!;
    }
}

// ====================================================================== décisions par proposition (index en mémoire)

public sealed class RolloutIndexTests
{
    private static readonly DateTimeOffset Now = RolloutSamples.T0;
    private static readonly PolicyTarget General = new(RolloutSamples.General, false);
    private static readonly PolicyTarget Pilot = new(RolloutSamples.Pilot, false);

    private static (RolloutIndex Index, RolloutSnapshot Snapshot, List<VersionPlan> Plans) Fleet()
    {
        var s = RolloutSamples.Snapshot(BigFleet.Rings,
        [
            RolloutSamples.Version(1, "31.0.101.5590", Now.AddDays(-30)),
            RolloutSamples.Version(2, "31.0.101.5600", Now.AddDays(-4)),
            RolloutSamples.Version(3, "27.20.100.9999", Now.AddDays(-200)),
            RolloutSamples.Version(4, "6.0.9600.1", Now.AddDays(-1), title: "Realtek Audio Driver", source: "Dell"),
            RolloutSamples.Version(5, "31.0.101.5500", Now.AddDays(-60), suspended: true),
        ]);
        var plans = RolloutPlanner.Plan(s, Now);
        return (new RolloutIndex(s, plans, Now), s, plans);
    }

    private static RolloutDecision D(RolloutIndex index, PolicyTarget target, string title, string version, string source = "WindowsUpdate", string kind = "Update") =>
        index.Decide(new CandidateQuery(source, title, version, kind), target, TimeZoneInfo.Utc, Now);

    [Fact]
    public void Decisions_cover_known_held_suspended_and_never_seen_versions()
    {
        var (index, _, _) = Fleet();
        var held = D(index, General, "Intel - Display - 31.0.101.5600", "31.0.101.5600");
        Assert.Equal(DecisionStatus.Held, held.Status);
        Assert.Equal(Now.AddDays(6), held.AvailableAt);
        Assert.Equal("Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 02/12/2026 à 09:00", held.Reason);
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "Intel - Display - 31.0.101.5590", "31.0.101.5590").Status);
        // Même titre en majuscules et version écrite autrement : même version (comme les règles de la politique).
        Assert.Equal(DecisionStatus.Held, D(index, General, "INTEL - DISPLAY - 31.0.101.5600", "31.0.101.5600.0").Status);
        // Jamais vue : plus récente que la dernière autorisée, nouvelle majeure, famille inconnue → retenue.
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Intel - Display - 31.0.101.5700", "31.0.101.5700").Status);
        Assert.Contains("dernière version autorisée : 31.0.101.5590", D(index, General, "Intel - Display - 31.0.101.5700", "31.0.101.5700").Reason);
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Intel - Display - 32.0.0.1", "32.0.0.1").Status);
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Contoso - Camera - 2.4.0.1", "2.4.0.1").Status);
        Assert.Contains("nouveau pilote, pas encore vu par la console", D(index, General, "Contoso - Camera - 2.4.0.1", "2.4.0.1").Reason);
        // Ancienne branche plafonnée à sa propre version ; version inconnue inférieure au plafond : autorisée (comme la règle « pin »).
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "Intel - Display - 27.20.100.9000", "27.20.100.9000").Status);
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Intel - Display - 27.20.100.10000", "27.20.100.10000").Status);
        // Famille sans aucune version autorisée pour l'anneau.
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Realtek Audio Driver", "6.0.9600.2", "Dell").Status);
        // Autre source : autre famille (inconnue).
        Assert.Equal(DecisionStatus.Unknown, D(index, General, "Intel - Display - 31.0.101.5590", "31.0.101.5590", "MicrosoftCatalog").Status);
        // Pilote manquant non soumis, version illisible non suivie.
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "Intel - Display - 31.0.101.5600", "31.0.101.5600", kind: "MissingDriver").Status);
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "Pilote", "version inconnue").Status);
        // Suspension : tous les anneaux, tous les types.
        var suspended = D(index, Pilot, "Intel - Display - 31.0.101.5500", "31.0.101.5500", kind: "MissingDriver");
        Assert.Equal(DecisionStatus.Suspended, suspended.Status);
        Assert.Contains("suspendu automatiquement", suspended.Reason);
        // Anneau pilote : tout ce qui n'est pas suspendu est autorisé, nouveautés comprises.
        Assert.Equal(DecisionStatus.Allowed, D(index, Pilot, "Intel - Display - 31.0.101.5600", "31.0.101.5600").Status);
        Assert.Equal(DecisionStatus.Allowed, D(index, Pilot, "Contoso - Camera - 2.4.0.1", "2.4.0.1").Status);
        // Poste non identifié : anneau le plus prudent.
        var cautious = D(index, new PolicyTarget(null, true), "Intel - Display - 31.0.101.5600", "31.0.101.5600");
        Assert.Equal(DecisionStatus.Held, cautious.Status);
        Assert.Contains("anneau le plus prudent", cautious.Reason);
    }

    /// <summary>
    /// Équivalence avec la politique complète lue par l'analyseur EXACT de la 3.1 : pour toute proposition d'une famille connue
    /// (versions connues, voisines, nouvelles majeures), la décision retient ⇔ la politique bloque. Seule différence attendue :
    /// une famille jamais vue est retenue par la décision (4.0) et pas par la politique (limite documentée des postes 3.x).
    /// </summary>
    [Fact]
    public void Decisions_match_the_complete_policy_read_by_a_31_client()
    {
        var (s, plans) = BigFleet.Build(400);
        var index = new RolloutIndex(s, plans, Now);
        foreach (var target in new[] { General, new PolicyTarget(RolloutSamples.Early, false), new PolicyTarget(null, true) })
        {
            var rules = RolloutPolicyBuilder.Build(s, plans, target, Now, TimeZoneInfo.Utc, new RolloutLimits(100_000, 100 * 1024 * 1024));
            Assert.Equal("complete", rules.Form);
            var p31 = BigFleet.Parse31(RolloutPolicyBuilder.Compose(null, rules, null)!);
            var at = Now.UtcDateTime.ToLocalTime();
            var checkedCount = 0;
            foreach (var v in s.Versions)
            {
                var parsed = RolloutKeys.ParseVersion(v.Version)!;
                var family = v.Title[..v.Title.LastIndexOf(" - ", StringComparison.Ordinal)];
                var probes = new[]
                {
                    v.Version, $"{parsed.Major}.{parsed.Minor}.{parsed.Build}.{parsed.Revision + 1}", $"{parsed.Major}.{parsed.Minor}.{parsed.Build}.{parsed.Revision - 1}",
                    $"{parsed.Major + 1}.0.0.0", $"{parsed.Major + 7}.0.0.0", "1.0.0.0",
                };
                foreach (var probe in probes)
                    foreach (var kind in new[] { "Update", "MissingDriver" })
                    {
                        var title = $"{family} - {probe}";
                        var decision = index.Decide(new CandidateQuery(v.Source, title, probe, kind), target, TimeZoneInfo.Utc, Now);
                        var blocked = p31.Evaluate(BigFleet.Candidate(v.Source, title, probe, kind), null, at).IsBlocked;
                        Assert.True(blocked == (decision.Status != DecisionStatus.Allowed),
                            $"{target.CacheKey} {v.Source} « {title} » {kind} : décision {decision.Status}, politique 3.1 {(blocked ? "bloque" : "autorise")}");
                        checkedCount++;
                    }
            }
            Assert.True(checkedCount > 5000);
            // Famille jamais vue : retenue par la décision (4.0), pas par la politique (3.x).
            Assert.Equal(DecisionStatus.Unknown, index.Decide(new CandidateQuery("WindowsUpdate", "Inconnu - Pilote - 1.0.0.1", "1.0.0.1", "Update"), target, TimeZoneInfo.Utc, Now).Status);
            Assert.False(p31.Evaluate(BigFleet.Candidate("WindowsUpdate", "Inconnu - Pilote - 1.0.0.1", "1.0.0.1"), null, at).IsBlocked);
        }
    }
}

// ====================================================================== politique des postes 3.x : document borné

public sealed class RolloutBoundedPolicyTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = RolloutSamples.T0;
    private static readonly PolicyTarget Cautious = new(null, true);

    private const string Org = """{ "schema": 1, "name": "Parc siège", "version": 3, "rules": [ { "id": "gel", "action": "block", "title": "Conexant*", "reason": "Gel" } ] }""";

    /// <summary>
    /// Preuve de l'invariant avec l'analyseur EXACT de la 3.1 : quel que soit le plafond, un poste 3.x bloque toute version
    /// connue qui n'est pas autorisée pour l'anneau le plus prudent (et toute version suspendue, pilotes manquants compris),
    /// et le document respecte le plafond (règles et octets).
    /// </summary>
    [Theory]
    [InlineData(4000, 300, 200)]   // tout ne tient pas : sources retenues en bloc
    [InlineData(1500, 2000, 1024)] // forme compacte ou complète
    [InlineData(4000, 2000, 1024)] // plafond par défaut
    [InlineData(300, 2000, 1024)]
    public void A_31_client_never_installs_a_known_version_not_yet_allowed_for_the_most_cautious_ring(int families, int maxRules, int maxKb)
    {
        var (s, plans) = BigFleet.Build(families);
        var limits = new RolloutLimits(maxRules, maxKb * 1024);
        var sw = Stopwatch.StartNew();
        var rules = RolloutPolicyBuilder.Build(s, plans, Cautious, Now, TimeZoneInfo.Utc, limits, reservedBytes: Org.Length);
        var buildMs = sw.ElapsedMilliseconds;
        var json = RolloutPolicyBuilder.Compose(Org, rules, null)!;
        Assert.True(rules.Rules.Count <= maxRules, $"{rules.Rules.Count} règles");
        Assert.True(Encoding.UTF8.GetByteCount(json) <= limits.MaxBytes, $"{json.Length} octets");

        sw.Restart();
        var p31 = BigFleet.Parse31(json);
        var parseMs = sw.ElapsedMilliseconds;
        Assert.Equal("gel", p31.Rules.Last().Id);
        var at = Now.UtcDateTime.ToLocalTime();
        int held = 0, completed = 0, completedBlocked = 0;
        foreach (var p in plans)
        {
            var v = p.Version;
            if (v.Suspended)
            {
                Assert.True(p31.Evaluate(BigFleet.Candidate(v.Source, v.Title, v.Version, "MissingDriver"), null, at).IsBlocked, $"suspendue : {v.DisplayName}");
                continue;
            }
            foreach (var kind in new[] { "Update", "Optional", "Firmware" })
            {
                var blocked = p31.Evaluate(BigFleet.Candidate(v.Source, v.Title, v.Version, kind), null, at).IsBlocked;
                if (!p.Completed) Assert.True(blocked, $"{v.Source} « {v.Title} » ({kind}) en cours de diffusion mais autorisée par la politique 3.x ({rules.Form})");
                else if (kind == "Update") { completed++; if (blocked) completedBlocked++; }
            }
            if (!p.Completed) held++;
        }
        // Une diffusion terminée n'est bloquée que si sa source est retenue en bloc.
        if (rules.SourceHolds.Count == 0) Assert.Equal(0, completedBlocked);
        output.WriteLine($"{families} familles, {plans.Count} versions ({held} en cours de diffusion) ; plafond {maxRules} règles / {maxKb} Ko : forme « {rules.Form} », " +
                         $"{rules.Rules.Count} règles ({rules.Holds} datées, {rules.Families} de famille, sources en bloc : {string.Join(", ", rules.SourceHolds)}), " +
                         $"{json.Length / 1024.0:0} Ko ; diffusions terminées bloquées : {completedBlocked}/{completed} ; construction {buildMs} ms, lecture 3.1 {parseMs} ms.");
    }

    [Fact]
    public void Small_fleets_keep_the_complete_document_with_one_dated_rule_per_held_version()
    {
        var (s, plans) = BigFleet.Build(40);
        var rules = RolloutPolicyBuilder.Build(s, plans, Cautious, Now, TimeZoneInfo.Utc);
        Assert.Equal("complete", rules.Form);
        Assert.Equal(plans.Count(p => !p.Version.Suspended && !p.Completed), rules.Holds);
        Assert.Equal(0, rules.UndatedHolds);
        Assert.Empty(rules.SourceHolds);
    }

    [Fact]
    public void Families_whose_rollout_is_over_are_the_first_to_go_and_held_versions_are_never_dropped()
    {
        var (s, plans) = BigFleet.Build(3000);
        var full = RolloutPolicyBuilder.Build(s, plans, Cautious, Now, TimeZoneInfo.Utc, new RolloutLimits(100_000, 50 * 1024 * 1024));
        var required = full.Rules.Count - full.Holds; // familles + suspensions : au-delà du plafond ci-dessous
        var limits = new RolloutLimits(Math.Max(100, required * 3 / 4 + full.Holds / 4), 50 * 1024 * 1024);
        var compact = RolloutPolicyBuilder.Build(s, plans, Cautious, Now, TimeZoneInfo.Utc, limits);
        Assert.Equal("compact", compact.Form);
        Assert.Empty(compact.SourceHolds);
        Assert.True(compact.OmittedFamilies > 0 || compact.UndatedHolds > 0);
        Assert.Equal(full.HeldVersions, compact.HeldVersions);
        Assert.True(compact.Rules.Count <= limits.MaxRules);
    }

    [Fact]
    public void Very_many_suspensions_are_grouped_by_source_and_still_block()
    {
        var versions = Enumerable.Range(1, 3000).Select(i => RolloutSamples.Version(i, $"1.0.{i}.0", Now.AddDays(-40), title: "Pilote " + i + " - {0}",
            source: BigFleet.Sources[i % 5], suspended: true)).ToList();
        var s = RolloutSamples.Snapshot(BigFleet.Rings, versions);
        var plans = RolloutPlanner.Plan(s, Now);
        var rules = RolloutPolicyBuilder.Build(s, plans, Cautious, Now, TimeZoneInfo.Utc);
        Assert.Equal(5, rules.Rules.Count(r => r["id"]!.GetValue<string>().StartsWith(RolloutPolicyBuilder.SuspendedPrefix)));
        Assert.Equal(3000, rules.Suspensions);
        var p31 = BigFleet.Parse31(RolloutPolicyBuilder.Compose(null, rules, null)!);
        foreach (var v in versions)
            Assert.True(p31.Evaluate(BigFleet.Candidate(v.Source, v.Title, v.Version, "MissingDriver"), null, DateTime.Now).IsBlocked);
    }
}

// ====================================================================== API : POST /api/rollout/decide, politique en mode décisions, aperçu

public sealed class RolloutDecideApiTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public RolloutDecideApiTests()
    {
        var rollout = _host.Factory.Services.GetRequiredService<RolloutService>();
        rollout.MinRebuildInterval = TimeSpan.Zero;
        rollout.Zone = TimeZoneInfo.Utc;
    }

    public void Dispose() => _host.Dispose();

    private const string Intel5590 = "Intel - Display - 31.0.101.5590";
    private const string Intel5600 = "Intel - Display - 31.0.101.5600";

    private async Task Report(int machine, string name, params RolloutReports.Cand[] candidates)
    {
        var r = await _host.Client(AgentToken).PostAsync("/api/reports", Json(RolloutReports.Json(name, machine, _host.Clock.Now, candidates)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task SetupAsync()
    {
        var admin = _host.Client(AdminToken);
        await Report(1, "PC-IT-01", new RolloutReports.Cand(Intel5590, "31.0.101.5590"));
        await Report(3, "PC-COMPTA-01");
        var group = await ReadJson(await admin.PostAsync("/api/groups", Json(new
        {
            name = "Informatique", rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-IT-" } } },
        })));
        var rings = (await ReadJson(await admin.PostAsync("/api/rollout/rings/suggested", null))).GetProperty("rings").EnumerateArray().ToList();
        var pilot = rings[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/rollout/rings/{pilot}", Json(new { name = "Pilote", delayDays = 0, groups = new[] { group.GetProperty("id").GetInt64() } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/rollout/settings", Json(new { enabled = true, breaker = new { enabled = true, minFailures = 3, minFailureRatePercent = 20, windowHours = 48 } }))).StatusCode);
        _host.Clock.Advance(TimeSpan.FromHours(1));
    }

    private static object Body(params (string Title, string Version, string Kind)[] candidates) =>
        new { candidates = candidates.Select(c => new { source = "WindowsUpdate", title = c.Title, version = c.Version, kind = c.Kind }) };

    private async Task<JsonElement> Decide(int? machine, string? name, object body, string token = AgentToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/rollout/decide") { Content = Json(body) };
        if (machine is { } m) req.Headers.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(m));
        if (name is not null) req.Headers.Add("X-SpokaPilote-Machine", name);
        var r = await _host.Client(token).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ReadJson(r);
    }

    private static string[] Statuses(JsonElement e) => e.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("status").GetString()!).ToArray();

    [Fact]
    public async Task Only_enrolled_machines_ask_for_decisions_and_invalid_requests_are_refused()
    {
        await SetupAsync();
        var body = Body((Intel5590, "31.0.101.5590", "Update"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(null).PostAsync("/api/rollout/decide", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AdminToken).PostAsync("/api/rollout/decide", Json(body))).StatusCode);
        await _host.CreateUserAsync("lecteur", "readonly");
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _host.LoginAsync("lecteur")).PostAsync("/api/rollout/decide", Json(body))).StatusCode);
        var agent = _host.Client(AgentToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync("/api/rollout/decide", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync("/api/rollout/decide", Json("{ pas du json"))).StatusCode);
        var many = Enumerable.Range(0, RolloutService.MaxDecisionCandidates + 1).Select(i => ($"P{i} - 1.0.{i}", $"1.0.{i}", "Update")).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync("/api/rollout/decide", Json(Body(many)))).StatusCode);

        // Corps compressé (comme le poste), réponse compressée sur demande.
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Body(Enumerable.Range(0, 40).Select(i => ($"Pilote {i} - 1.0.{i}", $"1.0.{i}", "Update")).ToArray())));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(raw);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/rollout/decide") { Content = new ByteArrayContent(ms.ToArray()) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Content.Headers.ContentEncoding.Add("gzip");
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var r = await agent.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("gzip", r.Content.Headers.ContentEncoding);
        using var unzip = new GZipStream(await r.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        var decoded = JsonNode.Parse(unzip)!;
        Assert.Equal(40, decoded["decisions"]!.AsArray().Count);
        Assert.Equal("conservative", r.Headers.GetValues("X-SpokaPilote-Ring").Single()); // sans identité : anneau le plus prudent
    }

    [Fact]
    public async Task Each_machine_gets_the_decisions_of_its_ring_for_its_own_candidates()
    {
        await SetupAsync();
        var body = Body((Intel5590, "31.0.101.5590", "Update"), (Intel5600, "31.0.101.5600", "Update"), ("Contoso - Camera - 2.4.0.1", "2.4.0.1", "Update"),
            (Intel5590, "31.0.101.5590", "MissingDriver"));

        var pilot = await Decide(1, "PC-IT-01", body);
        Assert.Equal(["allowed", "allowed", "allowed", "allowed"], Statuses(pilot));
        Assert.Equal("Pilote", pilot.GetProperty("ring").GetString());

        var general = await Decide(3, "PC-COMPTA-01", body);
        Assert.Equal("Général", general.GetProperty("ring").GetString());
        Assert.True(general.GetProperty("machineKnown").GetBoolean());
        Assert.True(general.GetProperty("holdUnknown").GetBoolean());
        Assert.Equal(["held", "unknown", "unknown", "allowed"], Statuses(general));
        var held = general.GetProperty("decisions")[0];
        Assert.Equal(_host.Clock.Now.AddHours(-1).AddDays(10), held.GetProperty("availableAt").GetDateTimeOffset());
        Assert.Contains("disponible pour votre anneau « Général »", held.GetProperty("reason").GetString());

        // Poste encore inconnu : anneau par défaut ; sans identité : anneau le plus prudent.
        var stranger = await Decide(99, "PC-NOUVEAU", body);
        Assert.False(stranger.GetProperty("machineKnown").GetBoolean());
        Assert.Equal("Général", stranger.GetProperty("ring").GetString());
        Assert.Equal(["held", "unknown", "unknown", "allowed"], Statuses(await Decide(null, null, body)));
    }

    [Fact]
    public async Task Deciding_records_nothing_the_first_report_registers_a_new_version_and_suspensions_win()
    {
        await SetupAsync();
        var store = _host.Factory.Services.GetRequiredService<Storage.ServerStore>();
        var before = store.LoadRolloutSnapshot(_host.Clock.Now).Versions.Count;
        var body = Body((Intel5600, "31.0.101.5600", "Update"));
        Assert.Equal(["unknown"], Statuses(await Decide(3, "PC-COMPTA-01", body)));
        Assert.Equal(before, store.LoadRolloutSnapshot(_host.Clock.Now).Versions.Count);

        // Le rapport d'un poste pilote la signale : elle est désormais connue, retenue avec une date pour « Général ».
        await Report(1, "PC-IT-01", new RolloutReports.Cand(Intel5600, "31.0.101.5600"));
        var general = await Decide(3, "PC-COMPTA-01", body);
        Assert.Equal(["held"], Statuses(general));
        Assert.Equal(_host.Clock.Now.AddDays(10), general.GetProperty("decisions")[0].GetProperty("availableAt").GetDateTimeOffset());

        // Suspension : tous les anneaux, pilotes manquants compris.
        var admin = _host.Client(AdminToken);
        var id = (await ReadJson(await admin.GetAsync("/api/rollout/versions?status=all&q=5600"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{id}/suspend", Json(new { reason = "écrans bleus" }))).StatusCode);
        var suspended = await Decide(1, "PC-IT-01", Body((Intel5600, "31.0.101.5600", "MissingDriver")));
        Assert.Equal(["suspended"], Statuses(suspended));
        Assert.Contains("écrans bleus", suspended.GetProperty("decisions")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_policy_of_a_machine_asking_for_decisions_only_carries_suspensions_and_the_preview_shows_both_views()
    {
        await SetupAsync();
        var admin = _host.Client(AdminToken);
        await Report(1, "PC-IT-01", new RolloutReports.Cand(Intel5600, "31.0.101.5600"));
        var id = (await ReadJson(await admin.GetAsync("/api/rollout/versions?status=all&q=5600"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        await admin.PostAsync($"/api/rollout/versions/{id}/suspend", Json(new { reason = "écrans bleus" }));

        async Task<JsonNode> Policy(bool decisions)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/policy");
            req.Headers.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(3));
            req.Headers.Add("X-SpokaPilote-Machine", "PC-COMPTA-01");
            req.Headers.Add("X-SpokaPilote-Rollout", "1");
            if (decisions) req.Headers.Add("X-SpokaPilote-Rollout-Decisions", "1");
            var r = await _host.Client(AgentToken).SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        }
        var small = await Policy(decisions: true);
        var ids = small["rules"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToList();
        Assert.Equal([$"anneau-suspendu-{id}"], ids);
        Assert.True(small["rollout"]!["decisions"]!.GetValue<bool>());
        Assert.True(small["rollout"]!["holdUnknown"]!.GetValue<bool>());
        // Sans l'en-tête (poste 4.0 antérieur) : liste des retenues, comme avant.
        var full = await Policy(decisions: false);
        Assert.Contains(full["rules"]!.AsArray(), x => x!["id"]!.GetValue<string>().StartsWith("anneau-retenue-"));
        Assert.Null(full["rollout"]!["decisions"]);

        // Capacité annoncée par la console.
        Assert.Contains("rollout-decisions", (await ReadJson(await _host.Client(null).GetAsync("/api/health"))).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));

        // Aperçu : décisions pour les propositions du dernier rapport du poste, tailles des documents 4.0 et 3.x.
        var preview = await ReadJson(await admin.GetAsync("/api/rollout/preview?machine=PC-IT-01"));
        Assert.Equal("Pilote", preview.GetProperty("ring").GetString());
        var d = preview.GetProperty("decisions");
        Assert.Equal(1, d.GetProperty("candidates").GetInt32());
        Assert.Equal(1, d.GetProperty("suspended").GetInt32());
        Assert.Equal(Intel5600, d.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.True(preview.GetProperty("legacy").GetProperty("bytes").GetInt32() > 0);
        Assert.Equal("complete", preview.GetProperty("legacy").GetProperty("form").GetString());
        Assert.Equal(RolloutLimits.Default.MaxRules, preview.GetProperty("legacy").GetProperty("maxRules").GetInt32());
        var general = await ReadJson(await admin.GetAsync("/api/rollout/preview?machine=PC-COMPTA-01"));
        Assert.True(general.GetProperty("bytes").GetInt32() < general.GetProperty("withoutDecisions").GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task The_31_document_stays_under_the_configured_cap()
    {
        var rollout = _host.Factory.Services.GetRequiredService<RolloutService>();
        rollout.Limits = new RolloutLimits(60, 64 * 1024);
        await SetupAsync();
        for (var i = 0; i < 120; i++) await Report(1, "PC-IT-01", new RolloutReports.Cand($"Fabricant {i} - Pilote {i} - 1.0.{i}.0", $"1.0.{i}.0"));
        var r = await _host.Client(AgentToken).GetAsync("/api/policy");
        var json = await r.Content.ReadAsStringAsync();
        var rules = JsonNode.Parse(json)!["rules"]!.AsArray();
        Assert.True(rules.Count <= 60, $"{rules.Count} règles");
        Assert.True(json.Length <= 64 * 1024);
        var p31 = BigFleet.Parse31(json);
        // Toutes les versions en cours de diffusion restent bloquées pour le poste 3.x (règle de source si besoin).
        for (var i = 0; i < 120; i++)
            Assert.True(p31.Evaluate(BigFleet.Candidate("WindowsUpdate", $"Fabricant {i} - Pilote {i} - 1.0.{i}.0", $"1.0.{i}.0"), null, DateTime.Now).IsBlocked);
    }
}
