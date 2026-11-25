using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Tests;

public class ReportParserTests
{
    [Fact]
    public void Parses_system_and_classifies_problem_devices()
    {
        var r = ReportParser.Parse(ReportFixture.Typical().Json());

        Assert.Equal("PC-COMPTA-01", r.Name);
        Assert.Equal("Dell Inc. Latitude 7420", r.System.DisplayModel);
        Assert.Equal("24H2", r.System.OsDisplayVersion);
        Assert.Equal("10.0.26100", r.System.OsVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 30, 0, TimeSpan.Zero), r.Timestamp.ToUniversalTime());
        Assert.Equal(4, r.DeviceCount);
        Assert.Equal(3, r.ProblemDevices.Count);

        // Panne USB : comptée à part, jamais comme pilote manquant.
        var usb = Assert.Single(r.ProblemDevices, d => d.IsHardwareFault);
        Assert.Contains("DESCRIPTOR_FAILURE", usb.AggregationKey);
        Assert.NotNull(usb.Hint);
        Assert.Equal(2, r.DriverProblems);
        Assert.Equal(1, r.HardwareFaults);

        // L'audio a un pilote proposé ; le lecteur d'empreintes n'en a pas.
        Assert.True(r.ProblemDevices.Single(d => d.Name == "Contrôleur audio").HasCandidate);
        Assert.Equal(1, r.Unresolved);
        Assert.Equal(1, r.Proposed);
        Assert.True(r.AnalysisOnly);
        Assert.False(r.Healthy);
    }

    [Theory]
    [InlineData("USB\\DEVICE_DESCRIPTOR_FAILURE")]
    [InlineData("USB\\RESET_FAILURE")]
    [InlineData("USB\\SET_ADDRESS_FAILURE")]
    [InlineData("USB\\CONFIGURATION_DESCRIPTOR_VALIDATION_FAILURE")]
    [InlineData("usb\\device_descriptor_failure")]
    public void Usb_failures_are_hardware_faults(string hardwareId) =>
        Assert.True(ReportParser.IsHardwareFault([hardwareId]));

    [Fact]
    public void Ordinary_ids_are_not_hardware_faults() =>
        Assert.False(ReportParser.IsHardwareFault(["PCI\\VEN_8086&DEV_43EF", "USB\\VID_046D&PID_C52B"]));

    [Fact]
    public void Disabled_and_absent_devices_are_ignored()
    {
        var json = new ReportFixture()
            .Device("Bluetooth désactivé", "USB\\VID_8087\\1", ["USB\\VID_8087&PID_0029"], 22)
            .Device("Clé débranchée", "USB\\VID_0781\\2", ["USB\\VID_0781&PID_5581"], 28, present: false)
            .Json();
        var r = ReportParser.Parse(json);
        Assert.Equal(2, r.DeviceCount); // carte réseau + Bluetooth (présents)
        Assert.Empty(r.ProblemDevices);
        Assert.True(r.Healthy);
    }

    [Fact]
    public void Blocked_candidates_do_not_resolve_devices()
    {
        var json = new ReportFixture()
            .Device("Carte graphique", "PCI\\VEN_10DE\\1", ["PCI\\VEN_10DE&DEV_2504"], 28)
            .Candidate("NVIDIA 551.23", ["PCI\\VEN_10DE\\1"], blockedReason: "Version bloquée par la politique (écran noir)")
            .Json();
        var r = ReportParser.Parse(json);
        var gpu = Assert.Single(r.ProblemDevices);
        Assert.False(gpu.HasCandidate);
        Assert.True(gpu.OnlyBlockedCandidates);
        Assert.Equal(1, r.Unresolved);
        Assert.Equal(0, r.Proposed);
        Assert.Equal(1, r.Blocked);
    }

    [Fact]
    public void Installation_outcomes_drive_health()
    {
        var json = new ReportFixture()
            .Candidate("Realtek Audio", ["X"]).Candidate("Intel Wi-Fi", ["Y"])
            .Outcome("Realtek Audio", "Installed", reboot: true)
            .Outcome("Intel Wi-Fi", "Failed")
            .Json();
        var r = ReportParser.Parse(json);
        Assert.False(r.AnalysisOnly);
        Assert.Equal(1, r.Installed);
        Assert.Equal(1, r.Failed);
        Assert.True(r.RebootRequired);
        Assert.False(r.Healthy); // échec d'installation = à traiter, même sans périphérique en erreur
    }

    [Theory]
    [InlineData("HP", "HP EliteDesk 800 G6", null, "HP EliteDesk 800 G6")]
    [InlineData("Dell Inc.", "Dell Pro 14", null, "Dell Pro 14")]
    [InlineData("Dell Inc.", "Latitude 7420", null, "Dell Inc. Latitude 7420")]
    [InlineData("LENOVO", "20W0005QFR", "ThinkPad T14 Gen 2i", "LENOVO ThinkPad T14 Gen 2i (20W0005QFR)")]
    [InlineData(null, "Virtual Machine", null, "Virtual Machine")]
    public void Display_model_avoids_repeating_the_manufacturer(string? manufacturer, string model, string? friendly, string expected) =>
        Assert.Equal(expected, new SystemSnapshot { Manufacturer = manufacturer, Model = model, FriendlyModel = friendly }.DisplayModel);

    [Fact]
    public void Machine_names_are_normalized_to_upper_case()
    {
        var r = ReportParser.Parse(new ReportFixture("pc-accueil").Json());
        Assert.Equal("PC-ACCUEIL", r.Name);
        Assert.Equal("pc-accueil", r.System.ComputerName);
    }

    [Fact]
    public void Tolerates_pascal_case_and_unknown_fields()
    {
        const string json = """
            { "Timestamp": "2026-11-20T10:00:00Z", "System": { "ComputerName": "POSTE-1", "Manufacturer": "HP" },
              "Devices": [ { "InstanceId": "PCI\\1", "Name": "X", "HardwareIds": ["PCI\\VEN_1"], "ErrorCode": 28, "Futur": {"a": 1} } ],
              "ChampInconnu": [1, 2, 3] }
            """;
        var r = ReportParser.Parse(json);
        Assert.Equal("POSTE-1", r.Name);
        Assert.Single(r.ProblemDevices);
        Assert.Equal("Aucun pilote installé", r.ProblemDevices[0].StatusText);
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"devices\": []}")]
    [InlineData("{\"system\": {\"computerName\": \"\"}, \"devices\": []}")]
    [InlineData("{\"system\": {\"computerName\": \"../etc/passwd\"}, \"devices\": []}")]
    [InlineData("{\"system\": {\"computerName\": \"PC-1\"}}")]
    public void Rejects_incomplete_or_invalid_reports(string json) =>
        Assert.Throws<InvalidReportException>(() => ReportParser.Parse(json));
}
