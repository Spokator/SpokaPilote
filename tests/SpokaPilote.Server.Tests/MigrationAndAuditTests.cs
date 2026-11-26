using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Migration automatique d'une base de la console 3.0 (schéma v2) vers la 3.1 (schéma v3).</summary>
public sealed class MigrationFrom30Tests : IDisposable
{
    private const string MachineId = "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-mig-" + Guid.NewGuid().ToString("N"));

    /// <summary>Schéma exact de la console 3.0 (copie figée : la base de test doit rester celle d'un serveur 3.0).</summary>
    private const string Schema30 = """
        CREATE TABLE machines (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, machine_id TEXT UNIQUE,
            last_seen TEXT NOT NULL, last_report TEXT NOT NULL, summary_json TEXT NOT NULL, raw_report BLOB, agent_seen TEXT);
        CREATE TABLE reports (
            id INTEGER PRIMARY KEY AUTOINCREMENT, machine_ref INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            report_at TEXT NOT NULL, received_at TEXT NOT NULL, summary_json TEXT NOT NULL);
        CREATE INDEX ix_reports_machine ON reports(machine_ref, report_at DESC);
        CREATE TABLE documents (key TEXT NOT NULL PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE jobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT, machine_ref INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            type TEXT NOT NULL, parameters TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT NOT NULL, state TEXT NOT NULL,
            started_at TEXT, finished_at TEXT, message TEXT, exit_code INTEGER, report_id INTEGER, log TEXT);
        CREATE INDEX ix_jobs_machine ON jobs(machine_ref, state);
        CREATE INDEX ix_jobs_state ON jobs(state, created_at);
        CREATE TABLE alert_log (subject TEXT NOT NULL, reason TEXT NOT NULL, last_sent TEXT NOT NULL, PRIMARY KEY (subject, reason));
        CREATE TABLE alert_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT NOT NULL, machine TEXT, reason TEXT NOT NULL, title TEXT NOT NULL,
            status TEXT NOT NULL, detail TEXT);
        PRAGMA user_version = 2;
        """;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    /// <summary>Base 3.0 avec un poste, deux rapports, une tâche en attente, une tâche terminée, la politique et une alerte.</summary>
    private void Create30Database()
    {
        Directory.CreateDirectory(_dir);
        var summary = System.Text.Json.JsonSerializer.Serialize(ReportParser.Parse(ReportFixture.Typical("PC-COMPTA-01").WithMachineId(MachineId).Json()),
            ServerStore.Json);
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spokapilote.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema30 + """
            INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json, agent_seen)
                VALUES('PC-COMPTA-01', $mid, '2026-11-20T08:00:00.0000000Z', '2026-11-20T08:00:00.0000000Z', $s, '2026-11-25T08:00:00.0000000Z');
            INSERT INTO reports(machine_ref, report_at, received_at, summary_json) VALUES(1, '2026-11-19T08:00:00.0000000Z', '2026-11-19T08:01:00.0000000Z', $s);
            INSERT INTO reports(machine_ref, report_at, received_at, summary_json) VALUES(1, '2026-11-20T08:00:00.0000000Z', '2026-11-20T08:01:00.0000000Z', $s);
            INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state)
                VALUES(1, 'scan', '{"dryRun":false}', '2026-11-25T09:00:00.0000000Z', 'Jean', 'pending');
            INSERT INTO jobs(machine_ref, type, parameters, created_at, created_by, state, started_at, finished_at, message, exit_code, log)
                VALUES(1, 'install-missing', '{"dryRun":true}', '2026-11-24T09:00:00.0000000Z', 'Jean', 'succeeded',
                       '2026-11-24T09:10:00.0000000Z', '2026-11-24T09:12:00.0000000Z', 'OK', 0, 'journal');
            INSERT INTO documents(key, json, updated_at) VALUES('policy', '{"version":1,"rules":[]}', '2026-11-01T00:00:00.0000000Z');
            INSERT INTO alert_events(created_at, machine, reason, title, status) VALUES('2026-11-20T08:02:00.0000000Z', 'PC-COMPTA-01', 'to-process', 'Poste à traiter', 'envoyée');
            """;
        cmd.Parameters.AddWithValue("$mid", MachineId);
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.ExecuteNonQuery();
    }

    private int UserVersion()
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spokapilote.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void Existing_data_is_preserved_and_new_features_work()
    {
        Create30Database();
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        Assert.Equal(ServerStore.SchemaVersion, UserVersion());

        var m = Assert.Single(store.GetMachines());
        Assert.Equal("PC-COMPTA-01", m.Name);
        Assert.Equal(MachineId, m.MachineId);
        Assert.NotNull(m.AgentSeen);
        Assert.Null(m.LastIp);
        Assert.Equal(2, store.GetHistory("PC-COMPTA-01").Count);
        Assert.Equal("{\"version\":1,\"rules\":[]}", store.GetDocument(ServerStore.PolicyKey)!.Json);
        Assert.Single(store.GetAlertEvents());
        var jobs = store.ListJobs();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Null(j.NotAfter));
        Assert.Equal("journal", store.GetJob(jobs.Single(j => j.State == JobStates.Succeeded).Id)!.Log);

        // Un agent 3.0 récupère sa tâche en attente comme avant.
        var agent = store.FindMachineForAgent(MachineId, "PC-COMPTA-01", DateTimeOffset.UtcNow)!;
        Assert.Equal(JobTypes.Scan, Assert.Single(store.ClaimPendingJobs(agent.Id, DateTimeOffset.UtcNow)).Type);

        // Nouvelles tables utilisables.
        var now = DateTimeOffset.UtcNow;
        var g = store.CreateGroup("Compta", null, new GroupRules(), now)!.Value;
        store.ChangeGroupMembers(g, [m.Id], [], now);
        Assert.Contains(m.Id, store.GetGroup(g)!.ManualMembers);
        Assert.NotNull(store.CreateUser("chef", null, Roles.Admin, PasswordHasher.Hash("Mot-De-Passe-Chef-1"), true, now));
        store.AddAudit(now, "chef", Roles.Admin, AuditActions.LoginSuccess, null, null, null, true, 100, 30);
        Assert.Equal(1, store.CountAudit());

        // Réouverture : la migration n'est pas rejouée.
        SqliteConnection.ClearAllPools();
        var again = new ServerStore(new ServerOptions { DataDirectory = _dir });
        Assert.Single(again.GetGroups());
        Assert.Single(again.GetUsers());
    }

    [Fact]
    public async Task A_30_server_volume_upgrades_in_place_and_30_agents_keep_working()
    {
        // Le volume de données de la 3.0 est déjà là quand le serveur 3.1 démarre (au premier client créé).
        Create30Database();
        SqliteConnection.ClearAllPools();
        using var host = new TestServerHost();
        Directory.CreateDirectory(host.DataDir);
        File.Copy(Path.Combine(_dir, "spokapilote.db"), Path.Combine(host.DataDir, "spokapilote.db"));

        var admin = host.Client(AdminToken);
        var machines = await ReadJson(await admin.GetAsync("/api/machines"));
        Assert.Equal("PC-COMPTA-01", Assert.Single(machines.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(2, (await ReadJson(await admin.GetAsync("/api/jobs"))).GetArrayLength());

        var agent = host.Agent(MachineId, "PC-COMPTA-01");
        var poll = await ReadJson(await agent.GetAsync("/api/agent/jobs"));
        Assert.True(poll.GetProperty("known").GetBoolean());
        Assert.Single(poll.GetProperty("jobs").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/api/reports", Json(ReportFixture.Typical("PC-COMPTA-01").WithMachineId(MachineId).Json()))).StatusCode);
        Assert.Equal(3, (await ReadJson(await admin.GetAsync("/api/machines/PC-COMPTA-01"))).GetProperty("history").GetArrayLength());
    }
}

/// <summary>Journal d'audit : consignation, rétention, export CSV.</summary>
public sealed class AuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-audit-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    [Fact]
    public void Retention_keeps_the_most_recent_entries_up_to_the_limit()
    {
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        for (var i = 0; i < 150; i++)
            store.AddAudit(T0.AddMinutes(i), "chef", Roles.Admin, AuditActions.JobCreate, $"cible {i}", null, null, true, maxEntries: 100, maxDays: 180);
        Assert.Equal(100, store.CountAudit());
        var entries = store.GetAudit(limit: 1000);
        Assert.Equal("cible 149", entries[0].Target);
        Assert.Equal("cible 50", entries[^1].Target);
    }

    [Fact]
    public void Retention_removes_entries_older_than_the_limit_in_days()
    {
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        store.AddAudit(T0, "chef", null, AuditActions.LoginSuccess, null, null, null, true, 5000, 30);
        store.AddAudit(T0.AddDays(20), "chef", null, AuditActions.LoginSuccess, null, null, null, true, 5000, 30);
        store.AddAudit(T0.AddDays(31), "chef", null, AuditActions.LoginSuccess, null, null, null, true, 5000, 30); // purge T0
        Assert.Equal(2, store.CountAudit());
        Assert.Equal(1, store.PurgeAudit(T0.AddDays(55), 5000, 30)); // purge périodique, sans nouvelle entrée
        Assert.Equal(1, store.CountAudit());
    }

    [Fact]
    public void Filters_by_category_actor_and_text()
    {
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        void Add(string actor, string action, string? target) => store.AddAudit(T0, actor, null, action, target, null, "10.0.0.1", true, 5000, 180);
        Add("chef", AuditActions.LoginSuccess, null);
        Add("tech", AuditActions.GroupCreate, "Siège_100%");
        Add("tech", AuditActions.JobCreate, "PC-01");
        Assert.Single(store.GetAudit("login"));
        Assert.Equal(2, store.GetAudit(actor: "tech").Count);
        Assert.Single(store.GetAudit(search: "_100%")); // caractères spéciaux de LIKE neutralisés
        Assert.Empty(store.GetAudit(search: "100_%x"));
        Assert.Empty(store.GetAudit("log")); // catégorie exacte, pas un préfixe libre
    }

    [Fact]
    public async Task Api_records_who_did_what_and_exports_csv()
    {
        using var host = new TestServerHost(b => b.UseSetting("SPOKAPILOTE_AUDIT_MAX_ENTRIES", "100"));
        await host.CreateUserAsync("tech", Roles.Technician);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.TryLoginAsync("tech", "mauvais-mot-de-passe", ip: "192.0.2.7")).StatusCode);
        var tech = await host.LoginAsync("tech");
        Assert.Equal(HttpStatusCode.Created, (await tech.PostAsync("/api/groups", Json(new { name = "=HYPERLINK(\"x\")" }))).StatusCode);
        var admin = host.Client(AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/policy", Json("{\"version\":2}"))).StatusCode);

        var audit = await ReadJson(await admin.GetAsync("/api/audit"));
        var entries = audit.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(100, audit.GetProperty("retention").GetProperty("maxEntries").GetInt32());
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == AuditActions.AccountCreate && e.GetProperty("actor").GetString() == Principal.AdminTokenActor);
        var failure = entries.Single(e => e.GetProperty("action").GetString() == AuditActions.LoginFailure);
        Assert.False(failure.GetProperty("success").GetBoolean());
        Assert.Equal("tech", failure.GetProperty("actor").GetString());
        Assert.Equal("192.0.2.7", failure.GetProperty("ip").GetString());
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == AuditActions.LoginSuccess && e.GetProperty("actor").GetString() == "tech");
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == AuditActions.GroupCreate && e.GetProperty("role").GetString() == Roles.Technician);
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == AuditActions.PolicyUpdate);
        Assert.Single((await ReadJson(await admin.GetAsync("/api/audit?category=group"))).GetProperty("entries").EnumerateArray());

        var csv = await admin.GetAsync("/api/audit/export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Date (UTC);Compte;Rôle;Action;Code;Cible;Détail;Adresse IP;Résultat\r\n", text);
        Assert.Contains("\"Groupe créé\";\"group.create\";\"'=HYPERLINK(\"\"x\"\")\"", text); // formule neutralisée
        // L'export lui-même est consigné.
        Assert.Contains((await ReadJson(await admin.GetAsync("/api/audit"))).GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("action").GetString() == AuditActions.AuditExport);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/audit/export")).StatusCode);
    }
}
