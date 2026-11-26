using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Tests;

/// <summary>Identification stable des postes par machineId (MachineGuid), rétrocompatible avec les rapports sans identifiant.</summary>
public sealed class MachineIdentityTests : IDisposable
{
    private const string Id1 = "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";
    private const string Id2 = "7a2d3b9f-2e3c-4d4e-8f50-123456789abc";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-id-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private ServerStore? _store;
    private ServerStore Store => _store ??= new ServerStore(new ServerOptions { DataDirectory = _dir });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    private SaveResult Save(string name, string? machineId, DateTimeOffset at)
    {
        var f = ReportFixture.Typical(name, timestamp: at);
        if (machineId is not null) f.WithMachineId(machineId);
        var json = f.Json();
        return Store.SaveReport(ReportParser.Parse(json), json, at.AddMinutes(1));
    }

    [Theory]
    [InlineData("6F1C2A8E-1D2B-4C3D-9E4F-0123456789AB", "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab")]
    [InlineData("{6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab}", "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab")]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("pas-un-guid", null)]
    [InlineData("", null)]
    public void Machine_ids_are_normalized(string raw, string? expected) => Assert.Equal(expected, ReportParser.NormalizeMachineId(raw));

    [Fact]
    public void A_renamed_machine_stays_the_same_machine()
    {
        var first = Save("PC-COMPTA-01", Id1, T0);
        var job = Store.CreateJob(first.MachineRef, JobTypes.Scan, new JobParameters(), "x", T0).Job;

        var renamed = Save("PC-FINANCE-01", Id1, T0.AddDays(1));
        Assert.Equal(first.MachineRef, renamed.MachineRef);
        Assert.Equal("PC-COMPTA-01", renamed.RenamedFrom);
        var m = Assert.Single(Store.GetMachines());
        Assert.Equal("PC-FINANCE-01", m.Name);
        Assert.Equal(Id1, m.MachineId);
        Assert.Equal(2, Store.GetHistory("PC-FINANCE-01").Count);  // historique conservé
        Assert.Equal("PC-FINANCE-01", Store.GetJob(job.Id)!.Machine); // tâches suivies
        Assert.Null(Store.GetMachine("PC-COMPTA-01"));
    }

    [Fact]
    public void An_old_report_does_not_rename_back()
    {
        Save("PC-NEW", Id1, T0.AddDays(5));
        var old = Save("PC-OLD", Id1, T0);
        Assert.False(old.IsLatest);
        Assert.Null(old.RenamedFrom);
        Assert.Equal("PC-NEW", Assert.Single(Store.GetMachines()).Name);
    }

    [Fact]
    public void Name_already_taken_by_an_obsolete_record_is_set_aside()
    {
        var a = Save("PC-A", Id1, T0);
        var b = Save("PC-B", Id2, T0);
        Save("PC-B", Id1, T0.AddDays(1)); // PC-A est renommé PC-B (l'ancien PC-B a été remplacé)
        var machines = Store.GetMachines();
        Assert.Equal(2, machines.Count);
        Assert.Equal(a.MachineRef, machines.Single(m => m.Name == "PC-B").Id);
        Assert.Equal($"PC-B~{b.MachineRef}", machines.Single(m => m.Id == b.MachineRef).Name);
    }

    [Fact]
    public void Reports_without_id_are_backward_compatible_and_adopt_the_id_later()
    {
        var legacy = Save("PC-01", null, T0);          // poste 2.x
        Assert.Null(Store.GetMachine("PC-01")!.MachineId);
        Assert.Equal(legacy.MachineRef, Save("PC-01", null, T0.AddDays(1)).MachineRef);
        var upgraded = Save("PC-01", Id1, T0.AddDays(2)); // même poste passé en 3.0
        Assert.Equal(legacy.MachineRef, upgraded.MachineRef);
        Assert.Equal(Id1, Store.GetMachine("PC-01")!.MachineId);
        Assert.Single(Store.GetMachines());
    }

    [Fact]
    public void Reinstalled_windows_new_id_same_name_is_the_same_machine()
    {
        var before = Save("PC-01", Id1, T0);
        var after = Save("PC-01", Id2, T0.AddDays(1));
        Assert.Equal(before.MachineRef, after.MachineRef);
        Assert.Equal(Id2, Store.GetMachine("PC-01")!.MachineId);
    }

    [Fact]
    public void Previous_state_is_reported_for_alerts()
    {
        var healthy = new ReportFixture("PC-01", timestamp: T0).WithMachineId(Id1).Json();
        var first = Store.SaveReport(ReportParser.Parse(healthy), healthy, T0);
        Assert.True(first.IsNewMachine);
        Assert.Null(first.Previous);
        var second = Save("PC-01", Id1, T0.AddDays(1));
        Assert.True(second.Previous!.Healthy);
        Assert.True(second.IsLatest);
    }

    [Fact]
    public void Version_1_database_is_migrated_with_its_history()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "spokapilote.db");
        var summary = System.Text.Json.JsonSerializer.Serialize(ReportParser.Parse(ReportFixture.Typical("PC-V1", timestamp: T0).Json()), ServerStore.Json);
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // Schéma exact de la console 2.x.
            cmd.CommandText = """
                CREATE TABLE machines (name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, last_seen TEXT NOT NULL, last_report TEXT NOT NULL,
                    summary_json TEXT NOT NULL, raw_report BLOB);
                CREATE TABLE reports (id INTEGER PRIMARY KEY AUTOINCREMENT, machine TEXT NOT NULL COLLATE NOCASE REFERENCES machines(name) ON DELETE CASCADE,
                    report_at TEXT NOT NULL, received_at TEXT NOT NULL, summary_json TEXT NOT NULL);
                CREATE INDEX ix_reports_machine ON reports(machine, report_at DESC);
                CREATE TABLE documents (key TEXT NOT NULL PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO machines VALUES ('PC-V1', '2026-11-01T08:00:00.0000000Z', '2026-11-01T08:00:00.0000000Z', $s, NULL);
                INSERT INTO reports(id, machine, report_at, received_at, summary_json) VALUES (41, 'PC-V1', '2026-10-01T08:00:00.0000000Z', '2026-10-01T08:00:00.0000000Z', $s);
                INSERT INTO reports(id, machine, report_at, received_at, summary_json) VALUES (42, 'PC-V1', '2026-11-01T08:00:00.0000000Z', '2026-11-01T08:00:00.0000000Z', $s);
                INSERT INTO documents VALUES ('policy', '{"version":1}', '2026-11-01T08:00:00.0000000Z');
                PRAGMA user_version = 1;
                """;
            cmd.Parameters.AddWithValue("$s", summary);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var m = Assert.Single(Store.GetMachines());
        Assert.Equal("PC-V1", m.Name);
        Assert.Null(m.MachineId);
        Assert.Equal([42L, 41L], Store.GetHistory("PC-V1").Select(h => h.Id));   // identifiants de rapports conservés
        Assert.Equal("{\"version\":1}", Store.GetDocument(ServerStore.PolicyKey)!.Json);
        // Le poste 2.x passe en 3.0 : même poste.
        Assert.Equal(m.Id, Save("PC-V1", Id1, T0.AddDays(1)).MachineRef);
        Assert.True(Store.DeleteMachine("PC-V1"));
        Assert.Empty(Store.GetHistory("PC-V1"));
    }
}
