using System.Net;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Security;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Limitation des tentatives d'authentification refusées (par adresse IP).</summary>
public sealed class AuthFailureLimiterTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Limiter_blocks_after_the_threshold_for_one_minute()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        var limiter = new AuthFailureLimiter(new ServerOptions { DataDirectory = ".", AuthFailuresPerMinute = 3 }, clock);
        var ip = IPAddress.Parse("10.1.2.3");
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(limiter.BlockedFor(ip));
            limiter.RecordFailure(ip);
        }
        Assert.NotNull(limiter.BlockedFor(ip));
        Assert.NotNull(limiter.BlockedFor(IPAddress.Parse("::ffff:10.1.2.3"))); // même adresse en IPv6 mappée
        Assert.Null(limiter.BlockedFor(IPAddress.Parse("10.1.2.4")));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(limiter.BlockedFor(ip));
    }

    [Fact]
    public void Disabled_when_set_to_zero()
    {
        var limiter = new AuthFailureLimiter(new ServerOptions { DataDirectory = ".", AuthFailuresPerMinute = 0 }, TimeProvider.System);
        for (var i = 0; i < 100; i++) limiter.RecordFailure(IPAddress.Loopback);
        Assert.Null(limiter.BlockedFor(IPAddress.Loopback));
    }

    [Fact]
    public async Task Api_answers_429_after_ten_refused_tokens_even_with_a_valid_token()
    {
        var attacker = _host.Client("mauvais-jeton", ip: "192.0.2.10");
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/machines")).StatusCode);
        var blocked = await attacker.GetAsync("/api/machines");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        // Même un bon jeton est refusé depuis cette adresse pendant la minute...
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _host.Client(AdminToken, ip: "192.0.2.10").GetAsync("/api/machines")).StatusCode);
        // ...mais pas depuis une autre adresse, et les requêtes sans jeton ne comptent pas.
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken, ip: "192.0.2.11").GetAsync("/api/machines")).StatusCode);
        var anonymous = _host.Client(null, ip: "192.0.2.12");
        for (var i = 0; i < 15; i++) await anonymous.GetAsync("/api/machines");
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken, ip: "192.0.2.12").GetAsync("/api/machines")).StatusCode);
        // La sonde de vie reste publique.
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(null, ip: "192.0.2.10").GetAsync("/api/health")).StatusCode);

        _host.Clock.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken, ip: "192.0.2.10").GetAsync("/api/machines")).StatusCode);
    }
}

/// <summary>Objet facultatif « health » (contrat partagé avec l'équipe du poste).</summary>
public sealed class HealthContractTests
{
    [Fact]
    public void Health_is_parsed_sorted_and_clamped()
    {
        var report = ReportParser.Parse(new ReportFixture("PC-01").WithHealth("""
            { "score": 142.6, "grade": "b", "findings": [
              { "severity": "info", "code": "uptime", "title": "Redémarré il y a 40 jours" },
              { "severity": "WARNING", "code": "old-bios", "title": "BIOS ancien", "detail": "2019" },
              { "severity": "bizarre", "code": "x", "title": "Gravité inconnue" },
              { "severity": "critical", "code": "known-bad-driver", "title": "Pilote dangereux", "deviceId": "PCI\\VEN_1" } ] }
            """).Json());
        var h = report.Health!;
        Assert.Equal(100, h.Score);
        Assert.Equal("B", h.Grade);
        Assert.Equal(["critical", "warning", "info", "info"], h.Findings.Select(f => f.Severity));
        Assert.True(h.HasDangerousDriver);
        Assert.Equal(1, h.Critical);
        Assert.Equal(1, h.Warnings);
        Assert.Equal("PCI\\VEN_1", h.Findings[0].DeviceId);
    }

    [Theory]
    [InlineData("""{ "score": -5, "grade": "Z" }""", 0, null)]
    [InlineData("""{ "grade": "E" }""", null, "E")]
    [InlineData("""{ "score": "80" }""", null, null)]
    public void Invalid_values_are_tolerated(string json, int? score, string? grade)
    {
        var h = ReportParser.Parse(new ReportFixture("PC-01").WithHealth(json).Json()).Health!;
        Assert.Equal(score, h.Score);
        Assert.Equal(grade, h.Grade);
        Assert.Empty(h.Findings);
    }

    [Fact]
    public void Without_health_nothing_changes()
    {
        var report = ReportParser.Parse(ReportFixture.Typical().Json());
        Assert.Null(report.Health);
        var row = FleetAggregator.ToRow(new StoredMachine(report.Name, DateTimeOffset.UtcNow, 1, report), DateTimeOffset.UtcNow, 7);
        Assert.Null(row.HealthScore);
        Assert.False(row.DangerousDriver);
        var summary = FleetAggregator.Summarize([new StoredMachine(report.Name, DateTimeOffset.UtcNow, 1, report)], DateTimeOffset.UtcNow, 7);
        Assert.Null(summary.AverageHealth);
        Assert.Equal(0, summary.HealthMachines);
    }

    [Fact]
    public void Fleet_average_health_only_counts_machines_that_send_it()
    {
        StoredMachine M(string name, string? health)
        {
            var f = new ReportFixture(name);
            if (health is not null) f.WithHealth(health);
            var r = ReportParser.Parse(f.Json());
            return new StoredMachine(r.Name, DateTimeOffset.UtcNow, 1, r);
        }
        var summary = FleetAggregator.Summarize([M("A", """{ "score": 90 }"""), M("B", """{ "score": 61, "findings": [ { "severity": "critical", "code": "known-bad-driver", "title": "x" } ] }"""), M("C", null)],
            DateTimeOffset.UtcNow, 7);
        Assert.Equal(75.5, summary.AverageHealth);
        Assert.Equal(2, summary.HealthMachines);
        Assert.Equal(1, summary.DangerousDrivers);
    }

    [Fact]
    public async Task Machine_detail_exposes_health_through_the_api()
    {
        using var host = new TestServerHost();
        Assert.Equal(HttpStatusCode.OK, (await host.Client(AgentToken).PostAsync("/api/reports",
            Json(new ReportFixture("PC-H").WithHealth("""{ "score": 55, "grade": "D", "findings": [ { "severity": "warning", "code": "c", "title": "t" } ] }""").Json()))).StatusCode);
        var admin = host.Client(AdminToken);
        var detail = await ReadJson(await admin.GetAsync("/api/machines/PC-H"));
        Assert.Equal(55, detail.GetProperty("report").GetProperty("health").GetProperty("score").GetInt32());
        Assert.Equal("D", detail.GetProperty("machine").GetProperty("healthGrade").GetString());
        var fleet = await ReadJson(await admin.GetAsync("/api/fleet"));
        Assert.Equal(55, fleet.GetProperty("averageHealth").GetDouble());
    }
}
