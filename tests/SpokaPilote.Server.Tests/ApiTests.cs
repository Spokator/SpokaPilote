using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Tests;

/// <summary>Tests d'intégration de l'API : serveur complet en mémoire, base SQLite dans un dossier temporaire.</summary>
public sealed class ApiTests : IDisposable
{
    private const string AdminToken = "admin-jeton-de-test-0123456789abcdef";
    private const string AgentToken = "agent-jeton-de-test-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-api-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("SPOKAPILOTE_DATA_DIR", _dir)
            .UseSetting(TokenService.AdminVariable, AdminToken)
            .UseSetting(TokenService.AgentVariable, AgentToken)
            .UseSetting("SPOKAPILOTE_MAX_REPORT_MB", "1"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* dossier temporaire */ }
    }

    private HttpClient Client(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Health_is_public_and_static_console_is_served()
    {
        var client = Client(null);
        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await ReadJson(health)).GetProperty("status").GetString());

        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("SpokaPilote", await page.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Tokens_are_checked_and_roles_are_separated()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("mauvais-jeton").GetAsync("/api/machines")).StatusCode);
        // Le jeton d'enrôlement (copié sur chaque poste) n'ouvre pas l'administration.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AgentToken).GetAsync("/api/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AgentToken).PutAsync("/api/policy", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AdminToken).PostAsync("/api/reports", Json(ReportFixture.Typical().Json()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(AdminToken).GetAsync("/api/machines")).StatusCode);

        var who = await ReadJson(await Client(AgentToken).GetAsync("/api/whoami"));
        Assert.Equal("agent", who.GetProperty("access").GetString());
        who = await ReadJson(await Client(AdminToken).GetAsync("/api/whoami"));
        Assert.Equal("admin", who.GetProperty("access").GetString());
    }

    [Fact]
    public async Task Reports_flow_into_machines_fleet_and_history()
    {
        var agent = Client(AgentToken);
        var admin = Client(AdminToken);

        var post = await agent.PostAsync("/api/reports", Json(ReportFixture.Typical("PC-01").Json()));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("PC-01", (await ReadJson(post)).GetProperty("machine").GetString());

        // Envoi compressé (comme le client SpokaPilote).
        var gz = new MemoryStream();
        await using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            await z.WriteAsync(Encoding.UTF8.GetBytes(ReportFixture.Typical("PC-02", model: "Latitude 5440").Json()));
        var compressed = new ByteArrayContent(gz.ToArray());
        compressed.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        compressed.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/api/reports", compressed)).StatusCode);

        var machines = await ReadJson(await admin.GetAsync("/api/machines"));
        Assert.Equal(2, machines.GetArrayLength());

        var fleet = await ReadJson(await admin.GetAsync("/api/fleet"));
        Assert.Equal(2, fleet.GetProperty("machineCount").GetInt32());
        Assert.Equal(2, fleet.GetProperty("toProcess").GetInt32());
        var missing = fleet.GetProperty("missingDrivers");
        Assert.Equal(1, missing.GetArrayLength());
        Assert.Equal(2, missing[0].GetProperty("count").GetInt32());

        var detail = await ReadJson(await admin.GetAsync("/api/machines/pc-01"));
        Assert.Equal("PC-01", detail.GetProperty("machine").GetProperty("name").GetString());
        Assert.Equal(3, detail.GetProperty("report").GetProperty("problemDevices").GetArrayLength());
        var historyId = detail.GetProperty("history")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/machines/PC-01/reports/{historyId}")).StatusCode);

        var raw = await admin.GetAsync("/api/machines/PC-01/raw");
        Assert.Equal("application/json", raw.Content.Headers.ContentType!.MediaType);
        Assert.Contains("DESCRIPTOR_FAILURE", await raw.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/machines/PC-01")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/machines/PC-01")).StatusCode);
    }

    [Fact]
    public async Task Invalid_and_oversized_reports_are_rejected()
    {
        var agent = Client(AgentToken);
        var bad = await agent.PostAsync("/api/reports", Json("{\"system\":{}}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("computerName", (await ReadJson(bad)).GetProperty("error").GetString());

        // Limite réglée à 1 Mo pour ce test.
        var huge = "{\"system\":{\"computerName\":\"PC-1\"},\"devices\":[],\"padding\":\"" + new string('x', 1_200_000) + "\"}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await agent.PostAsync("/api/reports", Json(huge))).StatusCode);

        // Bombe gzip : 1 Mo compressé en quelques Ko, refusée après décompression.
        var gz = new MemoryStream();
        await using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
            await z.WriteAsync(Encoding.UTF8.GetBytes(huge));
        Assert.True(gz.Length < 50_000);
        var bomb = new ByteArrayContent(gz.ToArray());
        bomb.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await agent.PostAsync("/api/reports", bomb)).StatusCode);
    }

    [Theory]
    [InlineData("/api/policy")]
    [InlineData("/api/printer-profile")]
    public async Task Documents_are_validated_stored_and_served_to_agents(string route)
    {
        var admin = Client(AdminToken);
        var agent = Client(AgentToken);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.GetAsync(route)).StatusCode);

        var invalid = await admin.PutAsync(route, Json("{\"version\": 1,"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("JSON invalide", (await ReadJson(invalid)).GetProperty("error").GetString());

        const string doc = "{\n  \"version\": 2,\n  \"name\": \"Parc siège\",\n  \"rules\": [ { \"action\": \"block\" } ]\n}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(route, Json(doc))).StatusCode);

        var get = await agent.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(doc, await get.Content.ReadAsStringAsync()); // JSON brut, tel qu'enregistré
        Assert.Equal("application/json", get.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(get.Content.Headers.LastModified);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task Enrollment_exposes_the_agent_token_to_admins_only()
    {
        var info = await ReadJson(await Client(AdminToken).GetAsync("/api/enrollment"));
        Assert.Equal(AgentToken, info.GetProperty("agentToken").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AgentToken).GetAsync("/api/enrollment")).StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_return_json_404()
    {
        var r = await Client(AdminToken).GetAsync("/api/inexistant");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
    }
}
