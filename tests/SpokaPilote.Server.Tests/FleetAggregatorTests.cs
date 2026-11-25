using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Tests;

public class FleetAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static StoredMachine Machine(ReportFixture fixture, double daysAgo = 0)
    {
        var report = ReportParser.Parse(fixture.Json());
        return new StoredMachine(report.Name, Now.AddDays(-daysAgo), 1, report);
    }

    private static List<StoredMachine> Fleet() =>
    [
        Machine(ReportFixture.Typical("PC-01")),
        Machine(ReportFixture.Typical("PC-02"), daysAgo: 9),
        Machine(ReportFixture.Typical("PC-03", model: "Latitude 5440")),
        Machine(new ReportFixture("PC-04", "LENOVO", "20W0")),       // sain
        Machine(new ReportFixture("PC-05", "LENOVO", "20W0")
            .Device("Lecteur d'empreintes", "USB\\VID_27C6&PID_538C\\9", ["USB\\VID_27C6&PID_538C&REV_0100"], 28)),
    ];

    [Fact]
    public void Summary_counts_healthy_to_process_stale_and_models()
    {
        var s = FleetAggregator.Summarize(Fleet(), Now, staleDays: 7);
        Assert.Equal(5, s.MachineCount);
        Assert.Equal(1, s.Healthy);
        Assert.Equal(4, s.ToProcess);
        Assert.Equal(1, s.Stale);
        Assert.Equal("PC-02", Assert.Single(s.StaleMachines).Name);
        Assert.Equal(3, s.ModelCount);
        Assert.Equal(3, s.HardwareFaults);
        Assert.Equal(4, s.Unresolved);
        Assert.Equal("Dell Inc. Latitude 7420", s.Models[0].Model);
        Assert.Equal(2, s.Models[0].Count);
    }

    [Fact]
    public void Missing_drivers_are_aggregated_by_hardware_id_across_the_fleet_without_hardware_faults()
    {
        var missing = FleetAggregator.MissingDrivers(Fleet());

        // Le lecteur d'empreintes manque sur 4 postes ; l'audio (pilote proposé) et la panne USB n'apparaissent pas.
        var fingerprint = Assert.Single(missing);
        Assert.Equal("USB\\VID_27C6&PID_538C&REV_0100", fingerprint.HardwareId);
        Assert.Equal(4, fingerprint.Count);
        Assert.Equal(["PC-01", "PC-02", "PC-03", "PC-05"], fingerprint.Machines);
        Assert.Equal(3, fingerprint.Models.Count);
        Assert.DoesNotContain(missing, h => h.HardwareId.Contains("DESCRIPTOR_FAILURE"));
    }

    [Fact]
    public void Rows_put_machines_to_process_first()
    {
        var rows = FleetAggregator.Rows(Fleet(), Now, 7);
        Assert.Equal("PC-04", rows[^1].Name);
        Assert.All(rows.Take(4), r => Assert.False(r.Healthy));
        Assert.True(rows.Single(r => r.Name == "PC-02").Stale);
    }

    [Theory]
    [InlineData(6.9, 7, false)]
    [InlineData(7.1, 7, true)]
    [InlineData(400, 0, false)] // 0 = alerte désactivée
    public void Stale_threshold(double daysAgo, int staleDays, bool expected) =>
        Assert.Equal(expected, FleetAggregator.IsStale(Now.AddDays(-daysAgo), Now, staleDays));

    [Fact]
    public void Empty_fleet()
    {
        var s = FleetAggregator.Summarize([], Now, 7);
        Assert.Equal(0, s.MachineCount);
        Assert.Empty(s.MissingDrivers);
        Assert.Empty(s.Models);
    }
}
