using System.Net;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>File de tâches à distance : stockage (cycle de vie, expiration) et API (liste blanche, isolement des postes).</summary>
public sealed class JobStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    public JobStoreTests() => _store = new ServerStore(new ServerOptions { DataDirectory = _dir });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    private long Machine(string name, string? machineId = null)
    {
        var fixture = ReportFixture.Typical(name, timestamp: T0);
        if (machineId is not null) fixture.WithMachineId(machineId);
        var json = fixture.Json();
        return _store.SaveReport(ReportParser.Parse(json), json, T0).MachineRef;
    }

    [Fact]
    public void Lifecycle_pending_running_succeeded()
    {
        var pc = Machine("PC-01", "11111111-1111-1111-1111-111111111111");
        var (job, created) = _store.CreateJob(pc, JobTypes.Scan, new JobParameters(), "Alice", T0);
        Assert.True(created);
        Assert.Equal(JobStates.Pending, job.State);
        Assert.Equal("PC-01", job.Machine);

        var claimed = Assert.Single(_store.ClaimPendingJobs(pc, T0.AddMinutes(15)));
        Assert.Equal(JobStates.Running, claimed.State);
        Assert.Equal(T0.AddMinutes(15), claimed.StartedAt);
        Assert.Empty(_store.ClaimPendingJobs(pc, T0.AddMinutes(16))); // jamais remise deux fois

        var (status, done) = _store.CompleteJob(job.Id, pc, true, 0, "Analyse terminée", null, "journal", T0.AddMinutes(18));
        Assert.Equal(JobCompletion.Completed, status);
        Assert.Equal(JobStates.Succeeded, done!.State);
        Assert.Equal("journal", _store.GetJob(job.Id)!.Log);

        // Un second résultat est refusé (la tâche n'est plus en cours).
        Assert.Equal(JobCompletion.NotRunning, _store.CompleteJob(job.Id, pc, false, 1, "x", null, null, T0.AddMinutes(19)).Status);
    }

    [Fact]
    public void Identical_pending_job_is_not_duplicated_and_cancel_only_pending()
    {
        var pc = Machine("PC-01");
        var first = _store.CreateJob(pc, JobTypes.InstallMissing, new JobParameters(DryRun: true), "a", T0).Job;
        var (again, created) = _store.CreateJob(pc, JobTypes.InstallMissing, new JobParameters(DryRun: true), "b", T0);
        Assert.False(created);
        Assert.Equal(first.Id, again.Id);
        Assert.True(_store.CreateJob(pc, JobTypes.InstallMissing, new JobParameters(DryRun: false), "c", T0).Created); // autres paramètres

        Assert.True(_store.CancelJob(first.Id, T0, "admin"));
        Assert.Equal(JobStates.Cancelled, _store.GetJob(first.Id)!.State);
        Assert.False(_store.CancelJob(first.Id, T0, "admin"));
    }

    [Fact]
    public void A_machine_cannot_complete_another_machines_job()
    {
        var a = Machine("PC-A");
        var b = Machine("PC-B");
        var job = _store.CreateJob(a, JobTypes.Scan, new JobParameters(), "x", T0).Job;
        Assert.Empty(_store.ClaimPendingJobs(b, T0)); // B ne voit pas la tâche de A
        _store.ClaimPendingJobs(a, T0);
        Assert.Equal(JobCompletion.NotFound, _store.CompleteJob(job.Id, b, true, 0, "faux", null, null, T0).Status);
        Assert.Equal(JobStates.Running, _store.GetJob(job.Id)!.State);
    }

    [Fact]
    public void Unclaimed_and_stuck_jobs_expire()
    {
        var pc = Machine("PC-01");
        var pending = _store.CreateJob(pc, JobTypes.Scan, new JobParameters(), "x", T0).Job;
        var running = _store.CreateJob(pc, JobTypes.InstallUpdates, new JobParameters(), "x", T0).Job;
        _store.ClaimPendingJobs(pc, T0); // les deux passent en cours...
        var fresh = _store.CreateJob(pc, JobTypes.Scan, new JobParameters(), "x", T0.AddDays(1)).Job;

        // 7 h plus tard : les tâches en cours sans résultat (délai 6 h) expirent, pas la tâche en attente récente.
        var expired = _store.ExpireJobs(T0.AddHours(7), pendingDays: 7, runningHours: 6);
        Assert.Equal(new[] { pending.Id, running.Id }.Order(), expired.Select(j => j.Id).Order());
        Assert.All(expired, j => Assert.Contains("Aucun résultat", j.Message));

        // 8 jours après sa création, la tâche jamais récupérée expire aussi.
        var late = Assert.Single(_store.ExpireJobs(T0.AddDays(9), 7, 6));
        Assert.Equal(fresh.Id, late.Id);
        Assert.Equal(JobStates.Expired, late.State);
        Assert.Contains("agent enable", late.Message);

        Assert.Equal(3, _store.PurgeFinishedJobs(T0.AddDays(200)));
    }

    [Fact]
    public void Deleting_a_machine_deletes_its_jobs()
    {
        var pc = Machine("PC-01");
        _store.CreateJob(pc, JobTypes.Scan, new JobParameters(), "x", T0);
        Assert.True(_store.DeleteMachine("PC-01"));
        Assert.Empty(_store.ListJobs());
    }
}

public sealed class JobApiTests : IDisposable
{
    private const string IdA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string IdB = "bbbbbbbb-0000-4000-8000-00000000000b";
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task Enroll(string name, string machineId) =>
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports",
            Json(ReportFixture.Typical(name).WithMachineId(machineId).Json()))).StatusCode);

    [Fact]
    public async Task Only_whitelisted_types_are_accepted()
    {
        await Enroll("PC-A", IdA);
        var admin = _host.Client(AdminToken);
        foreach (var bad in new[] { "powershell", "cmd", "scan; rm -rf /", "", "SCRIPT" })
        {
            var r = await admin.PostAsync("/api/jobs", Json(new { type = bad, machines = new[] { "PC-A" } }));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("Types autorisés", (await ReadJson(r)).GetProperty("error").GetString());
        }
        // Une analyse ne modifie rien : pas de simulation.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/jobs", Json(new { type = "scan", dryRun = true, machines = new[] { "PC-A" } }))).StatusCode);
        var types = await ReadJson(await admin.GetAsync("/api/job-types"));
        Assert.Equal(6, types.GetArrayLength());
        // Le jeton d'enrôlement ne peut pas créer de tâche.
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(AgentToken).PostAsync("/api/jobs", Json(new { type = "scan", machines = new[] { "PC-A" } }))).StatusCode);
    }

    [Fact]
    public async Task Full_cycle_through_the_api_and_isolation_between_machines()
    {
        await Enroll("PC-A", IdA);
        await Enroll("PC-B", IdB);
        var admin = _host.Client(AdminToken);

        var create = await ReadJson(await admin.PostAsync("/api/jobs",
            Json(new { type = "install-missing", dryRun = true, machines = new[] { "PC-A", "INCONNU" }, requestedBy = "Bob" })));
        var jobId = Assert.Single(create.GetProperty("created").EnumerateArray()).GetProperty("id").GetInt64();
        Assert.Contains("inconnu", Assert.Single(create.GetProperty("skipped").EnumerateArray()).GetProperty("reason").GetString());

        // PC-B ne voit rien ; l'identifiant machine est obligatoire ; l'administrateur n'utilise pas l'API agent.
        var b = await ReadJson(await _host.Agent(IdB, "PC-B").GetAsync("/api/agent/jobs"));
        Assert.Equal(0, b.GetProperty("jobs").GetArrayLength());
        var noId = _host.Client(AgentToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await noId.GetAsync("/api/agent/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/agent/jobs")).StatusCode);

        // Un poste inconnu ne reçoit rien (aucun rattachement sans rapport).
        var unknown = await ReadJson(await _host.Agent("cccccccc-0000-4000-8000-00000000000c", "PC-C").GetAsync("/api/agent/jobs"));
        Assert.False(unknown.GetProperty("known").GetBoolean());

        var a = await ReadJson(await _host.Agent(IdA, "PC-A").GetAsync("/api/agent/jobs"));
        var job = Assert.Single(a.GetProperty("jobs").EnumerateArray());
        Assert.Equal("install-missing", job.GetProperty("type").GetString());
        Assert.True(job.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, (await ReadJson(await _host.Agent(IdA, "PC-A").GetAsync("/api/agent/jobs"))).GetProperty("jobs").GetArrayLength());

        // PC-B ne peut pas envoyer le résultat de la tâche de PC-A.
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Agent(IdB, "PC-B").PostAsync($"/api/agent/jobs/{jobId}/result",
            Json(new { success = true, exitCode = 0, message = "faux" }))).StatusCode);

        // Rapport produit par la tâche, puis résultat.
        var upload = await ReadJson(await _host.Client(AgentToken).PostAsync("/api/reports", Json(ReportFixture.Typical("PC-A").WithMachineId(IdA).Json())));
        var reportId = upload.GetProperty("reportId").GetInt64();
        var result = await _host.Agent(IdA, "PC-A").PostAsync($"/api/agent/jobs/{jobId}/result",
            Json(new { success = true, exitCode = 0, message = "Simulation terminée", reportId, log = "sortie console" }));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        var detail = await ReadJson(await admin.GetAsync($"/api/jobs/{jobId}"));
        Assert.Equal("succeeded", detail.GetProperty("state").GetString());
        Assert.Equal(reportId, detail.GetProperty("reportId").GetInt64());
        Assert.Equal("sortie console", detail.GetProperty("log").GetString());
        Assert.Equal("Bob", detail.GetProperty("createdBy").GetString());

        var machine = await ReadJson(await admin.GetAsync("/api/machines/PC-A"));
        Assert.Equal(1, machine.GetProperty("jobs").GetArrayLength());
        Assert.True(machine.GetProperty("machine").TryGetProperty("agentSeen", out var seen) && seen.ValueKind == System.Text.Json.JsonValueKind.String);
        Assert.Equal(1, (await ReadJson(await admin.GetAsync("/api/jobs?state=succeeded"))).GetArrayLength());
        Assert.Equal(0, (await ReadJson(await admin.GetAsync("/api/jobs?state=active"))).GetArrayLength());
    }

    [Fact]
    public async Task To_process_target_and_cancellation()
    {
        await Enroll("PC-A", IdA);   // à traiter
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports",
            Json(new ReportFixture("PC-SAIN").WithMachineId(IdB).Json()))).StatusCode);
        var admin = _host.Client(AdminToken);
        var res = await ReadJson(await admin.PostAsync("/api/jobs", Json(new { type = "scan", target = "to-process" })));
        var created = Assert.Single(res.GetProperty("created").EnumerateArray());
        Assert.Equal("PC-A", created.GetProperty("machine").GetString());

        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/jobs/{id}/cancel", null)).StatusCode);
        Assert.Empty((await ReadJson(await _host.Agent(IdA, "PC-A").GetAsync("/api/agent/jobs"))).GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Pending_jobs_expire_after_seven_days()
    {
        await Enroll("PC-A", IdA);
        var admin = _host.Client(AdminToken);
        await admin.PostAsync("/api/jobs", Json(new { type = "scan", machines = new[] { "PC-A" } }));
        _host.Clock.Advance(TimeSpan.FromDays(8));
        var jobs = await ReadJson(await admin.GetAsync("/api/jobs"));
        Assert.Equal("expired", jobs[0].GetProperty("state").GetString());
        Assert.Empty((await ReadJson(await _host.Agent(IdA, "PC-A").GetAsync("/api/agent/jobs"))).GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Legacy_machine_without_id_is_adopted_by_its_agent()
    {
        // Rapport 2.x : sans identifiant machine.
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(AgentToken).PostAsync("/api/reports", Json(ReportFixture.Typical("PC-OLD").Json()))).StatusCode);
        var admin = _host.Client(AdminToken);
        await admin.PostAsync("/api/jobs", Json(new { type = "scan", machines = new[] { "PC-OLD" } }));
        var r = await ReadJson(await _host.Agent(IdA, "PC-OLD").GetAsync("/api/agent/jobs"));
        Assert.Single(r.GetProperty("jobs").EnumerateArray());
        Assert.Equal(IdA, (await ReadJson(await admin.GetAsync("/api/machines/PC-OLD"))).GetProperty("machine").GetProperty("machineId").GetString());
        // Une fois rattaché, un autre identifiant qui se présente sous ce nom n'obtient rien.
        await admin.PostAsync("/api/jobs", Json(new { type = "scan", machines = new[] { "PC-OLD" } }));
        Assert.False((await ReadJson(await _host.Agent(IdB, "PC-OLD").GetAsync("/api/agent/jobs"))).GetProperty("known").GetBoolean());
    }
}
