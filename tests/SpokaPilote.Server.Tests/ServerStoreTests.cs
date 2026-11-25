using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Tests;

public sealed class ServerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-store-" + Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public ServerStoreTests() => _store = new ServerStore(new ServerOptions { DataDirectory = _dir, HistoryPerMachine = 3 });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* fichier encore verrouillé : dossier temporaire */ }
    }

    private void Save(string name, DateTimeOffset at, ReportFixture? fixture = null)
    {
        var json = (fixture ?? ReportFixture.Typical(name, timestamp: at)).Json();
        _store.SaveReport(ReportParser.Parse(json), json, at.AddMinutes(1));
    }

    [Fact]
    public void Keeps_only_the_latest_reports_per_machine()
    {
        for (var i = 0; i < 5; i++) Save("PC-01", T0.AddDays(i));
        var history = _store.GetHistory("PC-01");
        Assert.Equal(3, history.Count);
        Assert.Equal(T0.AddDays(4), history[0].Timestamp);
        Assert.Equal(T0.AddDays(2), history[^1].Timestamp);
        Assert.Equal(3, _store.GetMachine("pc-01")!.ReportCount); // recherche insensible à la casse
    }

    [Fact]
    public void An_older_report_does_not_replace_the_current_state()
    {
        Save("PC-01", T0.AddDays(5), new ReportFixture("PC-01", timestamp: T0.AddDays(5))); // sain
        Save("PC-01", T0, ReportFixture.Typical("PC-01", timestamp: T0));                    // ancien, à traiter

        var m = _store.GetMachine("PC-01")!;
        Assert.True(m.Report.Healthy);
        Assert.Equal(T0.AddDays(5), m.Report.Timestamp);
        Assert.Equal(T0.AddDays(5), m.LastSeen);
        Assert.Equal(2, _store.GetHistory("PC-01").Count);
        Assert.Contains("\"computerName\": \"PC-01\"", _store.GetRawReport("PC-01"));
    }

    [Fact]
    public void Historic_reports_and_deletion()
    {
        Save("PC-01", T0);
        Save("PC-02", T0);
        var id = _store.GetHistory("PC-01")[0].Id;
        Assert.Equal("PC-01", _store.GetHistoricReport("PC-01", id)!.Name);
        Assert.Null(_store.GetHistoricReport("PC-02", id)); // un rapport n'est lisible que via son poste

        Assert.True(_store.DeleteMachine("pc-01"));
        Assert.Null(_store.GetMachine("PC-01"));
        Assert.Empty(_store.GetHistory("PC-01"));
        Assert.Single(_store.GetMachines());
        Assert.False(_store.DeleteMachine("PC-01"));
    }

    [Fact]
    public void Documents_are_stored_verbatim()
    {
        Assert.Null(_store.GetDocument(ServerStore.PolicyKey));
        const string json = "{\n  \"version\": 3, \"name\": \"Parc\", \"rules\": [] // commentaire conservé tel quel\n}";
        _store.SetDocument(ServerStore.PolicyKey, json, T0);
        _store.SetDocument(ServerStore.PolicyKey, json, T0.AddHours(1));
        var doc = _store.GetDocument(ServerStore.PolicyKey)!;
        Assert.Equal(json, doc.Json);
        Assert.Equal(T0.AddHours(1), doc.UpdatedAt);
        Assert.Null(_store.GetDocument(ServerStore.PrinterProfileKey));
        Assert.True(_store.DeleteDocument(ServerStore.PolicyKey));
        Assert.Null(_store.GetDocument(ServerStore.PolicyKey));
    }

    [Fact]
    public void Data_survives_a_restart()
    {
        Save("PC-01", T0);
        var reopened = new ServerStore(new ServerOptions { DataDirectory = _dir, HistoryPerMachine = 3 });
        Assert.Equal("PC-01", Assert.Single(reopened.GetMachines()).Name);
    }
}
