using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

/// <summary>Côté poste : identifiant machine, client des tâches à distance, tâche planifiée Agent.</summary>
public class AgentTests
{
    private const string MachineId = "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";

    // ------------------------------------------------------------------ identifiant machine

    [Theory]
    [InlineData("6F1C2A8E-1D2B-4C3D-9E4F-0123456789AB", MachineId)]
    [InlineData(" {6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab} ", MachineId)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("n'importe quoi", null)]
    [InlineData(null, null)]
    public void Machine_id_is_normalized(string? raw, string? expected) => Assert.Equal(expected, SystemInfoProvider.NormalizeMachineId(raw));

    [Fact]
    public void Machine_guid_is_read_from_the_registry()
    {
        // Tout Windows possède un MachineGuid, lisible sans droits administrateur.
        var id = SystemInfoProvider.ReadMachineId();
        Assert.NotNull(id);
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, id!.ToLowerInvariant());
        Assert.Equal(id, SystemInfoProvider.ReadMachineId()); // stable
    }

    [Fact]
    public void Machine_id_is_part_of_the_report_and_optional_on_reload()
    {
        var report = new ScanReport
        {
            System = new SystemInfo { ComputerName = "PC-TEST", MachineId = MachineId },
            Devices = [new DeviceInfo { InstanceId = "PCI\\X\\1", Name = "x" }],
        };
        Assert.Contains($"\"machineId\": \"{MachineId}\"", report.ToJson());
        var noId = new ScanReport { System = new SystemInfo { ComputerName = "PC-TEST" } }.ToJson();
        Assert.DoesNotContain("machineId", noId); // rapports sans identifiant : champ absent (rétrocompatible)
    }

    // ------------------------------------------------------------------ client des tâches

    [Fact]
    public async Task Pending_jobs_are_requested_with_the_machine_identity()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """
            { "known": true, "machine": "PC-TEST", "jobs": [
              { "id": 12, "type": "install-missing", "dryRun": true, "createdAt": "2026-11-26T09:00:00Z", "createdBy": "Alice" },
              { "id": 13, "type": "scan", "dryRun": false } ] }
            """));
        var client = new ServerClient("https://console.exemple.fr", "jeton-agent", handler);
        var batch = await client.GetPendingJobsAsync(MachineId, "PC-TEST");

        Assert.True(batch.Known);
        Assert.Equal(2, batch.Jobs.Count);
        Assert.Equal(new AgentJob(12, "install-missing", true, new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero), "Alice"), batch.Jobs[0]);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://console.exemple.fr/api/agent/jobs", req.Uri);
        Assert.Equal("Bearer jeton-agent", req.Authorization);
        Assert.Equal(MachineId, req.Headers[ServerClient.MachineIdHeader]);
        Assert.Equal("PC-TEST", req.Headers[ServerClient.MachineNameHeader]);
    }

    [Fact]
    public async Task Job_result_is_posted_as_json()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "{}"));
        var client = new ServerClient("https://console.exemple.fr/sp", "jeton", handler);
        await client.SendJobResultAsync(12, MachineId, "PC-TEST", new AgentJobResult(true, 3010, "Terminé, redémarrage requis", 42, "journal"));

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://console.exemple.fr/sp/api/agent/jobs/12/result", req.Uri);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body!));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3010, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(42, doc.RootElement.GetProperty("reportId").GetInt64());
        Assert.Equal("journal", doc.RootElement.GetProperty("log").GetString());
    }

    [Fact]
    public async Task Upload_returns_the_report_id_for_the_job_link()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "{\"machine\":\"PC-TEST\",\"reportId\":77,\"healthy\":false}"));
        var result = await new ServerClient("https://srv", "jeton", handler).UploadReportAsync(new ScanReport
        {
            System = new SystemInfo { ComputerName = "PC-TEST" },
            Devices = [new DeviceInfo { InstanceId = "PCI\\X\\1", Name = "x" }],
        });
        Assert.Equal(77, result.ReportId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "trop de tentatives")]
    [InlineData(HttpStatusCode.Conflict, "n'est plus en cours")]
    public async Task Job_errors_are_explained(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(_ => Respond(status, "{\"error\":\"La tâche 12 n'est plus en cours (état : expired) : résultat ignoré.\"}"));
        var client = new ServerClient("https://srv", "jeton", handler);
        var ex = await Assert.ThrowsAsync<ServerException>(() => client.SendJobResultAsync(12, MachineId, "PC", new AgentJobResult(true, 0, "ok")));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Agent_whitelist_matches_the_server_contract()
    {
        Assert.Equal(["scan", "install-missing", "install-updates", "deploy-printers", "rollback-last", "self-update"], AgentJobTypes.All);
        Assert.False(AgentJobTypes.IsAllowed("powershell"));
        Assert.False(AgentJobTypes.IsAllowed("SCAN")); // comparaison exacte
        Assert.False(AgentJobTypes.IsAllowed(null));
    }

    // ------------------------------------------------------------------ tâche planifiée Agent

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\SpokaPilote\spokapilote.exe";

    [Fact]
    public void Agent_task_repeats_every_n_minutes_as_system()
    {
        var now = new DateTime(2026, 11, 26, 9, 0, 0);
        var d = XDocument.Parse(AgentScheduler.BuildTaskXml(15, Exe, now));
        var trigger = d.Descendants(Ns + "TimeTrigger").Single();
        Assert.Equal("PT15M", trigger.Element(Ns + "Repetition")!.Element(Ns + "Interval")!.Value);
        Assert.Null(trigger.Element(Ns + "Repetition")!.Element(Ns + "Duration")); // indéfiniment
        Assert.Equal("2026-11-26T09:15:00", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal(@"\SpokaPilote\Agent", d.Descendants(Ns + "URI").Single().Value);
        Assert.Equal("S-1-5-18", d.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("IgnoreNew", d.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("false", d.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("agent run --quiet", d.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(Exe, d.Descendants(Ns + "Command").Single().Value);

        var (command, arguments, summary) = MaintenanceScheduler.ParseDefinition(AgentScheduler.BuildTaskXml(30, Exe, now));
        Assert.Equal(Exe, command);
        Assert.Equal("agent run --quiet", arguments);
        Assert.StartsWith("toutes les 30 min", summary);
    }

    [Fact]
    public void Interval_is_clamped_and_maintenance_task_is_unchanged()
    {
        var d = XDocument.Parse(AgentScheduler.BuildTaskXml(1, Exe));
        Assert.Equal("PT5M", d.Descendants(Ns + "Interval").Single().Value);
        // La maintenance garde sa définition (même socle partagé).
        var m = XDocument.Parse(MaintenanceScheduler.BuildTaskXml(new MaintenanceOptions(), Exe));
        Assert.Equal(@"\SpokaPilote\Maintenance", m.Descendants(Ns + "URI").Single().Value);
        Assert.Equal("maintenance --mode missing", m.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public async Task Enable_refuses_a_build_or_temporary_executable()
    {
        var (ok, message) = await AgentScheduler.EnableAsync(15, Path.Combine(Path.GetTempPath(), "spokapilote.exe"));
        Assert.False(ok);
        Assert.Contains("temporaire", message);
        Assert.False((await AgentScheduler.EnableAsync(2, Exe)).Ok);
    }

    // ------------------------------------------------------------------ outillage

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Captured(HttpMethod Method, string Uri, string? Authorization, Dictionary<string, string> Headers, byte[]? Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct)));
            return respond(request);
        }
    }
}
