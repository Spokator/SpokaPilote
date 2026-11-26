using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SpokaPilote.Server.Api;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Parc varié pour les tests « grands parcs » : postes sains, à traiter, en échec, non vus, avec santé, plusieurs modèles.</summary>
internal static class FleetSeed
{
    public static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    public static string Json(int i)
    {
        var models = new[] { ("Dell Inc.", "Latitude 7420"), ("LENOVO", "20W0"), ("HP", "HP EliteBook 840 G8"), ("Dell Inc.", "OptiPlex 7010") };
        var (manufacturer, model) = models[i % models.Length];
        var at = Now.AddHours(-i).AddDays(i % 11 == 0 ? -12 : 0); // un poste sur 11 n'a rien envoyé depuis 12 jours
        var name = $"PC-{(i % 3 == 0 ? "COMPTA" : i % 3 == 1 ? "RH" : "ATELIER")}-{i:D3}";
        var f = new ReportFixture(name, manufacturer, model, at).WithMachineId(new Guid(i + 1, 0, 0, new byte[8]).ToString());
        if (i % 4 == 1) f = ReportFixture.Typical(name, model, at).WithMachineId(new Guid(i + 1, 0, 0, new byte[8]).ToString());
        if (i % 7 == 2)
            f.Device($"Capteur {i % 5}", $"ACPI\\SENS{i % 5}\\{i}", [$"ACPI\\VEN_SNS&DEV_{i % 5:D4}"], 28).Outcome("Pilote X", "Failed");
        if (i % 5 == 0) f.WithHealth($$"""{ "score": {{40 + i % 60}}, "grade": "C", "findings": [ { "severity": "critical", "code": "known-bad-driver", "title": "x" } ] }""");
        return f.Json();
    }

    public static void Save(ServerStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var json = Json(i);
            store.SaveReport(ReportParser.Parse(json), json, Now, $"10.{i % 3}.0.{i + 1}");
        }
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, ServerStore.Json);
}

/// <summary>Liste des postes côté serveur : pagination, filtres, tri, et équivalence exacte avec le calcul 3.1.</summary>
public sealed class MachineListQueryTests : IDisposable
{
    private readonly TestServerHost _host = new(b => b.UseSetting("SPOKAPILOTE_STALE_DAYS", "7"));

    public void Dispose() => _host.Dispose();

    private ServerStore Store => _host.Factory.Services.GetRequiredService<ServerStore>();

    private async Task<long> SeedAsync(int count = 45)
    {
        _host.Clock.Now = FleetSeed.Now;
        await _host.Client(AdminToken).GetAsync("/api/health");
        FleetSeed.Save(Store, count);
        var admin = _host.Client(AdminToken);
        var created = await admin.PostAsync("/api/groups", Json(new
        {
            name = "Compta Dell",
            rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-COMPTA" }, new { field = "manufacturer", @operator = "contains", value = "dell" } } },
        }));
        var id = (await ReadJson(created)).GetProperty("id").GetInt64();
        await admin.PostAsync($"/api/groups/{id}/members", Json(new { add = new[] { "PC-RH-001" } }));
        var m = Store.GetMachine("PC-ATELIER-002")!;
        Store.CreateJob(m.Id, JobTypes.Scan, new JobParameters(), "x", FleetSeed.Now);
        return id;
    }

    /// <summary>Calcul de la console 3.1 (rapports complets, règles évaluées à la volée), pour comparer.</summary>
    private List<MachineRow> LegacyRows(Func<StoredMachine, bool>? filter = null)
    {
        var machines = Store.GetMachines().Where(filter ?? (_ => true)).ToList();
        var groups = Store.GetGroups();
        var active = Store.GetActiveJobCounts();
        var membership = GroupMatcher.GroupsByMachine(groups, machines);
        var rows = FleetAggregator.Rows(machines, FleetSeed.Now, 7);
        var ids = machines.ToDictionary(m => m.Name, m => m.Id);
        foreach (var r in rows)
        {
            r.ActiveJobs = active.GetValueOrDefault(ids[r.Name]);
            r.Groups = membership.GetValueOrDefault(ids[r.Name]) ?? new List<long>();
        }
        return rows;
    }

    [Fact]
    public async Task Legacy_list_and_summary_are_identical_to_the_31_computation()
    {
        var group = await SeedAsync();
        var admin = _host.Client(AdminToken);

        var api = await (await admin.GetAsync("/api/machines")).Content.ReadAsStringAsync();
        Assert.Equal(FleetSeed.Serialize(LegacyRows()), api);

        var g = Store.GetGroup(group)!;
        var inGroup = await (await admin.GetAsync($"/api/machines?group={group}")).Content.ReadAsStringAsync();
        Assert.Equal(FleetSeed.Serialize(LegacyRows(m => GroupMatcher.IsMember(g, m))), inGroup);

        // Synthèse : mêmes indicateurs, listes, matériel sans pilote et modèles que le calcul sur les rapports complets.
        var expected = FleetAggregator.Summarize(Store.GetMachines(), FleetSeed.Now, 7);
        var actual = Store.GetFleetSummary(null, FleetSeed.Now, 7);
        Assert.Equal(FleetSeed.Serialize(expected), FleetSeed.Serialize(actual));
        var expectedGroup = FleetAggregator.Summarize(Store.GetMachines().Where(m => GroupMatcher.IsMember(g, m)).ToList(), FleetSeed.Now, 7);
        Assert.Equal(FleetSeed.Serialize(expectedGroup), FleetSeed.Serialize(Store.GetFleetSummary(group, FleetSeed.Now, 7)));
        Assert.True(expected.MissingDrivers.Count > 0 && expected.StaleMachines.Count > 0 && expected.ToProcessMachines.Count > 0);
    }

    [Fact]
    public async Task Pages_cover_the_filtered_list_exactly_once_in_the_requested_order()
    {
        await SeedAsync();
        var admin = _host.Client(AdminToken);
        var names = new List<string>();
        int total = 0;
        for (var page = 1; page <= 5; page++)
        {
            var r = await ReadJson(await admin.GetAsync($"/api/machines?page={page}&pageSize=10&sort=name"));
            total = r.GetProperty("total").GetInt32();
            Assert.Equal(45, r.GetProperty("machineCount").GetInt32());
            names.AddRange(r.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!));
        }
        Assert.Equal(45, total);
        Assert.Equal(LegacyRows().Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);

        // offset / limit, ordre décroissant.
        var desc = await ReadJson(await admin.GetAsync("/api/machines?offset=40&limit=10&sort=name&dir=desc"));
        Assert.Equal(names.AsEnumerable().Reverse().Skip(40).ToList(), desc.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList());

        // Tri par compteur (décroissant par défaut), égalités départagées par le nom.
        var byFailed = (await ReadJson(await admin.GetAsync("/api/machines?pageSize=1000&sort=failed"))).GetProperty("items").EnumerateArray()
            .Select(x => (x.GetProperty("failed").GetInt32(), x.GetProperty("name").GetString()!)).ToList();
        Assert.Equal(byFailed.OrderByDescending(x => x.Item1).ThenByDescending(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList(), byFailed);

        // État : à traiter d'abord, puis les plus d'échecs et de pilotes en erreur.
        var status = (await ReadJson(await admin.GetAsync("/api/machines?pageSize=1000&sort=status"))).GetProperty("items").EnumerateArray()
            .Select(x => (Healthy: x.GetProperty("healthy").GetBoolean(), Failed: x.GetProperty("failed").GetInt32(), Problems: x.GetProperty("driverProblems").GetInt32()))
            .ToList();
        Assert.Equal(status.OrderBy(x => x.Healthy).ThenByDescending(x => x.Failed).ThenByDescending(x => x.Problems).ToList(), status);

        // Santé : les postes sans note à la fin (comme la console).
        var health = (await ReadJson(await admin.GetAsync("/api/machines?pageSize=1000&sort=health"))).GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("healthScore").ValueKind == JsonValueKind.Null ? 101 : x.GetProperty("healthScore").GetInt32()).ToList();
        Assert.Equal(health.OrderBy(x => x).ToList(), health);
    }

    [Fact]
    public async Task Filters_match_the_console_definitions()
    {
        var group = await SeedAsync();
        var admin = _host.Client(AdminToken);
        var all = LegacyRows();
        async Task<List<string>> Names(string query) =>
            (await ReadJson(await admin.GetAsync($"/api/machines?pageSize=1000&sort=name&{query}"))).GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()!).ToList();
        List<string> Expected(Func<MachineRow, bool> predicate) =>
            all.Where(predicate).Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(Expected(r => !r.Healthy), await Names("status=todo"));
        Assert.Equal(Expected(r => r.Healthy), await Names("status=healthy"));
        Assert.Equal(Expected(r => r.Stale), await Names("status=stale"));
        Assert.Equal(Expected(r => r.Unresolved > 0), await Names("status=unresolved"));
        Assert.Equal(Expected(r => r.HardwareFaults > 0), await Names("status=hardware"));
        Assert.Equal(Expected(r => r.Failed > 0), await Names("status=failed"));
        Assert.Equal(Expected(r => r.DangerousDriver), await Names("status=dangerous"));
        Assert.Equal(["PC-ATELIER-002"], await Names("status=jobs"));
        Assert.Equal(Expected(r => r.Groups.Contains(group)), await Names($"group={group}"));
        Assert.Equal(Expected(r => r.Groups.Count == 0), await Names("group=none"));
        Assert.Equal(Expected(r => r.Name.Contains("rh", StringComparison.OrdinalIgnoreCase) || r.Model.Contains("rh", StringComparison.OrdinalIgnoreCase)),
            await Names("q=rh"));
        Assert.Equal(Expected(r => r.Model.Contains("EliteBook")), await Names("q=elitebook"));
        Assert.Equal(Expected(r => r.SerialNumber == "SN-PC-RH-013"), await Names("q=SN-PC-RH-013"));
        Assert.Equal(Expected(r => !r.Healthy && r.Groups.Contains(group)), await Names($"status=todo&group={group}"));
        Assert.Empty(await Names("q=100%25_"));    // caractères spéciaux de LIKE neutralisés

        // Les filtres s'appliquent aussi à la liste historique (tableau, ordre historique).
        var legacy = await ReadJson(await admin.GetAsync("/api/machines?status=todo"));
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind);
        Assert.Equal(all.Where(r => !r.Healthy).Select(r => r.Name), legacy.EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        // Erreurs de paramètres.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/machines?status=inconnu")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/machines?sort=inconnu")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/machines?page=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/machines?group=999")).StatusCode);

        // Enveloppe : compteurs du parc et du groupe.
        var envelope = await ReadJson(await admin.GetAsync($"/api/machines?page=1&pageSize=5&group={group}"));
        Assert.Equal(all.Count(r => !r.Healthy), envelope.GetProperty("toProcessCount").GetInt32());
        Assert.Equal(all.Count(r => r.Groups.Contains(group)), envelope.GetProperty("group").GetProperty("machines").GetInt32());
        Assert.True(envelope.GetProperty("hasHealth").GetBoolean());
        Assert.Equal(5, envelope.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Csv_exports_stream_every_matching_row()
    {
        await SeedAsync();
        var admin = _host.Client(AdminToken);
        var r = await admin.GetAsync("/api/export/machines?status=todo");
        Assert.Equal("text/csv", r.Content.Headers.ContentType!.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Poste;Identifiant;", lines[0]);
        Assert.Equal(LegacyRows().Count(x => !x.Healthy), lines.Length - 1);
        Assert.All(lines.Skip(1), l => Assert.Contains("\"à traiter\"", l));

        var missing = Encoding.UTF8.GetString(await (await admin.GetAsync("/api/export/missing-drivers")).Content.ReadAsByteArrayAsync());
        var summary = Store.GetFleetSummary(null, FleetSeed.Now, 7);
        Assert.Equal(summary.MissingDrivers.Count + 1, missing.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains(string.Join(", ", summary.MissingDrivers[0].Machines), missing);
    }

    [Fact]
    public async Task Missing_drivers_and_fleet_lists_can_be_paged_without_changing_the_counts()
    {
        await SeedAsync();
        var admin = _host.Client(AdminToken);
        var full = Store.GetFleetSummary(null, FleetSeed.Now, 7);
        var page = await ReadJson(await admin.GetAsync("/api/missing-drivers?limit=1&machines=2"));
        Assert.Equal(full.MissingDrivers.Count, page.GetProperty("total").GetInt32());
        var first = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(full.MissingDrivers[0].HardwareId, first.GetProperty("hardwareId").GetString());
        Assert.Equal(full.MissingDrivers[0].Count, first.GetProperty("count").GetInt32());
        Assert.Equal(Math.Min(2, full.MissingDrivers[0].Count), first.GetProperty("machines").GetArrayLength());
        Assert.Equal(Math.Max(0, full.MissingDrivers[0].Count - 2), first.GetProperty("moreMachines").GetInt32());
        var search = await ReadJson(await admin.GetAsync("/api/missing-drivers?q=VEN_SNS"));
        Assert.All(search.GetProperty("items").EnumerateArray(), x => Assert.Contains("VEN_SNS", x.GetProperty("hardwareId").GetString()));

        var top = await ReadJson(await admin.GetAsync("/api/fleet?top=2"));
        Assert.Equal(2, top.GetProperty("toProcessMachines").GetArrayLength());
        Assert.Equal(full.ToProcess, top.GetProperty("toProcess").GetInt32());
        Assert.Equal(full.MissingDrivers.Count, top.GetProperty("missingDriverCount").GetInt32());
        Assert.True(top.GetProperty("missingDrivers").GetArrayLength() <= 2);
        var complete = await ReadJson(await admin.GetAsync("/api/fleet"));
        Assert.Equal(full.ToProcess, complete.GetProperty("toProcessMachines").GetArrayLength());     // sans ?top : listes complètes (3.1)
    }

    [Fact]
    public async Task Group_members_can_be_paged_and_suggested()
    {
        var group = await SeedAsync();
        var admin = _host.Client(AdminToken);
        var full = await ReadJson(await admin.GetAsync($"/api/groups/{group}"));
        var page = await ReadJson(await admin.GetAsync($"/api/groups/{group}?limit=2"));
        Assert.Equal(2, page.GetProperty("members").GetArrayLength());
        Assert.Equal(full.GetProperty("members").GetArrayLength(), page.GetProperty("memberTotal").GetInt32());
        Assert.Equal(full.GetProperty("members").EnumerateArray().Count(m => !m.GetProperty("healthy").GetBoolean()),
            page.GetProperty("toProcessCount").GetInt32());
        Assert.Equal(full.GetProperty("group").GetProperty("memberCount").GetInt32(), page.GetProperty("memberTotal").GetInt32());
    }
}

/// <summary>Synthèse mise en cache : toujours à jour après un changement (rapport, groupe, membres, suppression, temps).</summary>
public sealed class FleetCacheTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task Post(string json, string? ip = null)
    {
        var r = await _host.Client(AgentToken, ip).PostAsync("/api/reports", Json(json));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Summary_follows_every_change_and_is_reused_otherwise()
    {
        var admin = _host.Client(AdminToken);
        var store = _host.Factory.Services.GetRequiredService<ServerStore>();
        _host.Clock.Now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        await Post(new ReportFixture("PC-01", timestamp: _host.Clock.Now.AddHours(-1)).Json());
        await Post(new ReportFixture("PC-02", manufacturer: "HP", timestamp: _host.Clock.Now.AddHours(-1)).Json());
        async Task<JsonElement> Fleet(string q = "") => await ReadJson(await admin.GetAsync("/api/fleet" + q));

        Assert.Equal(0, (await Fleet()).GetProperty("toProcess").GetInt32());
        var (hits, _) = store.FleetCacheStats;
        await Fleet();
        Assert.Equal(hits + 1, store.FleetCacheStats.Hits);                       // rien n'a changé : servi depuis le cache

        // Nouveau rapport : le poste devient à traiter, son matériel sans pilote apparaît.
        await Post(ReportFixture.Typical("PC-01", timestamp: _host.Clock.Now.AddMinutes(-30)).Json());
        var f = await Fleet();
        Assert.Equal(1, f.GetProperty("toProcess").GetInt32());
        Assert.Equal(1, f.GetProperty("missingDriverCount").GetInt32());

        // Groupe créé, puis règles modifiées : la synthèse du groupe suit.
        var id = (await ReadJson(await admin.PostAsync("/api/groups", Json(new
        {
            name = "HP", rules = new { match = "all", conditions = new[] { new { field = "manufacturer", @operator = "equals", value = "HP" } } },
        })))).GetProperty("id").GetInt64();
        Assert.Equal(1, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());
        await admin.PutAsync($"/api/groups/{id}", Json(new
        {
            name = "HP", rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-" } } },
        }));
        Assert.Equal(2, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());
        await admin.PutAsync($"/api/groups/{id}", Json(new { name = "HP" }));                // plus de règle : plus de membre
        Assert.Equal(0, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());
        await admin.PostAsync($"/api/groups/{id}/members", Json(new { add = new[] { "PC-02" } }));
        Assert.Equal(1, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());

        // Un poste renommé (même identifiant) change de groupe par règle aussitôt.
        await admin.PutAsync($"/api/groups/{id}", Json(new
        {
            name = "HP", rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-NEW" } } },
        }));
        Assert.Equal(1, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());   // PC-02 (manuel)
        await Post(new ReportFixture("PC-01", timestamp: _host.Clock.Now.AddMinutes(-20)).WithMachineId("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab").Json());
        await Post(new ReportFixture("PC-NEW-01", timestamp: _host.Clock.Now.AddMinutes(-10)).WithMachineId("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab").Json());
        Assert.Equal(2, (await Fleet($"?group={id}")).GetProperty("machineCount").GetInt32());

        // Suppression d'un poste.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/machines/PC-02")).StatusCode);
        Assert.Equal(1, (await Fleet()).GetProperty("machineCount").GetInt32());

        // Le temps passe sans aucun rapport : le poste devient « non vu ».
        Assert.Equal(0, (await Fleet()).GetProperty("stale").GetInt32());
        _host.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(1, (await Fleet()).GetProperty("stale").GetInt32());
    }

    [Fact]
    public async Task Subnet_rules_follow_the_address_of_the_latest_report()
    {
        var admin = _host.Client(AdminToken);
        var id = (await ReadJson(await admin.PostAsync("/api/groups", Json(new
        {
            name = "Site 10.1", rules = new { match = "all", conditions = new[] { new { field = "subnet", @operator = "inSubnet", value = "10.1.0.0/16" } } },
        })))).GetProperty("id").GetInt64();
        await Post(new ReportFixture("PC-01", timestamp: _host.Clock.Now.AddHours(-2)).Json(), "10.1.0.5");
        Assert.Equal(1, (await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"))).GetProperty("machineCount").GetInt32());
        await Post(new ReportFixture("PC-01", timestamp: _host.Clock.Now.AddHours(-1)).Json(), "10.2.0.5");
        Assert.Equal(0, (await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"))).GetProperty("machineCount").GetInt32());
        // Un ancien rapport (reprise d'un partage) ne change ni l'adresse ni l'appartenance.
        await Post(new ReportFixture("PC-01", timestamp: _host.Clock.Now.AddDays(-3)).Json(), "10.1.0.9");
        Assert.Equal(0, (await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"))).GetProperty("machineCount").GetInt32());
    }

    [Fact]
    public async Task Agent_last_contact_is_fresh_in_the_cached_summary_and_written_at_most_once_a_minute()
    {
        const string id = "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";
        var store = _host.Factory.Services.GetRequiredService<ServerStore>();
        await Post(ReportFixture.Typical("PC-01", timestamp: _host.Clock.Now.AddHours(-1)).WithMachineId(id).Json());
        var admin = _host.Client(AdminToken);
        var before = await ReadJson(await admin.GetAsync("/api/fleet"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("toProcessMachines")[0].GetProperty("agentSeen").ValueKind);

        var agent = _host.Agent(id, "PC-01");
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/agent/jobs")).StatusCode);
        var revision = store.FleetRevision();
        var after = await ReadJson(await admin.GetAsync("/api/fleet"));
        Assert.Equal(_host.Clock.Now, after.GetProperty("toProcessMachines")[0].GetProperty("agentSeen").GetDateTimeOffset());

        // Nouvelle interrogation 30 s plus tard : aucune écriture ; une minute plus tard : contact mis à jour.
        _host.Clock.Advance(TimeSpan.FromSeconds(30));
        await agent.GetAsync("/api/agent/jobs");
        Assert.Equal(_host.Clock.Now.AddSeconds(-30), store.GetMachine("PC-01")!.AgentSeen);
        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        await agent.GetAsync("/api/agent/jobs");
        Assert.Equal(_host.Clock.Now, store.GetMachine("PC-01")!.AgentSeen);
        Assert.Equal(revision, store.FleetRevision());   // le contact de l'agent n'invalide pas la synthèse
    }
}

/// <summary>Envoi des rapports compressés : rapport brut conservé tel quel, protection contre les bombes de décompression.</summary>
public sealed class GzipUploadTests : IDisposable
{
    private readonly TestServerHost _host = new(b => b.UseSetting("SPOKAPILOTE_MAX_REPORT_MB", "1"));

    public void Dispose() => _host.Dispose();

    private static ByteArrayContent Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        var content = new ByteArrayContent(output.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [Fact]
    public async Task Gzip_report_is_accepted_and_its_raw_form_is_returned_unchanged()
    {
        var json = ReportFixture.Typical("PC-GZ").Json();
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var r = await _host.Client(AgentToken).PostAsync("/api/reports", Gzip(withBom));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var raw = await _host.Client(AdminToken).GetStringAsync("/api/machines/PC-GZ/raw");
        Assert.Equal(json, raw);
        Assert.Equal(2, (await ReadJson(await _host.Client(AdminToken).GetAsync("/api/machines/PC-GZ"))).GetProperty("machine").GetProperty("driverProblems").GetInt32());
    }

    [Fact]
    public async Task Decompression_bombs_and_invalid_bodies_are_refused()
    {
        // 30 Mo de JSON qui tiennent en ~30 Ko compressés : refusé dès que la limite (1 Mo décompressé) est franchie.
        var bomb = Encoding.UTF8.GetBytes("{\"system\":{\"computerName\":\"PC-X\"},\"devices\":[],\"x\":\"" + new string('A', 30 * 1024 * 1024) + "\"}");
        var content = Gzip(bomb);
        Assert.True(content.Headers.ContentLength < 200 * 1024);
        var r = await _host.Client(AgentToken).PostAsync("/api/reports", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);

        var garbage = new ByteArrayContent([0x1f, 0x8b, 8, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        garbage.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client(AgentToken).PostAsync("/api/reports", garbage)).StatusCode);

        var brotli = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        brotli.Headers.ContentEncoding.Add("br");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _host.Client(AgentToken).PostAsync("/api/reports", brotli)).StatusCode);
        Assert.Empty(_host.Factory.Services.GetRequiredService<ServerStore>().GetMachineSummaries());
    }

    [Fact]
    public async Task Health_advertises_capabilities_for_newer_clients()
    {
        var health = await ReadJson(await _host.Client(null).GetAsync("/api/health"));
        Assert.Equal("ok", health.GetProperty("status").GetString());
        var capabilities = health.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("gzip-reports", capabilities);
        Assert.Contains("retry-after", capabilities);
        // La sonde Docker cherche « "status":"ok" » dans la réponse brute.
        Assert.Contains("\"status\":\"ok\"", await _host.Client(null).GetStringAsync("/api/health"));
    }
}

/// <summary>Contrôle d'admission : afflux de postes refusé proprement (503 + Retry-After étalé) au lieu d'un effondrement.</summary>
public sealed class AdmissionTests : IDisposable
{
    private readonly TestServerHost _host = new(b => b
        .UseSetting("SPOKAPILOTE_MAX_CONCURRENT_UPLOADS", "1").UseSetting("SPOKAPILOTE_UPLOAD_QUEUE", "0")
        .UseSetting("SPOKAPILOTE_MAX_CONCURRENT_AGENTS", "1").UseSetting("SPOKAPILOTE_AGENT_QUEUE", "0")
        .UseSetting("SPOKAPILOTE_RETRY_AFTER_SECONDS", "60"));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Overloaded_console_answers_503_with_a_spread_retry_after_then_recovers()
    {
        var admission = _host.Factory.Services.GetRequiredService<AdmissionControl>();
        var report = ReportFixture.Typical("PC-01").WithMachineId("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab").Json();
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports", Json(report))).StatusCode);

        var waits = new List<int>();
        using (var busy = await admission.Uploads.EnterAsync(CancellationToken.None))
        {
            Assert.NotNull(busy);
            for (var i = 0; i < 20; i++)
            {
                var r = await _host.Client(AgentToken).PostAsync("/api/reports", Json(report));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
                waits.Add((int)r.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
                Assert.Contains("surchargée", (await ReadJson(r)).GetProperty("error").GetString());
            }
        }
        Assert.All(waits, w => Assert.InRange(w, 30, 90));
        Assert.True(waits.Distinct().Count() > 1);                                  // étalé : les postes ne reviennent pas ensemble
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports", Json(report))).StatusCode);

        using (await admission.Agents.EnterAsync(CancellationToken.None))
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await _host.Agent("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-01").GetAsync("/api/agent/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Agent("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-01").GetAsync("/api/agent/jobs")).StatusCode);
        Assert.Equal(20, admission.Uploads.Rejected);

        // La console reste consultable pendant un afflux (seules les requêtes des postes sont limitées).
        using (await admission.Uploads.EnterAsync(CancellationToken.None))
            Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken).GetAsync("/api/machines")).StatusCode);
    }

    [Fact]
    public async Task Queued_requests_wait_their_turn_instead_of_failing()
    {
        var gate = new AdmissionControl.Gate("test", permits: 1, queue: 5, timeout: TimeSpan.FromSeconds(5));
        var first = await gate.EnterAsync(CancellationToken.None);
        var waiting = gate.EnterAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, gate.Queued);
        first!.Dispose();
        using var second = await waiting;
        Assert.NotNull(second);

        var shortGate = new AdmissionControl.Gate("court", permits: 1, queue: 5, timeout: TimeSpan.FromMilliseconds(100));
        using var held = await shortGate.EnterAsync(CancellationToken.None);
        Assert.Null(await shortGate.EnterAsync(CancellationToken.None));         // attente maximale dépassée
        Assert.Equal(1, shortGate.Rejected);
    }
}

/// <summary>Migration automatique d'une base 3.1 (schéma 3) : indicateurs calculés au démarrage, historique compressé ensuite.</summary>
public sealed class MigrationFrom31Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-mig31-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "spokapilote.db");

    /// <summary>Schéma exact de la console 3.1 (copie figée).</summary>
    private const string Schema31 = """
        CREATE TABLE machines (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, machine_id TEXT UNIQUE,
            last_seen TEXT NOT NULL, last_report TEXT NOT NULL, summary_json TEXT NOT NULL, raw_report BLOB, agent_seen TEXT, last_ip TEXT);
        CREATE TABLE reports (
            id INTEGER PRIMARY KEY AUTOINCREMENT, machine_ref INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            report_at TEXT NOT NULL, received_at TEXT NOT NULL, summary_json TEXT NOT NULL);
        CREATE INDEX ix_reports_machine ON reports(machine_ref, report_at DESC);
        CREATE TABLE documents (key TEXT NOT NULL PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE jobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT, machine_ref INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            type TEXT NOT NULL, parameters TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT NOT NULL, state TEXT NOT NULL,
            started_at TEXT, finished_at TEXT, message TEXT, exit_code INTEGER, report_id INTEGER, log TEXT, not_after TEXT, schedule_id INTEGER);
        CREATE INDEX ix_jobs_machine ON jobs(machine_ref, state);
        CREATE INDEX ix_jobs_state ON jobs(state, created_at);
        CREATE TABLE alert_log (subject TEXT NOT NULL, reason TEXT NOT NULL, last_sent TEXT NOT NULL, PRIMARY KEY (subject, reason));
        CREATE TABLE alert_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT NOT NULL, machine TEXT, reason TEXT NOT NULL, title TEXT NOT NULL,
            status TEXT NOT NULL, detail TEXT);
        CREATE TABLE machine_groups (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, description TEXT, rules_json TEXT NOT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE group_members (
            group_id INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,
            machine_ref INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE, PRIMARY KEY (group_id, machine_ref));
        CREATE INDEX ix_group_members_machine ON group_members(machine_ref);
        CREATE TABLE schedules (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, job_type TEXT NOT NULL, dry_run INTEGER NOT NULL DEFAULT 0,
            group_id INTEGER REFERENCES machine_groups(id), frequency TEXT NOT NULL, days TEXT NOT NULL, time TEXT NOT NULL, window_minutes INTEGER,
            enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, created_by TEXT NOT NULL, updated_at TEXT NOT NULL, next_run TEXT,
            last_run TEXT, last_result TEXT);
        CREATE TABLE users (
            id INTEGER PRIMARY KEY AUTOINCREMENT, login TEXT NOT NULL UNIQUE COLLATE NOCASE, display_name TEXT, role TEXT NOT NULL,
            password_hash TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            password_changed_at TEXT NOT NULL, last_login TEXT);
        CREATE TABLE sessions (
            id_hash TEXT NOT NULL PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE, csrf TEXT NOT NULL,
            created_at TEXT NOT NULL, last_seen TEXT NOT NULL, expires_at TEXT NOT NULL, ip TEXT);
        CREATE INDEX ix_sessions_user ON sessions(user_id);
        CREATE TABLE audit (
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, actor TEXT NOT NULL, role TEXT, action TEXT NOT NULL, target TEXT,
            detail TEXT, ip TEXT, success INTEGER NOT NULL DEFAULT 1);
        CREATE INDEX ix_audit_at ON audit(at);
        PRAGMA user_version = 3;
        """;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    /// <summary>Base 3.1 : 12 postes avec historique (JSON en clair), un groupe par règle, un groupe manuel, des tâches.</summary>
    private void Create31Database()
    {
        Directory.CreateDirectory(_dir);
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using (var schema = c.CreateCommand())
        {
            schema.CommandText = Schema31 + """
                INSERT INTO machine_groups(name, rules_json, created_at, updated_at)
                    VALUES('Dell', '{"match":"all","conditions":[{"field":"manufacturer","operator":"contains","value":"dell"}]}',
                           '2026-11-01T00:00:00.0000000Z', '2026-11-01T00:00:00.0000000Z');
                INSERT INTO machine_groups(name, rules_json, created_at, updated_at) VALUES('VIP', '{"match":"all","conditions":[]}',
                           '2026-11-01T00:00:00.0000000Z', '2026-11-01T00:00:00.0000000Z');
                """;
            schema.ExecuteNonQuery();
        }
        for (var i = 0; i < 12; i++)
        {
            var reports = Enumerable.Range(0, 3).Select(k =>
            {
                var json = FleetSeed.Json(i * 10 + k);
                var r = ReportParser.Parse(json);
                return (Report: r, Summary: JsonSerializer.Serialize(r, ServerStore.Json), Raw: json);
            }).OrderBy(x => x.Report.Timestamp).ToList();
            // Même poste pour les trois rapports (nom du premier), comme la 3.1 les aurait enregistrés.
            var latest = reports[^1];
            var name = $"PC-MIG-{i:D2}";
            using var insert = c.CreateCommand();
            insert.CommandText = """
                INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json, last_ip) VALUES($n, NULL, $seen, $at, $s, $ip) RETURNING id;
                """;
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$seen", ServerStore.Iso(latest.Report.Timestamp));
            insert.Parameters.AddWithValue("$at", ServerStore.Iso(latest.Report.Timestamp));
            insert.Parameters.AddWithValue("$s", latest.Summary.Replace(latest.Report.Name, name));
            insert.Parameters.AddWithValue("$ip", $"10.0.0.{i + 1}");
            var id = (long)insert.ExecuteScalar()!;
            foreach (var r in reports)
            {
                using var report = c.CreateCommand();
                report.CommandText = "INSERT INTO reports(machine_ref, report_at, received_at, summary_json) VALUES($m, $at, $at, $s);";
                report.Parameters.AddWithValue("$m", id);
                report.Parameters.AddWithValue("$at", ServerStore.Iso(r.Report.Timestamp));
                report.Parameters.AddWithValue("$s", r.Summary);
                report.ExecuteNonQuery();
            }
            if (i % 4 == 0)
            {
                using var member = c.CreateCommand();
                member.CommandText = "INSERT INTO group_members(group_id, machine_ref) VALUES(2, $m);";
                member.Parameters.AddWithValue("$m", id);
                member.ExecuteNonQuery();
            }
        }
    }

    private object? Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void A_31_database_is_upgraded_automatically_and_lists_match_the_reports()
    {
        Create31Database();
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        Assert.Equal(3, store.OpenedSchemaVersion);
        Assert.Equal(4L, Scalar("PRAGMA user_version;"));
        var now = FleetSeed.Now;

        // Indicateurs précalculés dès l'ouverture : la synthèse et la liste sont celles de la 3.1.
        var machines = store.GetMachines();
        Assert.Equal(12, machines.Count);
        Assert.Equal(FleetSeed.Serialize(FleetAggregator.Summarize(machines, now, 7)), FleetSeed.Serialize(store.GetFleetSummary(null, now, 7)));
        Assert.Equal(FleetSeed.Serialize(FleetAggregator.Rows(machines, now, 7).Select(r => r.Name)),
            FleetSeed.Serialize(store.QueryMachines(new MachineQuery(), now, 7).Rows.Select(r => r.Name)));
        Assert.All(machines, m => Assert.Equal(3, m.ReportCount));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM machines WHERE summary_gz IS NULL OR summary_json <> '';"));

        // Groupes : membres par règle calculés pendant la migration, membres manuels conservés.
        var groups = store.GetGroups();
        var dell = groups.Single(g => g.Name == "Dell");
        var expectedDell = machines.Where(m => GroupMatcher.IsMember(dell, m)).Select(m => m.Id).ToList();
        Assert.NotEmpty(expectedDell);
        Assert.Equal(expectedDell, store.GetMachineRefs(dell.Id).Select(x => x.Id).OrderBy(x => x).ToList());
        Assert.Equal(3, store.GetMachineRefs(groups.Single(g => g.Name == "VIP").Id).Count);

        // Historique : lisible avant compression (JSON 3.1), identique après compression en arrière-plan.
        var before = machines.ToDictionary(m => m.Name, m => FleetSeed.Serialize(store.GetHistory(m.Name)));
        var reportsBefore = machines.ToDictionary(m => m.Name, m => store.GetHistory(m.Name).Select(h => FleetSeed.Serialize(store.GetHistoricReport(m.Name, h.Id)!)).ToList());
        Assert.Equal(36, store.CountLegacyReports());
        var converted = 0;
        int batch;
        while ((batch = store.MigrateLegacyReports(batch: 5)) > 0) converted += batch;
        Assert.Equal(36, converted);
        Assert.Equal(0, store.CountLegacyReports());
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM reports WHERE summary_gz IS NULL OR summary_json <> '' OR report_ts IS NULL;"));
        foreach (var m in machines)
        {
            Assert.Equal(before[m.Name], FleetSeed.Serialize(store.GetHistory(m.Name)));
            Assert.Equal(reportsBefore[m.Name], store.GetHistory(m.Name).Select(h => FleetSeed.Serialize(store.GetHistoricReport(m.Name, h.Id)!)).ToList());
        }

        // Réouverture : rien n'est rejoué, la base reste utilisable par les nouveaux rapports.
        SqliteConnection.ClearAllPools();
        var again = new ServerStore(new ServerOptions { DataDirectory = _dir, HistoryPerMachine = 3 });
        Assert.Equal(4, again.OpenedSchemaVersion);
        var json = ReportFixture.Typical("PC-MIG-00", timestamp: now).Json();
        again.SaveReport(ReportParser.Parse(json), json, now);
        Assert.Equal(3, again.GetMachine("PC-MIG-00")!.ReportCount);
        Assert.Equal(2, again.GetMachine("PC-MIG-00")!.Report.DriverProblems);
    }

    [Fact]
    public async Task A_31_volume_is_served_by_the_new_server_while_its_history_is_compressed()
    {
        Create31Database();
        SqliteConnection.ClearAllPools();
        using var host = new TestServerHost();
        Directory.CreateDirectory(host.DataDir);
        File.Copy(DbPath, Path.Combine(host.DataDir, "spokapilote.db"));
        var admin = host.Client(AdminToken);
        Assert.Equal(12, (await ReadJson(await admin.GetAsync("/api/machines"))).GetArrayLength());
        var detail = await ReadJson(await admin.GetAsync("/api/machines/PC-MIG-03"));
        Assert.Equal(3, detail.GetProperty("history").GetArrayLength());
        var metrics = await ReadJson(await admin.GetAsync("/api/metrics"));
        Assert.Equal(3, metrics.GetProperty("database").GetProperty("upgradedFrom").GetInt32());
        Assert.Equal(4, metrics.GetProperty("database").GetProperty("schemaVersion").GetInt32());
    }
}

/// <summary>Exploitation : sauvegarde à chaud, supervision, rétention, demandes groupées.</summary>
public sealed class OperationsTests : IDisposable
{
    private readonly TestServerHost _host = new(b => b.UseSetting("SPOKAPILOTE_BACKUP_KEEP", "2"));

    public void Dispose() => _host.Dispose();

    private ServerStore Store => _host.Factory.Services.GetRequiredService<ServerStore>();

    [Fact]
    public async Task Hot_backup_is_a_consistent_standalone_copy_and_old_ones_are_pruned()
    {
        var admin = _host.Client(AdminToken);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports", Json(ReportFixture.Typical($"PC-{i}").Json()))).StatusCode);

        var r = await admin.PostAsync("/api/admin/backup?wait=true", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var file = (await ReadJson(r)).GetProperty("file").GetProperty("name").GetString()!;
        var path = Path.Combine(_host.DataDir, "backups", file);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        using (var copy = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            copy.Open();
            using var cmd = copy.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM machines; ";
            Assert.Equal(3L, cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)ServerStore.SchemaVersion, cmd.ExecuteScalar());
        }

        // Rétention : 2 sauvegardes gardées (SPOKAPILOTE_BACKUP_KEEP).
        for (var i = 0; i < 2; i++)
        {
            _host.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/backup?wait=true", null)).StatusCode);
        }
        var list = await ReadJson(await admin.GetAsync("/api/admin/backups"));
        Assert.Equal(2, list.GetProperty("backups").GetArrayLength());
        Assert.False(File.Exists(path));

        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AgentToken).PostAsync("/api/admin/backup", null)).StatusCode);
        var audit = await ReadJson(await admin.GetAsync("/api/audit?category=maintenance"));
        Assert.Equal(3, audit.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Metrics_are_reserved_to_administrators_in_json_and_prometheus_formats()
    {
        await _host.Client(AgentToken).PostAsync("/api/reports", Json(ReportFixture.Typical("PC-01").Json()));
        var admin = _host.Client(AdminToken);
        await admin.GetAsync("/api/machines");
        var m = await ReadJson(await admin.GetAsync("/api/metrics"));
        Assert.Equal(1, m.GetProperty("fleet").GetProperty("machines").GetInt32());
        Assert.Equal(1, m.GetProperty("fleet").GetProperty("toProcess").GetInt32());
        Assert.True(m.GetProperty("database").GetProperty("fileBytes").GetInt64() > 0);
        Assert.Contains(m.GetProperty("requests").GetProperty("routes").EnumerateArray(), r => r.GetProperty("route").GetString() == "GET /api/machines");
        Assert.Equal(2, m.GetProperty("queues").GetArrayLength());

        var text = await (await admin.GetAsync("/api/metrics?format=prometheus")).Content.ReadAsStringAsync();
        Assert.Contains("spokapilote_machines 1", text);
        Assert.Contains("spokapilote_http_requests_total{route=\"GET /api/machines\"}", text);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AgentToken).GetAsync("/api/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(null).GetAsync("/api/metrics")).StatusCode);
    }

    [Fact]
    public void History_retention_by_age_keeps_the_latest_report_of_every_machine()
    {
        var now = FleetSeed.Now;
        _host.Client(null);
        foreach (var (name, days) in new[] { ("PC-A", 400), ("PC-A", 200), ("PC-A", 10), ("PC-B", 500), ("PC-B", 450) })
        {
            var json = new ReportFixture(name, timestamp: now.AddDays(-days)).Json();
            Store.SaveReport(ReportParser.Parse(json), json, now.AddDays(-days));
        }
        Assert.Equal(3, Store.PurgeOldReports(now, 180, batch: 1));
        Assert.Single(Store.GetHistory("PC-A"));                     // 10 jours : gardé
        Assert.Single(Store.GetHistory("PC-B"));                     // le plus récent est gardé, même ancien
        Assert.Equal(1, Store.GetMachine("PC-A")!.ReportCount);
        Assert.Equal(1, Store.GetMachine("PC-B")!.ReportCount);
        Assert.Equal(0, Store.PurgeOldReports(now, 180));
    }

    [Fact]
    public async Task Fleet_wide_actions_are_created_in_batches_without_duplicates()
    {
        _host.Client(null);
        for (var i = 0; i < 1203; i++)
        {
            var json = new ReportFixture($"PC-{i:D4}").Json();
            Store.SaveReport(ReportParser.Parse(json), json, FleetSeed.Now);
        }
        var admin = _host.Client(AdminToken);
        var first = await ReadJson(await admin.PostAsync("/api/jobs", Json(new { type = "scan", target = "all" })));
        Assert.Equal(1203, first.GetProperty("created").GetArrayLength());
        Assert.Equal("PC-0000", first.GetProperty("created")[0].GetProperty("machine").GetString());
        var again = await ReadJson(await admin.PostAsync("/api/jobs", Json(new { type = "scan", target = "all" })));
        Assert.Equal(0, again.GetProperty("created").GetArrayLength());
        Assert.Equal(1203, again.GetProperty("skipped").GetArrayLength());
        Assert.Contains("déjà en attente", again.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.Equal(1203, Store.GetJobStats(FleetSeed.Now).Pending);
    }

    [Fact]
    public void Audit_and_alert_event_retention_keep_the_most_recent_entries()
    {
        _host.Client(null);
        var now = FleetSeed.Now;
        for (var i = 0; i < 30; i++) Store.AddAudit(now.AddMinutes(i), "x", null, "login.success", $"n{i}", null, null, true, maxEntries: 10, maxDays: 30);
        var entries = Store.GetAudit();
        Assert.Equal(10, entries.Count);
        Assert.Equal("n29", entries[0].Target);
        Assert.Equal("n20", entries[^1].Target);
        Assert.Equal(5, Store.PurgeAudit(now.AddDays(30).AddMinutes(25), 100, 30));   // ancienneté
    }
}
