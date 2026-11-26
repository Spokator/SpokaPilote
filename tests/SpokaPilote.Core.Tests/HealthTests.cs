using System.Text.Json;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;

namespace SpokaPilote.Core.Tests;

public class HealthTests
{
    private static readonly DateTime Now = new(2026, 11, 26);

    private static DeviceInfo Device(string name, string id, int error = 0, string? version = "1.0.0.0", DateTime? date = null,
        string? provider = "Contoso", string? manufacturer = "Contoso", string? inf = "oem10.inf", bool? signed = true,
        string cls = "Media", string[]? hw = null) => new()
    {
        InstanceId = id,
        Name = name,
        ClassName = cls,
        Manufacturer = manufacturer,
        HardwareIds = hw ?? new[] { id[..id.LastIndexOf('\\')] },
        ErrorCode = error,
        DriverVersion = error is 28 or 1 ? null : version,
        DriverDate = error is 28 or 1 ? null : date ?? new DateTime(2025, 6, 1),
        DriverProvider = error is 28 or 1 ? null : provider,
        InfName = error is 28 or 1 ? null : inf,
        IsSigned = error is 28 or 1 ? null : signed,
    };

    private static DeviceInfo Sst(string version) => Device("Intel® Smart Sound Technology (Intel® SST) Audio Controller",
        @"PCI\VEN_8086&DEV_A0C8&SUBSYS_0A1F1028&REV_20\3&2", version: version, provider: "Intel Corporation",
        manufacturer: "Intel Corporation", inf: "oem42.inf", cls: "System");

    private static DriverPolicy ReferenceOnly()
    {
        var reference = PolicyLoader.LoadEmbeddedReference();
        Assert.True(reference.IsValid, string.Join(" | ", reference.Errors));
        return DriverPolicy.Combine(null, reference.Policy);
    }

    private static DriverPolicy WithOrganization(string rulesJson)
    {
        var org = DriverPolicy.Parse($$"""{ "schema": 1, "name": "Parc", "rules": [ {{rulesJson}} ] }""");
        Assert.True(org.IsValid, string.Join(" | ", org.Errors));
        return DriverPolicy.Combine(org.Policy, PolicyLoader.LoadEmbeddedReference().Policy);
    }

    private static DriverCandidate Update(DeviceInfo target, string version = "2.0.0.0", string? blocked = null,
        CandidateKind kind = CandidateKind.Update)
    {
        var c = new DriverCandidate
        {
            Id = "wu:" + target.InstanceId + version,
            Source = DriverSourceKind.WindowsUpdate,
            Title = target.Name + " " + version,
            Version = version,
            Kind = kind,
            BlockedReason = blocked,
        };
        c.TargetDeviceIds.Add(target.InstanceId);
        c.TargetDeviceNames.Add(target.Name);
        return c;
    }

    private static HealthReport Compute(IReadOnlyList<DeviceInfo> devices, IReadOnlyList<DriverCandidate>? candidates = null,
        DriverPolicy? policy = null, HealthOptions? options = null) =>
        DriverHealth.Compute(devices, candidates ?? Array.Empty<DriverCandidate>(), policy ?? ReferenceOnly(),
            options ?? new HealthOptions { Now = Now });

    // ------------------------------------------------------------------ score et note

    [Fact]
    public void Perfect_machine_scores_100_A_without_findings()
    {
        var h = Compute(new[] { Device("Carte son", @"PCI\VEN_1234&DEV_0001\1"), Device("Webcam", @"USB\VID_1234&PID_0002\2") });
        Assert.Equal(100, h.Score);
        Assert.Equal("A", h.Grade);
        Assert.Empty(h.Findings);
    }

    [Theory]
    [InlineData(100, "A")]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(75, "B")]
    [InlineData(74, "C")]
    [InlineData(60, "C")]
    [InlineData(59, "D")]
    [InlineData(40, "D")]
    [InlineData(39, "E")]
    [InlineData(0, "E")]
    public void Grade_thresholds(int score, string grade) => Assert.Equal(grade, DriverHealth.GradeFor(score));

    [Fact]
    public void Missing_driver_and_device_error_cost_15_points_each()
    {
        var missing = Device("Contrôleur inconnu", @"PCI\VEN_1234&DEV_0003\3", error: 28);
        var error = Device("Lecteur de cartes", @"PCI\VEN_1234&DEV_0004\4", error: 10);
        var h = Compute(new[] { missing, error });
        Assert.Equal(70, h.Score);
        Assert.Equal("C", h.Grade);
        Assert.Contains(h.Findings, f => f.Code == HealthCodes.MissingDriver && f.Severity == HealthSeverity.Warning && f.DeviceId == missing.InstanceId);
        Assert.Contains(h.Findings, f => f.Code == HealthCodes.DeviceError && f.DeviceId == error.InstanceId && f.Detail.Contains("device repair"));
    }

    [Fact]
    public void Missing_driver_mentions_the_available_driver()
    {
        var missing = Device("Contrôleur inconnu", @"PCI\VEN_1234&DEV_0003\3", error: 28);
        var h = Compute(new[] { missing }, new[] { Update(missing, kind: CandidateKind.MissingDriver) });
        var f = Assert.Single(h.Findings);
        Assert.Contains("Pilote disponible", f.Detail);
        Assert.Equal(85, h.Score); // un pilote manquant (proposé) n'est pas compté une deuxième fois comme mise à jour
    }

    [Fact]
    public void Hardware_fault_is_reported_as_info_and_does_not_lower_the_score()
    {
        var usb = Device("Périphérique USB inconnu (échec de demande de descripteur de périphérique)", @"USB\VID_0000&PID_0002\5&2E786FD2&0&4",
            error: 43, cls: "USB", hw: new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" }, inf: "usb.inf");
        var h = Compute(new[] { usb });
        var f = Assert.Single(h.Findings);
        Assert.Equal(HealthCodes.HardwareFault, f.Code);
        Assert.Equal(HealthSeverity.Info, f.Severity);
        Assert.Equal(100, h.Score);
    }

    [Fact]
    public void Disabled_device_is_info_only()
    {
        var h = Compute(new[] { Device("Bluetooth", @"USB\VID_1234&PID_0009\9", error: 22) });
        Assert.Equal(HealthCodes.DeviceDisabled, Assert.Single(h.Findings).Code);
        Assert.Equal(100, h.Score);
    }

    [Fact]
    public void Unsigned_driver_is_a_warning()
    {
        var h = Compute(new[] { Device("Adaptateur série", @"USB\VID_1A86&PID_7523\1", signed: false) });
        var f = Assert.Single(h.Findings);
        Assert.Equal(HealthCodes.UnsignedDriver, f.Code);
        Assert.Equal(HealthSeverity.Warning, f.Severity);
        Assert.Equal(85, h.Score);
    }

    [Fact]
    public void Old_third_party_drivers_are_reported_but_builtin_windows_drivers_are_ignored()
    {
        var old = Device("Lecteur d'empreintes", @"USB\VID_1234&PID_0005\5", date: new DateTime(2019, 3, 1), inf: "oem3.inf");
        var builtin = Device("Clavier HID", @"HID\VID_1234&PID_0006\6", date: new DateTime(2006, 6, 21), inf: "keyboard.inf", provider: "Microsoft");
        var recent = Device("Webcam", @"USB\VID_1234&PID_0007\7", date: new DateTime(2023, 1, 1), inf: "oem4.inf");
        var h = Compute(new[] { old, builtin, recent });
        var f = Assert.Single(h.Findings);
        Assert.Equal(HealthCodes.OldDriver, f.Code);
        Assert.Equal(old.InstanceId, f.DeviceId);
        Assert.Equal(97, h.Score);
    }

    [Fact]
    public void Old_driver_and_update_penalties_are_capped()
    {
        var devices = Enumerable.Range(1, 12)
            .Select(i => Device($"Ancien {i}", $@"PCI\VEN_1234&DEV_{i:X4}\{i}", date: new DateTime(2015, 1, i), inf: $"oem{i}.inf"))
            .ToList();
        var updates = devices.Select(d => Update(d)).ToList();
        var h = Compute(devices, updates);
        Assert.Equal(12, h.Findings.Count(f => f.Code == HealthCodes.OldDriver));
        Assert.Equal(12, h.Findings.Count(f => f.Code == HealthCodes.UpdateAvailable));
        Assert.Equal(100 - DriverHealth.MaxPenaltyOldDrivers - DriverHealth.MaxPenaltyUpdates, h.Score);
    }

    [Fact]
    public void Blocked_or_informational_candidates_are_not_counted_as_available_updates()
    {
        var d = Device("Carte graphique", @"PCI\VEN_8086&DEV_A780\1", cls: "Display");
        var blocked = Update(d, blocked: "Version bloquée par la politique");
        var info = new DriverCandidate { Id = "intel:link", Source = DriverSourceKind.Intel, Title = "Intel Graphics", IsInformational = true };
        info.TargetDeviceIds.Add(d.InstanceId);
        var h = Compute(new[] { d }, new[] { blocked, info });
        Assert.Empty(h.Findings);
        Assert.Equal(100, h.Score);
    }

    // ------------------------------------------------------------------ pilotes dangereux déjà installés

    [Fact]
    public void Known_bad_driver_from_reference_list_is_critical_and_caps_grade_at_D()
    {
        var h = Compute(new[] { Sst("10.29.0.5152") });
        var f = Assert.Single(h.Findings);
        Assert.Equal(HealthSeverity.Critical, f.Severity);
        Assert.Equal(HealthCodes.KnownBadDriver, f.Code);
        Assert.Contains("10.29.0.5152", f.Detail);
        Assert.Contains("IntcAudioBus", f.Detail); // raison de la liste de référence
        Assert.Contains("https://learn.microsoft.com/", f.Detail); // lien de l'avis
        Assert.StartsWith("https://", f.AdvisoryUrl);
        Assert.Contains("ref-intel-sst-intcaudiobus-5152", f.Detail);
        Assert.True(h.Score <= DriverHealth.KnownBadScoreCap);
        Assert.Contains(h.Grade, new[] { "D", "E" });
        Assert.NotNull(f.Recommendation);
    }

    [Theory]
    [InlineData("10.29.0.5714")] // version corrigée
    [InlineData("10.29.0.5151")]
    [InlineData(null)]
    public void Fixed_or_unknown_versions_are_not_flagged(string? version)
    {
        var h = Compute(new[] { Sst(version!) });
        Assert.DoesNotContain(h.Findings, f => f.Code == HealthCodes.KnownBadDriver);
    }

    [Fact]
    public void Same_version_from_another_manufacturer_is_not_flagged()
    {
        var other = Device("Contrôleur audio", @"PCI\VEN_10EC&DEV_0001\1", version: "10.29.0.5152", provider: "Realtek", manufacturer: "Realtek");
        Assert.Empty(Compute(new[] { other }).Findings);
    }

    [Fact]
    public void Known_bad_driver_proposes_the_available_update()
    {
        var sst = Sst("10.29.0.5152");
        var h = Compute(new[] { sst }, new[] { Update(sst, "10.29.0.5714") });
        var f = h.Findings.First(x => x.Code == HealthCodes.KnownBadDriver);
        Assert.Contains("Mettez à jour", f.Recommendation);
        Assert.Contains("10.29.0.5714", f.Recommendation);
    }

    [Fact]
    public void Known_bad_driver_installed_by_spokapilote_proposes_rollback()
    {
        var intervention = new Intervention { Id = "20261120-101010", AddedDriverPackages = { "oem42.inf" } };
        var h = Compute(new[] { Sst("10.30.0.5152") }, options: new HealthOptions { Now = Now, Interventions = new[] { intervention } });
        var f = Assert.Single(h.Findings);
        Assert.Contains("spokapilote rollback 20261120-101010", f.Recommendation);
    }

    [Fact]
    public void Organization_block_rule_with_version_flags_the_installed_driver()
    {
        var gpu = Device("NVIDIA RTX A2000", @"PCI\VEN_10DE&DEV_2531\1", version: "32.0.15.6094", provider: "NVIDIA", manufacturer: "NVIDIA", cls: "Display");
        var policy = WithOrganization("""{ "id": "nv-566", "action": "block", "manufacturer": "NVIDIA", "versions": "32.0.15.6094", "reason": "Écrans noirs sur les stations CAO (ticket #812)" }""");
        var f = Assert.Single(Compute(new[] { gpu }, policy: policy).Findings);
        Assert.Equal(HealthCodes.KnownBadDriver, f.Code);
        Assert.Contains("politique de l'organisation", f.Detail);
        Assert.Contains("ticket #812", f.Detail);
    }

    [Fact]
    public void Rules_without_version_or_targeting_sources_do_not_flag_installed_drivers()
    {
        var gpu = Device("NVIDIA RTX A2000", @"PCI\VEN_10DE&DEV_2531\1", version: "32.0.15.6094", provider: "NVIDIA", manufacturer: "NVIDIA", cls: "Display");
        // « ne pas installer de pilote NVIDIA » ne veut pas dire que le pilote en place est dangereux.
        var noVersion = WithOrganization("""{ "action": "block", "manufacturer": "NVIDIA", "reason": "Pilotes graphiques gérés à la main" }""");
        var bySource = WithOrganization("""{ "action": "block", "source": "WindowsUpdate", "versions": "32.0.15.6094", "reason": "x" }""");
        var pin = WithOrganization("""{ "action": "pin", "manufacturer": "NVIDIA", "pinVersion": "31.0.0.0", "reason": "x" }""");
        Assert.Empty(Compute(new[] { gpu }, policy: noVersion).Findings);
        Assert.Empty(Compute(new[] { gpu }, policy: bySource).Findings);
        Assert.Empty(Compute(new[] { gpu }, policy: pin).Findings);
    }

    [Fact]
    public void Organization_allow_rule_placed_before_lifts_the_reference_block()
    {
        var policy = WithOrganization("""{ "action": "allow", "hardwareId": "PCI\\VEN_8086&DEV_A0C8*", "reason": "Validé par l'IT sur ce modèle" }""");
        Assert.Empty(Compute(new[] { Sst("10.29.0.5152") }, policy: policy).Findings);
    }

    [Fact]
    public void Ignored_reference_rule_is_not_applied()
    {
        var org = DriverPolicy.Parse("""{ "schema": 1, "name": "Parc", "settings": { "ignoreReferenceRules": [ "ref-intel-sst-intcaudiobus-5152" ] }, "rules": [] }""");
        var policy = DriverPolicy.Combine(org.Policy, PolicyLoader.LoadEmbeddedReference().Policy);
        Assert.Empty(Compute(new[] { Sst("10.29.0.5152") }, policy: policy).Findings);
    }

    [Fact]
    public void Title_criterion_matches_the_original_inf_name()
    {
        var policy = WithOrganization("""{ "action": "block", "title": "intcaudiobus.inf", "versionMax": "10.29.0.6000", "reason": "x" }""");
        var options = new HealthOptions { Now = Now, OriginalInfNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["oem42.inf"] = "intcaudiobus.inf" } };
        Assert.Single(Compute(new[] { Sst("10.29.0.5714") }, policy: policy, options: options).Findings);
        Assert.Empty(Compute(new[] { Sst("10.29.0.5714") }, policy: policy).Findings);
    }

    // ------------------------------------------------------------------ tri et contrat JSON

    [Fact]
    public void Findings_are_sorted_critical_first()
    {
        var devices = new[]
        {
            Device("Ancien", @"PCI\VEN_1234&DEV_0001\1", date: new DateTime(2015, 1, 1)),
            Device("Sans pilote", @"PCI\VEN_1234&DEV_0002\2", error: 28),
            Sst("10.29.0.5152"),
        };
        var h = Compute(devices);
        Assert.Equal(new[] { HealthSeverity.Critical, HealthSeverity.Warning, HealthSeverity.Info }, h.Findings.Select(f => f.Severity));
    }

    [Fact]
    public void Scan_report_contains_the_health_object_with_the_contract_shape()
    {
        var sst = Sst("10.29.0.5152");
        var devices = new List<DeviceInfo> { sst };
        var scan = new ScanResult
        {
            System = new SystemInfo(),
            Devices = devices,
            Candidates = new List<DriverCandidate>(),
            Health = Compute(devices),
        };
        using var doc = JsonDocument.Parse(ScanReport.From(scan).ToJson());
        var health = doc.RootElement.GetProperty("health");
        Assert.Equal(JsonValueKind.Number, health.GetProperty("score").ValueKind);
        Assert.Equal("D", health.GetProperty("grade").GetString());
        var finding = health.GetProperty("findings")[0];
        Assert.Equal("critical", finding.GetProperty("severity").GetString());
        Assert.Equal("known-bad-driver", finding.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(finding.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(finding.GetProperty("detail").GetString()));
        Assert.Equal(sst.InstanceId, finding.GetProperty("deviceId").GetString());
        // Seuls les champs du contrat sont publiés.
        Assert.Equal(new[] { "severity", "code", "title", "detail", "deviceId" }, finding.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "score", "grade", "findings" }, health.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Health_is_optional_and_survives_a_round_trip()
    {
        var scan = new ScanResult { System = new SystemInfo(), Devices = new List<DeviceInfo> { Sst("1.0.0.0") }, Candidates = new List<DriverCandidate>() };
        using (var doc = JsonDocument.Parse(ScanReport.From(scan).ToJson()))
            Assert.False(doc.RootElement.TryGetProperty("health", out _)); // (« health » des périphériques = leur état, sans rapport)

        var withHealth = new ScanResult { System = new SystemInfo(), Devices = new List<DeviceInfo> { Sst("10.29.0.5152") }, Candidates = new List<DriverCandidate>() };
        withHealth = new ScanResult { System = withHealth.System, Devices = withHealth.Devices, Candidates = withHealth.Candidates, Health = Compute(withHealth.Devices) };
        var path = Path.Combine(Path.GetTempPath(), $"sp-health-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ScanReport.From(withHealth).ToJson());
            var reloaded = ScanReport.Load(path);
            Assert.NotNull(reloaded.Health);
            Assert.Equal(HealthSeverity.Critical, reloaded.Health!.Findings[0].Severity);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Html_report_shows_the_health_card()
    {
        var devices = new List<DeviceInfo> { Sst("10.29.0.5152") };
        var scan = new ScanResult { System = new SystemInfo(), Devices = devices, Candidates = new List<DriverCandidate>(), Health = Compute(devices) };
        var html = ReportWriter.ToHtml(scan);
        Assert.Contains("Santé des pilotes", html);
        Assert.Contains("class=\"grade g-D\"", html);
        Assert.Contains("sev-critical", html);
        Assert.Contains("<a href=\"https://learn.microsoft.com/", html);
    }
}
