using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Tests;

/// <summary>Horloge réglable pour les tests (expiration, anti-spam, limitation).</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Serveur complet en mémoire pour les tests d'intégration : base dans un dossier temporaire, horloge réglable,
/// adresse IP du client simulée par l'en-tête X-Test-Ip (le serveur de test n'en fournit pas).
/// </summary>
internal sealed class TestServerHost : IDisposable
{
    public const string AdminToken = "admin-jeton-de-test-0123456789abcdef";
    public const string AgentToken = "agent-jeton-de-test-0123456789abcdef";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "sp-server-it-" + Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    public WebApplicationFactory<Program> Factory { get; }

    public TestServerHost(Action<IWebHostBuilder>? configure = null)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SPOKAPILOTE_DATA_DIR", DataDir)
                .UseSetting(TokenService.AdminVariable, AdminToken)
                .UseSetting(TokenService.AgentVariable, AgentToken);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            });
            configure?.Invoke(b);
        });
    }

    public HttpClient Client(string? token, string? ip = null)
    {
        var client = Factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ip is not null) client.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        return client;
    }

    /// <summary>Client d'un poste : jeton d'enrôlement + identité (en-têtes de l'agent).</summary>
    public HttpClient Agent(string machineId, string name)
    {
        var client = Client(AgentToken);
        client.DefaultRequestHeaders.Add("X-SpokaPilote-Machine-Id", machineId);
        client.DefaultRequestHeaders.Add("X-SpokaPilote-Machine", name);
        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { /* dossier temporaire */ }
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    public static StringContent Json(object value) => Json(JsonSerializer.Serialize(value));

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, Func<Task> nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Ip"].ToString(), out var ip)) context.Connection.RemoteIpAddress = ip;
                return nextMiddleware();
            });
            next(app);
        };
    }
}
