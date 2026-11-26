using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using SpokaPilote.Server.Security;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Sessions de la console : cookie, protection CSRF, expiration glissante, limitation des tentatives.</summary>
public sealed class SessionTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Login_sets_a_hardened_session_cookie_and_returns_a_csrf_token()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        var r = await _host.TryLoginAsync("tech", Password);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(SessionService.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // cookie de session (fermeture du navigateur)
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);  // HTTP de test ; Secure en HTTPS
        var body = await ReadJson(r);
        Assert.Equal(43, body.GetProperty("csrf").GetString()!.Length);
        Assert.Equal("technician", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Cookie_is_secure_when_the_request_is_https()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        var client = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session") { Content = Json(new { login = "tech", password = Password }) };
        request.Headers.Add("X-SpokaPilote-CSRF", "1");
        var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("secure", Assert.Single(r.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_requires_the_anti_csrf_header_and_gives_one_generic_error()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.TryLoginAsync("tech", Password, withHeader: false)).StatusCode);
        var wrong = await _host.TryLoginAsync("tech", "mauvais-mot-de-passe");
        var unknown = await _host.TryLoginAsync("inconnu", "mauvais-mot-de-passe");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // Même message : la réponse ne révèle pas si le compte existe.
        Assert.Equal((await ReadJson(wrong)).GetProperty("error").GetString(), (await ReadJson(unknown)).GetProperty("error").GetString());
        Assert.False(wrong.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task State_changing_requests_need_the_session_csrf_token()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        var client = await _host.LoginAsync("tech");
        var csrf = client.DefaultRequestHeaders.GetValues("X-SpokaPilote-CSRF").Single();

        client.DefaultRequestHeaders.Remove("X-SpokaPilote-CSRF");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/groups")).StatusCode); // lecture : pas de jeton requis
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/groups", Json(new { name = "A" }))).StatusCode);
        client.DefaultRequestHeaders.Add("X-SpokaPilote-CSRF", "un-autre-jeton");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/groups", Json(new { name = "A" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/session")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-SpokaPilote-CSRF");
        client.DefaultRequestHeaders.Add("X-SpokaPilote-CSRF", csrf);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/groups", Json(new { name = "A" }))).StatusCode);

        // Le jeton d'une autre session ne convient pas.
        var other = await _host.LoginAsync("tech");
        var otherCsrf = other.DefaultRequestHeaders.GetValues("X-SpokaPilote-CSRF").Single();
        Assert.NotEqual(csrf, otherCsrf);
        client.DefaultRequestHeaders.Remove("X-SpokaPilote-CSRF");
        client.DefaultRequestHeaders.Add("X-SpokaPilote-CSRF", otherCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/groups", Json(new { name = "B" }))).StatusCode);
    }

    [Fact]
    public async Task Logout_closes_the_session()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        var client = await _host.LoginAsync("tech");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/machines")).StatusCode);
    }

    [Fact]
    public async Task Sessions_expire_after_idle_time_with_sliding_renewal()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        var active = await _host.LoginAsync("tech");
        var idle = await _host.LoginAsync("tech");

        _host.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/api/machines")).StatusCode); // prolonge la session
        _host.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/api/machines")).StatusCode);  // 14 h après, toujours active
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/api/machines")).StatusCode); // 14 h sans activité (> 8 h)

        // Durée maximale : 7 jours, même avec de l'activité.
        for (var i = 0; i < 24; i++)
        {
            _host.Clock.Advance(TimeSpan.FromHours(7));
            if ((await active.GetAsync("/api/machines")).StatusCode != HttpStatusCode.OK) break;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await active.GetAsync("/api/machines")).StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_address()
    {
        await _host.CreateUserAsync("tech", Roles.Technician);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _host.TryLoginAsync("tech", "mauvais-" + i, ip: "192.0.2.50")).StatusCode);
        var blocked = await _host.TryLoginAsync("tech", Password, ip: "192.0.2.50");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); // même avec le bon mot de passe
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await _host.TryLoginAsync("tech", Password, ip: "192.0.2.51")).StatusCode);
        _host.Clock.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal(HttpStatusCode.OK, (await _host.TryLoginAsync("tech", Password, ip: "192.0.2.50")).StatusCode);
    }

    [Fact]
    public async Task Bearer_tokens_need_no_csrf_header_and_agents_keep_working()
    {
        // Scripts d'administration (jeton) : aucun changement par rapport à la 3.0.
        Assert.Equal(HttpStatusCode.Created, (await _host.Client(AdminToken).PostAsync("/api/groups", Json(new { name = "Script" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken).PutAsync("/api/policy", Json("{}"))).StatusCode);

        // Poste 3.0 : rapport, politique, file de tâches et résultat, avec les mêmes en-têtes et le même JSON.
        const string id = "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";
        var agent = _host.Agent(id, "PC-01");
        var report = await agent.PostAsync("/api/reports", Json(ReportFixture.Typical("PC-01").WithMachineId(id).Json()));
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var ack = await ReadJson(report);
        Assert.Equal("PC-01", ack.GetProperty("machine").GetString());
        Assert.True(ack.GetProperty("reportId").GetInt64() > 0);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AdminToken).PostAsync("/api/jobs", Json(new { type = "scan", machines = new[] { "PC-01" } }))).StatusCode);
        var poll = await ReadJson(await agent.GetAsync("/api/agent/jobs"));
        Assert.True(poll.GetProperty("known").GetBoolean());
        var job = Assert.Single(poll.GetProperty("jobs").EnumerateArray());
        Assert.Equal(["id", "type", "dryRun", "createdAt", "createdBy"], job.EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/api/agent/jobs/{job.GetProperty("id").GetInt64()}/result",
            Json(new { success = true, exitCode = 0, message = "ok" }))).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_unknown_cookie_is_refused()
    {
        var client = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionService.CookieName}=forge");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/machines")).StatusCode);
    }
}
