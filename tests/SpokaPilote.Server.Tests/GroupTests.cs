using System.Net;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Règles de groupe (sans état).</summary>
public sealed class GroupRulesTests
{
    private static StoredMachine Machine(string name = "PC-COMPTA-01", string manufacturer = "Dell Inc.", string model = "Latitude 7420",
        string? ip = "10.1.20.15", long id = 1)
    {
        var report = ReportParser.Parse(new ReportFixture(name, manufacturer, model).Json());
        return new StoredMachine(report.Name, DateTimeOffset.UtcNow, 1, report, id, null, null, ip);
    }

    private static GroupRules Rules(string match, params (string Field, string Op, string Value)[] conditions) => GroupMatcher.Normalize(new GroupRules
    {
        Match = match,
        Conditions = conditions.Select(c => new GroupCondition { Field = c.Field, Operator = c.Op, Value = c.Value }).ToList(),
    });

    [Theory]
    [InlineData("name", "startsWith", "pc-compta", true)]
    [InlineData("name", "startsWith", "COMPTA", false)]
    [InlineData("name", "contains", "compta", true)]
    [InlineData("name", "equals", "pc-compta-01", true)]
    [InlineData("name", "equals", "PC-COMPTA", false)]
    [InlineData("name", "wildcard", "PC-*-0?", true)]
    [InlineData("name", "wildcard", "PC-*-1?", false)]
    [InlineData("name", "wildcard", "PC-COMPTA", false)]           // le motif porte sur tout le nom
    [InlineData("name", "wildcard", "pc-compta-01", true)]
    [InlineData("name", "wildcard", "PC.COMPTA.01", false)]        // le point n'est pas un joker
    [InlineData("manufacturer", "startsWith", "dell", true)]
    [InlineData("manufacturer", "contains", "Lenovo", false)]
    [InlineData("model", "contains", "latitude", true)]
    [InlineData("model", "equals", "Latitude 7420", true)]
    [InlineData("os", "contains", "Windows 11", true)]
    [InlineData("os", "contains", "24H2", true)]
    [InlineData("os", "startsWith", "10.0.26100", true)]
    [InlineData("os", "contains", "Windows 10", false)]
    [InlineData("subnet", "inSubnet", "10.1.20.0/24", true)]
    [InlineData("subnet", "inSubnet", "10.1.0.0/16", true)]
    [InlineData("subnet", "inSubnet", "10.1.21.0/24", false)]
    [InlineData("subnet", "inSubnet", "10.1.20.15", true)]
    [InlineData("subnet", "inSubnet", "192.168.0.0/16, 10.1.20.0/28", true)]
    [InlineData("subnet", "inSubnet", "0.0.0.0/0", true)]
    [InlineData("subnet", "inSubnet", "fd00::/8", false)]
    public void Conditions_match_case_insensitively(string field, string op, string value, bool expected) =>
        Assert.Equal(expected, GroupMatcher.Matches(Rules("all", (field, op, value)), Machine()));

    [Fact]
    public void Subnets_handle_ipv6_ipv4_mapped_and_unknown_addresses()
    {
        Assert.True(GroupMatcher.Matches(Rules("all", ("subnet", "inSubnet", "fd00:1::/32")), Machine(ip: "fd00:1:2::5")));
        Assert.True(GroupMatcher.Matches(Rules("all", ("subnet", "inSubnet", "10.1.20.0/24")), Machine(ip: "::ffff:10.1.20.9")));
        Assert.False(GroupMatcher.Matches(Rules("all", ("subnet", "inSubnet", "10.1.20.0/24")), Machine(ip: null)));
        Assert.True(GroupMatcher.InSubnet(System.Net.IPAddress.Parse("10.1.20.200"), "10.1.20.128/25"));
        Assert.False(GroupMatcher.InSubnet(System.Net.IPAddress.Parse("10.1.20.100"), "10.1.20.128/25"));
    }

    [Fact]
    public void All_and_any_combine_conditions()
    {
        var m = Machine();
        var dell = ("manufacturer", "contains", "dell");
        var hp = ("manufacturer", "contains", "hp");
        Assert.True(GroupMatcher.Matches(Rules("all", dell, ("name", "startsWith", "PC-COMPTA")), m));
        Assert.False(GroupMatcher.Matches(Rules("all", dell, hp), m));
        Assert.True(GroupMatcher.Matches(Rules("any", dell, hp), m));
        Assert.False(GroupMatcher.Matches(Rules("any", hp, ("name", "contains", "RH")), m));
    }

    [Fact]
    public void Empty_rules_match_nothing_and_manual_members_always_belong()
    {
        var m = Machine(id: 7);
        Assert.False(GroupMatcher.Matches(new GroupRules(), m));
        var g = new GroupRecord(1, "Manuel", null, new GroupRules(), new HashSet<long> { 7 }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(GroupMatcher.IsMember(g, m));
        Assert.False(GroupMatcher.IsMember(g, Machine(id: 8)));
        var byRule = g with { ManualMembers = new HashSet<long>(), Rules = Rules("all", ("name", "contains", "compta")) };
        Assert.True(GroupMatcher.IsMember(byRule, Machine(id: 8)));
    }

    [Theory]
    [InlineData("inconnu", "contains", "x")]
    [InlineData("name", "inconnu", "x")]
    [InlineData("name", "contains", "  ")]
    [InlineData("name", "inSubnet", "10.0.0.0/8")]
    [InlineData("subnet", "contains", "10.0.0.0/8")]
    [InlineData("subnet", "inSubnet", "10.0.0.0/33")]
    [InlineData("subnet", "inSubnet", "pas-une-adresse")]
    public void Invalid_rules_are_refused_with_a_message(string field, string op, string value) =>
        Assert.Throws<GroupValidationException>(() => Rules("all", (field, op, value)));

    [Fact]
    public void Normalize_trims_and_canonicalizes()
    {
        var r = GroupMatcher.Normalize(new GroupRules
        {
            Match = " ANY ",
            Conditions = [new GroupCondition { Field = "Name", Operator = "STARTSWITH", Value = "  PC-  " }],
        });
        Assert.Equal("any", r.Match);
        Assert.Equal(("name", "startsWith", "PC-"), (r.Conditions[0].Field, r.Conditions[0].Operator, r.Conditions[0].Value));
        Assert.Throws<GroupValidationException>(() => GroupMatcher.Normalize(new GroupRules { Match = "souvent" }));
        Assert.Throws<GroupValidationException>(() => GroupMatcher.Normalize(new GroupRules
        {
            Conditions = Enumerable.Range(0, 21).Select(_ => new GroupCondition { Field = "name", Operator = "contains", Value = "x" }).ToList(),
        }));
    }
}

/// <summary>Groupes par l'API : membres, filtres du parc, action sur un groupe.</summary>
public sealed class GroupApiTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task Report(string name, string manufacturer, string ip)
    {
        var client = _host.Client(AgentToken, ip);
        var r = await client.PostAsync("/api/reports", Json(new ReportFixture(name, manufacturer).Json()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Groups_combine_manual_members_and_rules_and_filter_the_fleet()
    {
        await Report("PC-COMPTA-01", "Dell Inc.", "10.1.20.11");
        await Report("PC-COMPTA-02", "HP", "10.1.20.12");
        await Report("PC-RH-01", "Dell Inc.", "10.2.0.5");
        var admin = _host.Client(AdminToken);

        var created = await admin.PostAsync("/api/groups", Json(new
        {
            name = "Siège Dell",
            rules = new { match = "all", conditions = new[] { new { field = "manufacturer", @operator = "contains", value = "dell" }, new { field = "subnet", @operator = "inSubnet", value = "10.1.0.0/16" } } },
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadJson(created)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/groups", Json(new { name = "siège dell" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/groups", Json(new { name = "" }))).StatusCode);

        var members = await admin.PostAsync($"/api/groups/{id}/members", Json(new { add = new[] { "pc-rh-01", "PC-FANTOME" } }));
        var m = await ReadJson(members);
        Assert.Equal(1, m.GetProperty("changes").GetInt32());
        Assert.Equal("PC-FANTOME", m.GetProperty("unknown")[0].GetString());
        Assert.Equal(2, m.GetProperty("group").GetProperty("memberCount").GetInt32()); // COMPTA-01 (règle) + RH-01 (manuel)

        var detail = await ReadJson(await admin.GetAsync($"/api/groups/{id}"));
        var names = detail.GetProperty("members").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(["PC-COMPTA-01", "PC-RH-01"], names);
        Assert.Equal("10.1.20.11", detail.GetProperty("members")[0].GetProperty("lastIp").GetString());

        var fleet = await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"));
        Assert.Equal(2, fleet.GetProperty("machineCount").GetInt32());
        Assert.Equal(3, (await ReadJson(await admin.GetAsync("/api/fleet"))).GetProperty("machineCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/fleet?group=999")).StatusCode);
        var rows = await ReadJson(await admin.GetAsync("/api/machines"));
        Assert.Contains(id, rows.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "PC-RH-01").GetProperty("groups").EnumerateArray().Select(g => g.GetInt64()));
        Assert.Equal(2, (await ReadJson(await admin.GetAsync($"/api/machines?group={id}"))).GetArrayLength());

        // Action sur tout le groupe.
        var jobs = await ReadJson(await admin.PostAsync("/api/jobs", Json(new { type = "scan", target = "group", group = id })));
        Assert.Equal(2, jobs.GetProperty("created").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/jobs", Json(new { type = "scan", target = "group", group = 999 }))).StatusCode);
        Assert.Equal(2, (await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"))).GetProperty("pendingJobs").GetInt32());

        // Retrait manuel : le poste n'est plus membre (il ne remplit pas les règles).
        await admin.PostAsync($"/api/groups/{id}/members", Json(new { remove = new[] { "PC-RH-01" } }));
        Assert.Equal(1, (await ReadJson(await admin.GetAsync($"/api/fleet?group={id}"))).GetProperty("machineCount").GetInt32());

        // Aperçu des règles.
        var preview = await ReadJson(await admin.PostAsync("/api/groups/preview", Json(new { rules = new { match = "any", conditions = new[] { new { field = "name", @operator = "wildcard", value = "PC-*-02" } } } })));
        Assert.Equal(1, preview.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_group_used_by_a_schedule_cannot_be_deleted()
    {
        var admin = _host.Client(AdminToken);
        var id = (await ReadJson(await admin.PostAsync("/api/groups", Json(new { name = "Atelier" })))).GetProperty("id").GetInt64();
        var schedule = await admin.PostAsync("/api/schedules", Json(new { name = "Analyse", type = "scan", groupId = id, frequency = "daily", time = "21:00" }));
        Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/groups/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/schedules/{(await ReadJson(schedule)).GetProperty("id").GetInt64()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/groups/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/groups/{id}")).StatusCode);
    }
}
