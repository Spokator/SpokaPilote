using System.Text.Json;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

public class V1FeatureTests
{
    // ------------------------------------------------------------------ mise à jour

    private static JsonElement Release(string tag, bool draft = false, string? digest = "sha256:ab120000000000000000000000000000000000000000000000000000000000ff")
    {
        var json = $$"""
            {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":false,"html_url":"https://github.com/Spokator/SpokaPilote/releases/tag/{{tag}}",
             "body":"Notes","assets":[
               {"name":"SpokaPilote-1.0.0.msix","browser_download_url":"https://github.com/x.msix","size":1},
               {"name":"SpokaPilote-Setup-1.0.0.exe","browser_download_url":"https://github.com/Spokator/SpokaPilote/releases/download/{{tag}}/SpokaPilote-Setup-1.0.0.exe","size":1234{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}]}
            """;
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Update_detects_newer_release_and_picks_setup_exe()
    {
        var update = UpdateChecker.Parse(Release("v1.2.0"), new Version(1, 0, 0));
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update!.Version);
        Assert.EndsWith("SpokaPilote-Setup-1.0.0.exe", update.DownloadUrl);
        Assert.Equal(1234, update.Size);
        Assert.Equal(64, update.Sha256!.Length);
    }

    [Fact]
    public void Update_picks_the_installer_matching_the_architecture()
    {
        var json = """
            {"tag_name":"v2.0.0","draft":false,"prerelease":false,"assets":[
              {"name":"SpokaPilote-Setup-2.0.0-arm64.exe","browser_download_url":"https://github.com/a/arm.exe","size":2},
              {"name":"SpokaPilote-Setup-2.0.0.exe","browser_download_url":"https://github.com/a/x64.exe","size":1}]}
            """;
        var release = JsonDocument.Parse(json).RootElement;
        Assert.Equal("https://github.com/a/x64.exe", UpdateChecker.Parse(release, new Version(1, 0), "x64")!.DownloadUrl);
        Assert.Equal("https://github.com/a/arm.exe", UpdateChecker.Parse(release, new Version(1, 0), "arm64")!.DownloadUrl);
        // Release sans installeur ARM64 : pas de mise à jour proposée à un PC ARM64 (plutôt que d'installer du x64).
        Assert.Null(UpdateChecker.Parse(Release("v2.0.0"), new Version(1, 0), "arm64"));
    }

    [Fact]
    public void Update_ignores_same_older_and_draft_releases()
    {
        Assert.Null(UpdateChecker.Parse(Release("v1.0.0"), new Version(1, 0, 0)));
        Assert.Null(UpdateChecker.Parse(Release("v0.9.0"), new Version(1, 0, 0)));
        Assert.Null(UpdateChecker.Parse(Release("v2.0.0", draft: true), new Version(1, 0, 0)));
    }

    [Fact]
    public void Update_integrity_rejects_wrong_size_or_hash()
    {
        var update = new UpdateInfo(new Version(1, 1), "v1.1", "https://github.com/x", "", "", 100, new string('a', 64));
        Assert.Null(UpdateChecker.CheckIntegrity(update, 100, new string('A', 64)));
        Assert.NotNull(UpdateChecker.CheckIntegrity(update, 99, new string('a', 64)));
        Assert.NotNull(UpdateChecker.CheckIntegrity(update, 100, new string('b', 64)));
        Assert.Null(UpdateChecker.ParseDigest("md5:abcd"));
    }

    // ------------------------------------------------------------------ rapport JSON (aller-retour)

    private static ScanResult SampleScan(string computer, params DeviceInfo[] extra)
    {
        var devices = new List<DeviceInfo>
        {
            new() { InstanceId = @"PCI\VEN_10EC&DEV_8168\1", Name = "Realtek Ethernet", HardwareIds = new[] { @"PCI\VEN_10EC&DEV_8168" }, DriverVersion = "10.1", InfName = "oem3.inf" },
        };
        devices.AddRange(extra);
        var candidate = new DriverCandidate { Id = "wu:1", Source = DriverSourceKind.WindowsUpdate, Title = "Realtek Net", Version = "10.2" };
        candidate.TargetDeviceIds.Add(devices[0].InstanceId);
        candidate.TargetDeviceNames.Add(devices[0].Name);
        return new ScanResult
        {
            System = new SystemInfo { ComputerName = computer, Manufacturer = "Dell Inc.", Model = "Latitude 7420", OsVersion = new Version(10, 0, 26100), OsDisplayVersion = "24H2" },
            Devices = devices,
            Candidates = new List<DriverCandidate> { candidate },
            Duration = TimeSpan.FromSeconds(12),
        };
    }

    private static DeviceInfo MissingNic() => new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_15F3\2", Name = "Contrôleur Ethernet", ErrorCode = 28, HardwareIds = new[] { @"PCI\VEN_8086&DEV_15F3" },
    };

    private static DeviceInfo BrokenUsb() => new()
    {
        InstanceId = @"USB\VID_0000&PID_0002\3", Name = "Périphérique USB inconnu", ErrorCode = 43, HardwareIds = new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" },
    };

    [Fact]
    public void Scan_report_roundtrips_system_and_devices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp-report-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ReportWriter.ToJson(SampleScan("PC-01", MissingNic())));
            var report = ScanReport.Load(path);
            Assert.Equal("PC-01", report.System.ComputerName);
            Assert.Equal(new Version(10, 0, 26100), report.System.OsVersion);
            Assert.True(report.System.IsWindows11);
            Assert.Equal(2, report.Devices.Count);
            Assert.Equal(new[] { @"PCI\VEN_8086&DEV_15F3" }, report.Devices[1].HardwareIds);
            Assert.True(report.Devices[1].HasProblem);
            Assert.Single(report.Candidates);
            Assert.Equal(new[] { "Contrôleur Ethernet" }, report.UnresolvedDevices);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Loading_a_non_spokapilote_json_fails_clearly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"hello":"world"}""");
            Assert.Throws<InvalidDataException>(() => ScanReport.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ rapport de parc

    [Fact]
    public void Fleet_report_keeps_latest_per_machine_and_ignores_hardware_faults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sp-fleet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var old = ScanReport.From(SampleScan("COMPTA-01", MissingNic()));
            old.Timestamp = new DateTime(2020, 1, 1);
            File.WriteAllText(Path.Combine(dir, "a-old.json"), old.ToJson());
            File.WriteAllText(Path.Combine(dir, "a-new.json"), ReportWriter.ToJson(SampleScan("COMPTA-01", BrokenUsb())));
            File.WriteAllText(Path.Combine(dir, "b.json"), ReportWriter.ToJson(SampleScan("ACCUEIL-02", MissingNic())));
            File.WriteAllText(Path.Combine(dir, "notes.json"), "{}"); // ignoré

            var machines = FleetReport.Load(dir);
            Assert.Equal(2, machines.Count);
            var compta = Assert.Single(machines, m => m.Name == "COMPTA-01");
            Assert.True(compta.Healthy);            // seule une panne USB matérielle
            Assert.Equal(1, compta.HardwareFaults);
            var accueil = Assert.Single(machines, m => m.Name == "ACCUEIL-02");
            Assert.False(accueil.Healthy);
            Assert.Equal(1, accueil.Unresolved);

            var html = FleetReport.ToHtml(machines, dir);
            Assert.Contains("ACCUEIL-02", html);
            Assert.Contains(@"PCI\VEN_8086&amp;DEV_15F3", html);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Hardware_faults_are_distinguished_from_missing_drivers()
    {
        Assert.True(BrokenUsb().IsHardwareFault);
        Assert.False(MissingNic().IsHardwareFault);
    }

    // ------------------------------------------------------------------ annulation

    [Theory]
    [InlineData("oem12.inf", true)]
    [InlineData("OEM7.INF", true)]
    [InlineData("hdaudio.inf", false)]
    [InlineData("oem12.inf & del C:\\*", false)]
    [InlineData("..\\oem1.inf", false)]
    public void Rollback_only_removes_published_oem_packages(string name, bool allowed)
        => Assert.Equal(allowed, PnpUtil.IsPublishedOemName(name));

    [Fact]
    public void Rollback_refuses_invalid_names_before_running_anything()
        => Assert.Throws<ArgumentException>(() => { _ = PnpUtil.DeleteDriverAsync("hdaudio.inf"); });

    [Fact]
    public void Intervention_can_rollback_only_with_added_packages()
    {
        Assert.False(new Intervention().CanRollback);
        Assert.True(new Intervention { AddedDriverPackages = { "oem5.inf" } }.CanRollback);
        Assert.False(new Intervention { AddedDriverPackages = { "oem5.inf" }, RolledBackAt = DateTime.Now }.CanRollback);
    }
}
