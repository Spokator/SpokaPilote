using System.IO.Compression;
using System.Net;
using System.Text;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

public class ServerClientTests
{
    // ------------------------------------------------------------------ validation de l'adresse

    [Theory]
    [InlineData("https://spokapilote.mon-entreprise.fr", "https://spokapilote.mon-entreprise.fr/")]
    [InlineData("https://srv.local:8443/spokapilote", "https://srv.local:8443/spokapilote/")]
    [InlineData("  https://srv/console/  ", "https://srv/console/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080/")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/")]
    public void Accepts_https_and_local_http(string url, string expected) =>
        Assert.Equal(expected, ServerClient.ValidateUrl(url).ToString());

    [Theory]
    [InlineData("http://spokapilote.mon-entreprise.fr")]   // HTTP hors localhost : jeton en clair
    [InlineData("http://192.168.1.10:8080")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("ftp://srv")]
    [InlineData("srv.mon-entreprise.fr")]
    [InlineData("https://user:pass@srv")]
    [InlineData("https://srv/?x=1")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_unsafe_or_malformed_urls(string? url)
    {
        var ex = Assert.Throws<ServerException>(() => ServerClient.ValidateUrl(url));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Http_rejection_message_explains_https()
    {
        var ex = Assert.Throws<ServerException>(() => ServerClient.ValidateUrl("http://srv"));
        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public void Rejects_tokens_with_whitespace() =>
        Assert.Throws<ServerException>(() => new ServerClient("https://srv", "abc\r\nX-Injection: 1", new StubHandler()));

    // ------------------------------------------------------------------ échanges (serveur simulé)

    private static ScanReport Report() => new()
    {
        Timestamp = new DateTime(2026, 11, 25, 10, 0, 0),
        System = new SystemInfo { ComputerName = "PC-TEST", Manufacturer = "Dell Inc.", Model = "Latitude 7420" },
        Devices = [new DeviceInfo { InstanceId = "PCI\\VEN_8086\\1", Name = "Carte réseau", HardwareIds = ["PCI\\VEN_8086&DEV_15F3"] }],
    };

    [Fact]
    public async Task Upload_sends_gzipped_json_with_bearer_token()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "{\"machine\":\"PC-TEST\",\"healthy\":true,\"driverProblems\":0,\"unresolved\":0}"));
        var client = new ServerClient("https://console.exemple.fr/sp", "jeton-agent", handler);

        var result = await client.UploadReportAsync(Report());

        Assert.Equal("PC-TEST", result.Machine);
        Assert.True(result.Healthy);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://console.exemple.fr/sp/api/reports", request.Uri);
        Assert.Equal("Bearer jeton-agent", request.Authorization);
        Assert.Equal("gzip", request.ContentEncoding);
        using var gzip = new GZipStream(new MemoryStream(request.Body!), CompressionMode.Decompress);
        var json = new StreamReader(gzip, Encoding.UTF8).ReadToEnd();
        Assert.Contains("\"computerName\": \"PC-TEST\"", json);
        Assert.Contains("\"devices\"", json);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Jeton refusé")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "trop volumineux")]
    [InlineData(HttpStatusCode.BadRequest, "nom du poste absent")]
    [InlineData(HttpStatusCode.NotFound, "ne semble pas être une console")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Upload_errors_have_clear_messages(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(_ => Respond(status, "{\"error\":\"nom du poste absent\"}"));
        var client = new ServerClient("https://srv", "jeton", handler);
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
        Assert.Contains(expected, ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    // ------------------------------------------------------------------ grands parcs : 415, 503 + Retry-After

    [Fact]
    public async Task Upload_is_resent_uncompressed_when_gzip_is_refused()
    {
        // Intermédiaire (proxy, pare-feu applicatif) qui refuse Content-Encoding : un seul renvoi, en clair.
        var handler = new StubHandler(r => r.Content!.Headers.ContentEncoding.Count > 0
            ? Respond(HttpStatusCode.UnsupportedMediaType, "{\"error\":\"Encodage non pris en charge\"}")
            : Respond(HttpStatusCode.OK, "{\"machine\":\"PC-TEST\",\"healthy\":true,\"reportId\":7}"));
        var result = await new ServerClient("https://srv", "jeton", handler).UploadReportAsync(Report());

        Assert.Equal(7, result.ReportId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("gzip", handler.Requests[0].ContentEncoding);
        Assert.Null(handler.Requests[1].ContentEncoding);
        Assert.Contains("\"computerName\": \"PC-TEST\"", Encoding.UTF8.GetString(handler.Requests[1].Body!));
    }

    [Fact]
    public async Task Busy_console_is_retried_after_retry_after_with_jitter()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var handler = new StubHandler(_ => ++calls < 3 ? Busy(TimeSpan.FromSeconds(30)) : Respond(HttpStatusCode.OK, "{\"machine\":\"PC-TEST\"}"));
        var client = new ServerClient("https://srv", "jeton", handler, (d, _) => { waits.Add(d); return Task.CompletedTask; });

        Assert.Equal("PC-TEST", (await client.UploadReportAsync(Report())).Machine);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, waits.Count);
        Assert.All(waits, w => Assert.InRange(w.TotalSeconds, 30, 37.5)); // délai demandé + 0 à 25 %
        Assert.All(handler.Requests, r => Assert.Equal("gzip", r.ContentEncoding));
    }

    [Fact]
    public async Task Busy_console_retries_are_bounded_and_explained()
    {
        var waits = 0;
        var handler = new StubHandler(_ => Busy(TimeSpan.FromSeconds(10)));
        var client = new ServerClient("https://srv", "jeton", handler, (_, _) => { waits++; return Task.CompletedTask; });
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Contains("surchargée", ex.Message);
        Assert.Equal(1 + ServerClient.MaxUploadRetries, handler.Requests.Count);
        Assert.Equal(ServerClient.MaxUploadRetries, waits);
    }

    [Fact]
    public async Task No_retry_without_retry_after_or_when_it_is_too_long()
    {
        foreach (var response in new Func<HttpResponseMessage>[]
                 {
                     () => Respond(HttpStatusCode.ServiceUnavailable, "{\"error\":\"maintenance\"}"),
                     () => Busy(TimeSpan.FromMinutes(10)),
                 })
        {
            var handler = new StubHandler(_ => response());
            var client = new ServerClient("https://srv", "jeton", handler, (_, _) => throw new InvalidOperationException("pas d'attente attendue"));
            await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task Agent_poll_is_not_retried_on_a_busy_console()
    {
        // L'agent repassera de lui-même à son prochain intervalle : pas d'attente dans la tâche planifiée.
        var handler = new StubHandler(_ => Busy(TimeSpan.FromSeconds(5)));
        var client = new ServerClient("https://srv", "jeton", handler, (_, _) => throw new InvalidOperationException("pas d'attente attendue"));
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.GetPendingJobsAsync("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-TEST"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage Busy(TimeSpan retryAfter)
    {
        var r = Respond(HttpStatusCode.ServiceUnavailable, "{\"error\":\"Console momentanément surchargée (réception des rapports) : nouvel essai dans 30 s.\"}");
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return r;
    }

    [Fact]
    public async Task Redirects_are_refused()
    {
        var handler = new StubHandler(_ =>
        {
            var r = Respond(HttpStatusCode.Found, "");
            r.Headers.Location = new Uri("https://ailleurs.exemple.com/api/reports");
            return r;
        });
        var client = new ServerClient("https://srv", "jeton", handler);
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
        Assert.Contains("redirige", ex.Message);
        Assert.Single(handler.Requests); // aucune requête vers l'adresse de redirection
    }

    [Fact]
    public async Task Unreachable_server_is_reported_without_crashing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connexion refusée"));
        var client = new ServerClient("https://srv", "jeton", handler);
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
        Assert.Contains("injoignable", ex.Message);

        var status = await client.TestAsync();
        Assert.False(status.Reachable);
        Assert.False(status.Authorized);
    }

    [Fact]
    public async Task Missing_token_fails_before_any_request()
    {
        var handler = new StubHandler();
        var client = new ServerClient("https://srv", null, handler);
        await Assert.ThrowsAsync<ServerException>(() => client.UploadReportAsync(Report()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Documents_return_raw_json_or_null_when_absent()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/policy" => Respond(HttpStatusCode.OK, "{\"version\":1,\"name\":\"Parc\",\"rules\":[]}"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        var client = new ServerClient("http://localhost:8080", "jeton", handler);
        Assert.Equal("{\"version\":1,\"name\":\"Parc\",\"rules\":[]}", await client.GetPolicyJsonAsync());
        Assert.Null(await client.GetPrinterProfileJsonAsync());
        Assert.All(handler.Requests, r => Assert.Equal("Bearer jeton", r.Authorization));
    }

    [Fact]
    public async Task Status_checks_health_anonymously_then_the_token()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Respond(HttpStatusCode.OK, "{\"status\":\"ok\",\"version\":\"1.1.0\"}"),
            "/api/whoami" => Respond(HttpStatusCode.OK, "{\"access\":\"agent\",\"version\":\"1.1.0\"}"),
            _ => Respond(HttpStatusCode.NotFound, ""),
        });
        var status = await new ServerClient("https://srv", "jeton", handler).TestAsync();
        Assert.True(status.Reachable);
        Assert.True(status.Authorized);
        Assert.Equal("agent", status.Access);
        Assert.Equal("1.1.0", status.ServerVersion);
        Assert.Null(handler.Requests[0].Authorization); // la sonde de vie ne reçoit jamais le jeton
        Assert.Equal("Bearer jeton", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Status_reports_a_refused_token()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath == "/api/health"
            ? Respond(HttpStatusCode.OK, "{\"status\":\"ok\"}")
            : Respond(HttpStatusCode.Unauthorized, "{\"error\":\"Jeton absent ou invalide\"}"));
        var status = await new ServerClient("https://srv", "mauvais", handler).TestAsync();
        Assert.True(status.Reachable);
        Assert.False(status.Authorized);
        Assert.Contains("Jeton refusé", status.Message);
    }

    // ------------------------------------------------------------------ outillage

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Captured(HttpMethod Method, string Uri, string? Authorization, string? ContentEncoding, byte[]? Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentEncoding.FirstOrDefault(),
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct)));
            return (respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)))(request);
        }
    }
}
