using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Calcul des échéances, y compris aux changements d'heure.</summary>
public sealed class ScheduleCalculatorTests
{
    /// <summary>Fuseau de Paris (règles européennes) construit à la main : résultat identique sous Windows et Linux.</summary>
    internal static TimeZoneInfo Paris()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/Paris", TimeSpan.FromHours(1), "Paris (test)", "CET", "CEST", [rule]);
    }

    private static readonly TimeZoneInfo Zone = Paris();
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);
    private static DateTimeOffset Next(string frequency, DayOfWeek[] days, string time, DateTimeOffset after) =>
        ScheduleCalculator.NextRun(frequency, days, TimeOnly.Parse(time), after, Zone);

    [Fact]
    public void Daily_runs_today_if_still_ahead_otherwise_tomorrow()
    {
        // 26/11/2026 : hiver (UTC+1). 22:00 locale = 21:00 UTC.
        Assert.Equal(Utc(2026, 11, 26, 21, 0), Next("daily", [], "22:00", Utc(2026, 11, 26, 9, 0)));
        Assert.Equal(Utc(2026, 11, 27, 21, 0), Next("daily", [], "22:00", Utc(2026, 11, 26, 21, 0))); // strictement après
        Assert.Equal(Utc(2026, 11, 27, 21, 0), Next("daily", [], "22:00", Utc(2026, 11, 26, 21, 30)));
    }

    [Fact]
    public void Weekly_picks_the_next_selected_day()
    {
        // Jeudi 26/11/2026.
        Assert.Equal(Utc(2026, 11, 30, 20, 30), Next("weekly", [DayOfWeek.Monday], "21:30", Utc(2026, 11, 26, 9, 0)));
        Assert.Equal(Utc(2026, 11, 27, 20, 30), Next("weekly", [DayOfWeek.Monday, DayOfWeek.Friday], "21:30", Utc(2026, 11, 26, 9, 0)));
        Assert.Equal(Utc(2026, 12, 3, 20, 30), Next("weekly", [DayOfWeek.Thursday], "21:30", Utc(2026, 11, 26, 20, 30))); // une semaine plus tard
        Assert.Equal(Utc(2026, 11, 29, 6, 0), Next("weekly", [DayOfWeek.Sunday], "07:00", Utc(2026, 11, 26, 9, 0)));
    }

    [Fact]
    public void Summer_time_is_applied()
    {
        // Juillet : UTC+2.
        Assert.Equal(Utc(2026, 7, 1, 20, 0), Next("daily", [], "22:00", Utc(2026, 7, 1, 8, 0)));
    }

    [Fact]
    public void Spring_forward_gap_runs_at_the_first_existing_minute()
    {
        // 28/03/2027 : 02:00 → 03:00. 02:30 n'existe pas : exécution à 03:00 locale (01:00 UTC).
        var first = Next("daily", [], "02:30", Utc(2027, 3, 27, 23, 0));
        Assert.Equal(Utc(2027, 3, 28, 1, 0), first);
        // Le lendemain, 02:30 heure d'été = 00:30 UTC.
        Assert.Equal(Utc(2027, 3, 29, 0, 30), Next("daily", [], "02:30", first));
        // Une heure hors du trou n'est pas décalée.
        Assert.Equal(Utc(2027, 3, 28, 1, 30), Next("daily", [], "03:30", Utc(2027, 3, 27, 23, 0)));
    }

    [Fact]
    public void Fall_back_repeated_hour_runs_only_once()
    {
        // 25/10/2026 : 03:00 → 02:00. 02:30 existe deux fois : une seule exécution, à la première (00:30 UTC).
        var first = Next("daily", [], "02:30", Utc(2026, 10, 24, 22, 0));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);
        var second = Next("daily", [], "02:30", first);
        Assert.Equal(Utc(2026, 10, 26, 1, 30), second); // pas de seconde exécution le 25 à 01:30 UTC
        // Même calculé pendant la seconde occurrence de 02:30.
        Assert.Equal(Utc(2026, 10, 26, 1, 30), Next("daily", [], "02:30", Utc(2026, 10, 25, 1, 30)));
    }

    [Theory]
    [InlineData("daily", null, "22:00", null, true)]
    [InlineData("weekly", new[] { 1, 4 }, "06:15", 120, true)]
    [InlineData("weekly", new int[0], "22:00", null, false)]
    [InlineData("weekly", new[] { 8 }, "22:00", null, false)]
    [InlineData("monthly", null, "22:00", null, false)]
    [InlineData("daily", null, "25:00", null, false)]
    [InlineData("daily", null, "22:00", 5, false)]
    public void Requests_are_validated(string frequency, int[]? days, string time, int? window, bool valid)
    {
        var req = new ScheduleRequest { Name = "Test", Type = "scan", Frequency = frequency, Days = days?.ToList(), Time = time, WindowMinutes = window };
        if (valid) ScheduleCalculator.Validate(req);
        else Assert.Throws<ScheduleValidationException>(() => ScheduleCalculator.Validate(req));
    }

    [Fact]
    public void Unknown_job_types_and_unsupported_dry_runs_are_refused()
    {
        Assert.Throws<ScheduleValidationException>(() => ScheduleCalculator.Validate(new ScheduleRequest { Name = "x", Type = "powershell", Frequency = "daily", Time = "22:00" }));
        Assert.Throws<ScheduleValidationException>(() => ScheduleCalculator.Validate(new ScheduleRequest { Name = "x", Type = "scan", DryRun = true, Frequency = "daily", Time = "22:00" }));
    }

    [Fact]
    public void Descriptions_are_readable()
    {
        Assert.Equal("Chaque jour à 22:00", ScheduleCalculator.Describe("daily", [], new TimeOnly(22, 0)));
        Assert.Equal("Chaque lundi, mercredi et dimanche à 07:05",
            ScheduleCalculator.Describe("weekly", [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(7, 5)));
    }
}

/// <summary>Planificateur : création des tâches à l'échéance, sans doublon, fenêtre de maintenance.</summary>
public sealed class ScheduleRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-sched-" + Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly ScheduleRunner _runner;

    public ScheduleRunnerTests()
    {
        var options = new ServerOptions { DataDirectory = _dir };
        _store = new ServerStore(options);
        var audit = new AuditService(_store, options, _clock, NullLogger<AuditService>.Instance);
        _runner = new ScheduleRunner(_store, audit, _clock, NullLogger<ScheduleRunner>.Instance) { Zone = ScheduleCalculatorTests.Paris() };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    private long Machine(string name)
    {
        var json = ReportFixture.Typical(name).Json();
        return _store.SaveReport(ReportParser.Parse(json), json, _clock.Now).MachineRef;
    }

    private long Schedule(string type = "scan", long? group = null, int? window = null, bool enabled = true, bool dryRun = false)
    {
        var s = new ScheduleRecord(0, "Analyse du soir", type, dryRun, group, ScheduleFrequencies.Daily, [], new TimeOnly(22, 0), window, enabled,
            _clock.Now, "test", _clock.Now, null, null, null);
        var id = _store.CreateSchedule(s);
        _store.RecordScheduleRun(id, null, null, enabled ? _runner.NextRun(s, _clock.Now) : null);
        return id;
    }

    private List<JobRecord> Pending() => _store.ListJobs(JobStates.Active.Take(1).ToArray());

    [Fact]
    public void Jobs_are_created_at_the_due_time_only()
    {
        Machine("PC-01");
        Machine("PC-02");
        var id = Schedule();
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 21, 0, 0, TimeSpan.Zero), _store.GetSchedule(id)!.NextRun);

        _clock.Now = new DateTimeOffset(2026, 11, 26, 20, 59, 0, TimeSpan.Zero);
        Assert.Equal(0, _runner.RunDue(_clock.Now));
        Assert.Empty(Pending());

        _clock.Now = new DateTimeOffset(2026, 11, 26, 21, 0, 20, TimeSpan.Zero);
        Assert.Equal(1, _runner.RunDue(_clock.Now));
        var jobs = Pending();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(id, j.ScheduleId));
        Assert.All(jobs, j => Assert.Equal("Planification « Analyse du soir »", j.CreatedBy));
        var s = _store.GetSchedule(id)!;
        Assert.Equal(_clock.Now, s.LastRun);
        Assert.Equal("2 tâches créées.", s.LastResult);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero), s.NextRun);

        // Passage suivant du planificateur dans la même minute : rien de plus.
        Assert.Equal(0, _runner.RunDue(_clock.Now.AddSeconds(30)));
        Assert.Equal(2, Pending().Count);
        Assert.Contains(_store.GetAudit("schedule"), e => e.Action == AuditActions.ScheduleRun && e.Actor == AuditService.SchedulerActor);
    }

    [Fact]
    public void No_duplicate_when_a_job_of_the_same_type_is_still_pending()
    {
        var pc1 = Machine("PC-01");
        Machine("PC-02");
        // Une installation en simulation déjà demandée à la main sur PC-01 (paramètres différents) : même type, donc pas de doublon.
        _store.CreateJob(pc1, JobTypes.InstallMissing, new JobParameters(DryRun: true), "technicien", _clock.Now);
        var id = Schedule(JobTypes.InstallMissing);

        _clock.Now = new DateTimeOffset(2026, 11, 26, 21, 0, 0, TimeSpan.Zero);
        _runner.RunDue(_clock.Now);
        Assert.Equal(2, Pending().Count); // PC-01 : la tâche existante ; PC-02 : une nouvelle
        Assert.Contains("1 poste ignoré", _store.GetSchedule(id)!.LastResult);

        // Le lendemain, les tâches de la veille sont toujours en attente (postes éteints) : toujours pas de doublon.
        _clock.Now = new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);
        _runner.RunDue(_clock.Now);
        Assert.Equal(2, Pending().Count);
        Assert.Equal("0 tâches créées, 2 postes ignorés (tâche identique déjà en attente).", _store.GetSchedule(id)!.LastResult);

        // Une fois les tâches récupérées par les postes, la planification suivante en recrée.
        _store.ClaimPendingJobs(pc1, _clock.Now);
        _clock.Now = new DateTimeOffset(2026, 11, 28, 21, 0, 0, TimeSpan.Zero);
        _runner.RunDue(_clock.Now);
        Assert.Equal("1 tâche créée, 1 poste ignoré (tâche identique déjà en attente).", _store.GetSchedule(id)!.LastResult);
    }

    [Fact]
    public void Group_target_uses_current_membership()
    {
        Machine("PC-COMPTA-01");
        Machine("PC-RH-01");
        var groupId = _store.CreateGroup("Compta", null, GroupMatcher.Normalize(new GroupRules
        {
            Conditions = [new GroupCondition { Field = "name", Operator = "startsWith", Value = "PC-COMPTA" }],
        }), _clock.Now)!.Value;
        Schedule(group: groupId);
        Machine("PC-COMPTA-02"); // arrivé après la création de la planification
        _clock.Now = new DateTimeOffset(2026, 11, 26, 21, 0, 0, TimeSpan.Zero);
        _runner.RunDue(_clock.Now);
        Assert.Equal(["PC-COMPTA-01", "PC-COMPTA-02"], Pending().Select(j => j.Machine).Order());
    }

    [Fact]
    public void Maintenance_window_limits_when_jobs_can_be_picked_up()
    {
        var pc1 = Machine("PC-01");
        var pc2 = Machine("PC-02");
        Schedule(JobTypes.InstallUpdates, window: 120);
        _clock.Now = new DateTimeOffset(2026, 11, 26, 21, 0, 10, TimeSpan.Zero);
        _runner.RunDue(_clock.Now);
        var windowEnd = new DateTimeOffset(2026, 11, 26, 23, 0, 0, TimeSpan.Zero);
        Assert.All(Pending(), j => Assert.Equal(windowEnd, j.NotAfter));

        // PC-01 interroge pendant la fenêtre : il reçoit sa tâche.
        Assert.Single(_store.ClaimPendingJobs(pc1, new DateTimeOffset(2026, 11, 26, 22, 30, 0, TimeSpan.Zero)));
        // PC-02 se réveille après la fin de la fenêtre : rien n'est remis, la tâche expire.
        var late = new DateTimeOffset(2026, 11, 27, 7, 30, 0, TimeSpan.Zero);
        Assert.Empty(_store.ClaimPendingJobs(pc2, late));
        var expired = Assert.Single(_store.ExpireJobs(late, 7, 6), j => j.MachineRef == pc2);
        Assert.Equal(JobStates.Expired, expired.State);
        Assert.Contains("Fenêtre de maintenance", expired.Message);
    }

    [Fact]
    public void Missed_runs_are_caught_up_once_unless_their_window_is_over()
    {
        Machine("PC-01");
        var withWindow = Schedule(JobTypes.InstallMissing, window: 60);
        var withoutWindow = Schedule(JobTypes.Scan);

        // Console arrêtée de 21:00 à 23:30 UTC le 26 : la fenêtre (21:00-22:00) est passée, l'analyse est rattrapée une fois.
        _clock.Now = new DateTimeOffset(2026, 11, 26, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(2, _runner.RunDue(_clock.Now));
        Assert.Contains("manquée", _store.GetSchedule(withWindow)!.LastResult);
        Assert.Equal("1 tâche créée.", _store.GetSchedule(withoutWindow)!.LastResult);
        Assert.Equal(JobTypes.Scan, Assert.Single(Pending()).Type);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero), _store.GetSchedule(withWindow)!.NextRun);
    }

    [Fact]
    public void Disabled_schedules_never_run()
    {
        Machine("PC-01");
        Schedule(enabled: false);
        _clock.Now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, _runner.RunDue(_clock.Now));
        Assert.Empty(Pending());
    }
}

/// <summary>Planifications par l'API.</summary>
public sealed class ScheduleApiTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Create_update_disable_and_delete()
    {
        var admin = _host.Client(AdminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/schedules", Json(new { name = "x", type = "script", frequency = "daily", time = "22:00" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/schedules", Json(new { name = "x", type = "scan", groupId = 42, frequency = "daily", time = "22:00" }))).StatusCode);

        var created = await admin.PostAsync("/api/schedules", Json(new { name = "Mises à jour", type = "install-updates", dryRun = true, frequency = "weekly", days = new[] { 2, 4 }, time = "21:30", windowMinutes = 90 }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var s = await ReadJson(created);
        var id = s.GetProperty("id").GetInt64();
        Assert.Equal("Chaque mardi et jeudi à 21:30", s.GetProperty("description").GetString());
        Assert.True(s.GetProperty("nextRun").GetDateTimeOffset() > _host.Clock.Now);
        Assert.Equal(Principal_AdminActor, s.GetProperty("createdBy").GetString());

        var disabled = await ReadJson(await admin.PutAsync($"/api/schedules/{id}", Json(new { name = "Mises à jour", type = "install-updates", frequency = "daily", time = "21:30", enabled = false })));
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, disabled.GetProperty("nextRun").ValueKind);

        var list = await ReadJson(await admin.GetAsync("/api/schedules"));
        Assert.Equal(1, list.GetProperty("schedules").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(list.GetProperty("timeZone").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/schedules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/schedules/{id}")).StatusCode);
    }

    private const string Principal_AdminActor = SpokaPilote.Server.Security.Principal.AdminTokenActor;
}
