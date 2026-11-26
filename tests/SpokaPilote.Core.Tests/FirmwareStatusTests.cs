using System.Text.Json;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using SpokaPilote.Core.Sources.Oem;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// État du BIOS et des firmwares : comparaison des versions selon chaque constructeur, importance déclarée,
/// constat « bios-outdated », rapport JSON / HTML. Rien n'est jamais installé par cette fonction.
/// </summary>
public class FirmwareStatusTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static ScanContext Context(string manufacturer, string? bios, DateTime? biosDate = null, bool firmware = false, params DeviceInfo[] devices) => new()
    {
        System = new SystemInfo
        {
            Manufacturer = manufacturer,
            Model = "20XWCTO1WW",
            SystemSku = "0A6B",
            BaseBoardProduct = "8549",
            BiosVersion = bios,
            BiosReleaseDate = biosDate,
            OsVersion = new Version(10, 0, 26100),
            OsDisplayVersion = "24H2",
            Architecture = "amd64",
        },
        Devices = devices,
        Settings = new Settings { IncludeBiosAndFirmware = firmware },
        Options = new ScanOptions(),
    };

    private static DeviceInfo Thunderbolt() => new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_9A1B&SUBSYS_0A6B1028&REV_01\3&11583659&0&68",
        Name = "Contrôleur hôte Thunderbolt(TM) 4 USB4",
        HardwareIds = new[] { @"PCI\VEN_8086&DEV_9A1B&SUBSYS_0A6B1028&REV_01", @"PCI\VEN_8086&DEV_9A1B" },
        DriverVersion = "1.41.1193.0",
    };

    // ------------------------------------------------------------------ comparaison des versions

    [Theory]
    // Dell : « 1.23.0 », comparaison numérique (1.9.0 < 1.23.0), anciens BIOS « A12 ».
    [InlineData(OemVendor.Dell, "1.12.0", "1.18.0", null, null, 1)]
    [InlineData(OemVendor.Dell, "1.9.0", "1.23.0", null, null, 1)]
    [InlineData(OemVendor.Dell, "1.40.0", "1.40.0", null, null, 0)]
    [InlineData(OemVendor.Dell, "1.41.0", "1.40.0", null, null, -1)]
    [InlineData(OemVendor.Dell, "A12", "A29", null, null, 1)]
    [InlineData(OemVendor.Dell, "A12", "1.2.0", null, null, null)]
    [InlineData(OemVendor.Dell, "BIOS-X", "1.2.0", null, null, null)]
    // HP : « T37 Ver. 01.15.00 » (famille + version), révision de SoftPaq ignorée, « F.60 » grand public.
    [InlineData(OemVendor.HP, "T37 Ver. 01.15.00", "01.17.00", null, "HP Notebook System BIOS Update (T37)", 1)]
    [InlineData(OemVendor.HP, "T37 Ver. 01.17.00", "01.17.00 Rev.A", null, null, 0)]
    [InlineData(OemVendor.HP, "T37 Ver. 01.17.00", "01.17.00 A 1", null, null, 0)]
    [InlineData(OemVendor.HP, "T37 Ver. 01.15.00", "01.17.00", null, "HP Notebook System BIOS Update (S70)", null)]
    [InlineData(OemVendor.HP, "68CDD Ver. F.60", "F.62", null, null, 1)]
    [InlineData(OemVendor.HP, "F.60", "01.02.00", null, null, null)]
    // Lenovo : « N2XET39W (1.29 ) » ; l'identifiant de build fait foi quand le catalogue en donne un.
    [InlineData(OemVendor.Lenovo, "N2XET39W (1.29 )", "1.31", "N2XET41W", null, 1)]
    [InlineData(OemVendor.Lenovo, "N2XET39W (1.29 )", "1.29", "N2XET39W^", null, 0)]
    [InlineData(OemVendor.Lenovo, "N2XET41W (1.31 )", "1.29", "N2XET39W", null, -1)]
    [InlineData(OemVendor.Lenovo, "N2XET39W (1.29 )", "1.31", null, null, 1)]
    [InlineData(OemVendor.Lenovo, "N2XET39W (1.29 )", "1.67", "N35ET67W", null, null)] // autre famille de BIOS
    [InlineData(OemVendor.Lenovo, "N2XET39W (1.29 )", "0131", null, null, null)] // format inconnu : pas de devinette
    [InlineData(OemVendor.Lenovo, "M3AKT56A", "M3AKT59A", null, null, 1)] // ThinkCentre
    [InlineData(OemVendor.Dell, null, "1.18.0", null, null, null)]
    public void Bios_versions_are_compared_per_vendor_scheme(OemVendor vendor, string? installed, string catalog, string? build, string? title, int? expected)
    {
        var result = BiosVersionComparer.Compare(vendor, installed, catalog, build is null ? null : new[] { build }, title);
        Assert.Equal(expected, result is null ? null : Math.Sign(result.Value));
    }

    [Theory]
    [InlineData("Urgent-Dell highly recommends applying this update as soon as possible.", VendorImportance.Critical)]
    [InlineData("Recommended-Dell recommends applying this update during your next scheduled update cycle.", VendorImportance.Recommended)]
    [InlineData("Optional-Dell recommends the customer review specifics about the update", VendorImportance.Optional)]
    [InlineData("Critical", VendorImportance.Critical)]
    [InlineData("Routine", VendorImportance.Optional)]
    [InlineData("", VendorImportance.Unknown)]
    [InlineData("Security", VendorImportance.Unknown)]
    public void Vendor_importance_is_mapped_from_the_catalog_label(string text, VendorImportance expected)
        => Assert.Equal(expected, BiosEvaluator.ImportanceFromText(text));

    [Theory]
    [InlineData("1", VendorImportance.Critical)]
    [InlineData("2", VendorImportance.Recommended)]
    [InlineData("3", VendorImportance.Optional)]
    [InlineData(null, VendorImportance.Unknown)]
    public void Lenovo_severity_is_mapped(string? type, VendorImportance expected)
        => Assert.Equal(expected, BiosEvaluator.ImportanceFromLenovoSeverity(type));

    // ------------------------------------------------------------------ Dell

    [Fact]
    public void Dell_outdated_bios_is_reported_without_firmware_option_and_never_proposed()
    {
        var context = Context("Dell Inc.", "1.12.0", new DateTime(2025, 2, 1));
        var result = DellSource.Parse(Fixture("dell-catalog-bios.xml"), context);

        Assert.DoesNotContain(result, c => c.Kind == CandidateKind.Firmware); // opt-in inchangé
        var bios = Assert.IsType<BiosStatus>(context.Bios);
        Assert.Equal(BiosState.Outdated, bios.State);
        Assert.Equal("Dell", bios.Vendor);
        Assert.Equal("1.12.0", bios.InstalledVersion);
        Assert.Equal("1.18.0", bios.LatestVersion); // l'entrée d'un autre modèle (1.30.0) est ignorée
        // 1.18.0 est « Recommended » mais 1.15.0, intermédiaire, est « Urgent » : la mise à jour est critique.
        Assert.Equal(VendorImportance.Critical, bios.Importance);
        Assert.Equal("Urgent", bios.ImportanceLabel);
        Assert.Equal("https://downloads.dell.com/FOLDER12000118M/1/Latitude_7420_7520_1.18.0.exe", bios.DownloadUrl);
        Assert.Equal("https://www.dell.com/support/home/en-us/drivers/DriversDetails?driverId=BIOS118", bios.InfoUrl); // http → https
        Assert.Equal("BIOS : 1.12.0 installé, 1.18.0 disponible (critique) → https://www.dell.com/support/home/en-us/drivers/DriversDetails?driverId=BIOS118", bios.Summary);
    }

    [Fact]
    public void Dell_bios_newer_than_last_urgent_release_is_only_recommended()
    {
        var context = Context("Dell Inc.", "1.15.0");
        DellSource.Parse(Fixture("dell-catalog-bios.xml"), context);
        Assert.Equal(BiosState.Outdated, context.Bios!.State);
        Assert.Equal(VendorImportance.Recommended, context.Bios.Importance);
    }

    [Fact]
    public void Dell_up_to_date_bios_is_not_proposed_even_with_firmware_enabled()
    {
        var upToDate = Context("Dell Inc.", "1.18.0", firmware: true);
        var result = DellSource.Parse(Fixture("dell-catalog-bios.xml"), upToDate);
        Assert.Equal(BiosState.UpToDate, upToDate.Bios!.State);
        Assert.DoesNotContain(result, c => c.Title.Contains("System BIOS"));

        var outdated = Context("Dell Inc.", "1.12.0", firmware: true);
        var bios = Assert.Single(DellSource.Parse(Fixture("dell-catalog-bios.xml"), outdated), c => c.Title.Contains("System BIOS"));
        Assert.Equal(CandidateKind.Firmware, bios.Kind);
        Assert.Contains(bios.Id, outdated.Bios!.CandidateIds);
    }

    [Fact]
    public void Dell_incomparable_version_is_reported_as_unknown_and_candidate_kept()
    {
        var context = Context("Dell Inc.", "A12", firmware: true);
        var result = DellSource.Parse(Fixture("dell-catalog-bios.xml"), context);
        Assert.Equal(BiosState.Unknown, context.Bios!.State);
        Assert.Contains("impossible de comparer", context.Bios.Summary);
        Assert.Contains(result, c => c.Kind == CandidateKind.Firmware && c.Title.Contains("System BIOS")); // comme avant
    }

    [Fact]
    public void Dell_thunderbolt_firmware_is_listed_only_for_present_hardware_and_docks_without_ids_are_skipped()
    {
        var withTbt = Context("Dell Inc.", "1.18.0", devices: Thunderbolt());
        DellSource.Parse(Fixture("dell-catalog-bios.xml"), withTbt);
        var status = withTbt.BuildFirmwareStatus()!;
        var tbt = Assert.Single(status.Components);
        Assert.Equal("thunderbolt", tbt.Kind);
        Assert.Equal("37.0", tbt.Version);
        Assert.Equal(VendorImportance.Recommended, tbt.Importance);
        Assert.Contains("Contrôleur hôte Thunderbolt(TM) 4 USB4", tbt.Devices);
        Assert.StartsWith("https://downloads.dell.com/", tbt.DownloadUrl);
        // Station WD19 : aucun identifiant matériel au catalogue → non listée, et on le dit.
        Assert.Contains(status.Notes, n => n.Contains("sans identifiant matériel"));

        var without = Context("Dell Inc.", "1.18.0");
        DellSource.Parse(Fixture("dell-catalog-bios.xml"), without);
        Assert.Empty(without.BuildFirmwareStatus()!.Components);
    }

    [Fact]
    public void Dell_catalog_without_bios_for_the_model_says_so()
    {
        var context = Context("Dell Inc.", "1.0.0");
        DellSource.Parse(Fixture("dell-catalog.xml"), context); // catalogue de base : BIOS 1.40.0 pour 0A6B
        Assert.Equal(BiosState.Outdated, context.Bios!.State);

        var other = new ScanContext
        {
            System = new SystemInfo { Manufacturer = "Dell Inc.", SystemSku = "0FFF", BiosVersion = "1.0.0", OsVersion = new Version(10, 0, 26100) },
            Devices = Array.Empty<DeviceInfo>(),
            Settings = new Settings(),
            Options = new ScanOptions(),
        };
        DellSource.Parse(Fixture("dell-catalog.xml"), other);
        Assert.Equal(BiosState.NotInCatalog, other.Bios!.State);
    }

    [Fact]
    public void Inconsistent_dates_make_the_comparison_unknown()
    {
        // Version « plus ancienne » mais BIOS installé daté bien après la version du catalogue : on ne conclut pas.
        var context = Context("Dell Inc.", "1.12.0", new DateTime(2027, 6, 1));
        DellSource.Parse(Fixture("dell-catalog-bios.xml"), context);
        Assert.Equal(BiosState.Unknown, context.Bios!.State);
        Assert.Contains("incohérentes", context.Bios.Reason);
    }

    // ------------------------------------------------------------------ HP

    [Fact]
    public void Hp_bios_family_version_and_release_type_are_read()
    {
        var dock = new DeviceInfo
        {
            InstanceId = @"USB\VID_03F0&PID_0667\5&1A2B3C&0&1",
            Name = "HP Thunderbolt Dock G4",
            HardwareIds = new[] { @"USB\VID_03F0&PID_0667&REV_0100", @"USB\VID_03F0&PID_0667" },
            DriverVersion = "10.0.26100.1",
        };
        var context = Context("HP", "T37 Ver. 01.15.00", firmware: false, devices: dock);
        var result = HpSource.Parse(Fixture("hp-reference-bios.xml"), context);

        Assert.Empty(result); // BIOS et firmwares : jamais proposés sans --include-firmware
        var bios = context.Bios!;
        Assert.Equal(BiosState.Outdated, bios.State);
        Assert.Equal("01.17.00 Rev.A", bios.LatestVersion);
        Assert.Equal(VendorImportance.Critical, bios.Importance);
        Assert.Equal("https://ftp.hp.com/pub/softpaq/sp171001-171500/sp171001.html", bios.InfoUrl);
        Assert.Equal(new DateTime(2026, 10, 2), bios.LatestDate);

        var status = context.BuildFirmwareStatus()!;
        var component = Assert.Single(status.Components); // le Dock G5 sans identifiant matériel n'est pas listé
        Assert.Equal("dock", component.Kind);
        Assert.Equal("HP Thunderbolt Dock G4 Firmware", component.Title);
        Assert.Single(status.Notes);
    }

    [Fact]
    public void Hp_same_bios_with_softpaq_revision_is_up_to_date_and_not_proposed()
    {
        var context = Context("HP", "T37 Ver. 01.17.00", firmware: true);
        var result = HpSource.Parse(Fixture("hp-reference-bios.xml"), context);
        Assert.Equal(BiosState.UpToDate, context.Bios!.State);
        Assert.DoesNotContain(result, c => c.SourceItemId == "sp171001");
    }

    // ------------------------------------------------------------------ Lenovo

    private const string LenovoBiosDescriptor = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Package name="N2XUJ41W" id="n2xuj41w" version="1.41">
          <Title default="EN"><Desc id="EN">BIOS Update for Windows 10 (64-bit), Windows 11 (64-bit) - ThinkPad T14 Gen 2</Desc></Title>
          <Vendor>Lenovo</Vendor>
          <ReleaseDate>2026-09-30</ReleaseDate>
          <Severity type="1" />
          <Reboot type="5" />
          <PackageType type="3" />
          <Install rc="0,1" type="cmd" default="EN"><Cmdline id="EN">winuptp.exe -s</Cmdline></Install>
          <Dependencies><_Bios><Level>N2XET*</Level></_Bios></Dependencies>
          <DetectInstall><_Bios><Level>N2XET41W^</Level></_Bios></DetectInstall>
          <Files>
            <Installer><File><Name>n2xuj41w.exe</Name><CRC>C030171D1BB5E65B8F9B9BCD0234BA0D805F2635F6E96A5951B78064347F9A90</CRC><Size>30000000</Size></File></Installer>
            <Readme><File><Name>n2xuj41w.txt</Name></File></Readme>
          </Files>
        </Package>
        """;

    [Fact]
    public void Lenovo_bios_descriptor_feeds_the_status_without_becoming_a_candidate()
    {
        var uri = new Uri("https://download.lenovo.com/pccbbs/mobiles/n2xuj41w_2_.xml");
        var context = Context("LENOVO", "N2XET39W (1.29 )");
        Assert.Null(LenovoSource.ParseDescriptor(LenovoBiosDescriptor, uri, "BIOS/UEFI", context));
        var results = new List<DriverCandidate>();
        var bios = BiosEvaluator.Finish(context, OemVendor.Lenovo, results);

        Assert.Equal(BiosState.Outdated, bios.State);
        Assert.Equal("1.41", bios.LatestVersion);
        Assert.Equal(VendorImportance.Critical, bios.Importance);
        Assert.Equal("Critical", bios.ImportanceLabel);
        Assert.Equal("https://download.lenovo.com/pccbbs/mobiles/n2xuj41w.exe", bios.DownloadUrl);
        Assert.Equal("https://download.lenovo.com/pccbbs/mobiles/n2xuj41w.txt", bios.InfoUrl);
    }

    [Fact]
    public void Lenovo_bios_of_another_family_is_not_compared()
    {
        var uri = new Uri("https://download.lenovo.com/pccbbs/mobiles/n2xuj41w_2_.xml");
        var context = Context("LENOVO", "N35ET59W (1.59 )", firmware: true);
        var candidate = LenovoSource.ParseDescriptor(LenovoBiosDescriptor, uri, "BIOS/UEFI", context);
        Assert.NotNull(candidate); // --include-firmware : proposé comme avant (jamais coché par défaut)
        var results = new List<DriverCandidate> { candidate! };
        var bios = BiosEvaluator.Finish(context, OemVendor.Lenovo, results);
        Assert.Equal(BiosState.Unknown, bios.State);
        Assert.Single(results);
    }

    [Fact]
    public void Lenovo_up_to_date_bios_candidate_is_removed()
    {
        var uri = new Uri("https://download.lenovo.com/pccbbs/mobiles/n2xuj41w_2_.xml");
        var context = Context("LENOVO", "N2XET41W (1.41 )", firmware: true);
        var results = new List<DriverCandidate> { LenovoSource.ParseDescriptor(LenovoBiosDescriptor, uri, "BIOS/UEFI", context)! };
        Assert.Equal(BiosState.UpToDate, BiosEvaluator.Finish(context, OemVendor.Lenovo, results).State);
        Assert.Empty(results);
    }

    // ------------------------------------------------------------------ santé

    private static FirmwareStatus Outdated(VendorImportance importance, string? candidateId = null)
    {
        var bios = new BiosStatus
        {
            Vendor = "Dell", State = BiosState.Outdated, InstalledVersion = "1.12.0", LatestVersion = "1.18.0",
            LatestDate = new DateTime(2026, 9, 15), Importance = importance, Title = "Dell Latitude 7420/7520 System BIOS",
            InfoUrl = "https://www.dell.com/support/home/en-us/drivers/DriversDetails?driverId=BIOS118",
        };
        if (candidateId is not null) bios.CandidateIds.Add(candidateId);
        return new FirmwareStatus { Bios = bios };
    }

    private static HealthReport Health(FirmwareStatus? firmware, IReadOnlyList<DriverCandidate>? candidates = null) =>
        DriverHealth.Compute(Array.Empty<DeviceInfo>(), candidates ?? Array.Empty<DriverCandidate>(), null,
            new HealthOptions { Now = new DateTime(2026, 11, 26), Firmware = firmware });

    [Fact]
    public void Critical_bios_update_is_a_warning_finding()
    {
        var h = Health(Outdated(VendorImportance.Critical));
        var f = Assert.Single(h.Findings);
        Assert.Equal(HealthCodes.BiosOutdated, f.Code);
        Assert.Equal("bios-outdated", f.Code);
        Assert.Equal(HealthSeverity.Warning, f.Severity);
        Assert.Null(f.DeviceId);
        Assert.Contains("1.12.0", f.Title);
        Assert.Contains("1.18.0", f.Title);
        Assert.Contains("BitLocker", f.Detail);
        Assert.Contains("https://www.dell.com/", f.Detail);
        Assert.Equal(100 - DriverHealth.PenaltyBiosOutdatedCritical, h.Score);
    }

    [Fact]
    public void Non_critical_bios_update_is_info_and_up_to_date_bios_is_not_a_finding()
    {
        var info = Assert.Single(Health(Outdated(VendorImportance.Recommended)).Findings);
        Assert.Equal(HealthSeverity.Info, info.Severity);
        Assert.Equal(DriverHealth.PenaltyBiosOutdated, info.Penalty);

        var upToDate = new FirmwareStatus { Bios = new BiosStatus { Vendor = "Dell", State = BiosState.UpToDate, InstalledVersion = "1.18.0", LatestVersion = "1.18.0" } };
        Assert.Empty(Health(upToDate).Findings);
        var unknown = new FirmwareStatus { Bios = new BiosStatus { Vendor = "Dell", State = BiosState.Unknown, InstalledVersion = "A12" } };
        Assert.Empty(Health(unknown).Findings);
    }

    [Fact]
    public void Bios_candidate_is_not_counted_twice()
    {
        var biosCandidate = new DriverCandidate { Id = "dell:BIOS118", Source = DriverSourceKind.Dell, Title = "Dell Latitude 7420/7520 System BIOS", Version = "1.18.0", Kind = CandidateKind.Firmware };
        var h = Health(Outdated(VendorImportance.Recommended, "dell:BIOS118"), new[] { biosCandidate });
        Assert.Equal(HealthCodes.BiosOutdated, Assert.Single(h.Findings).Code);
    }

    // ------------------------------------------------------------------ rapport

    private static ScanResult Scan(FirmwareStatus? firmware) => new()
    {
        System = new SystemInfo { Manufacturer = "Dell Inc.", BiosVersion = "1.12.0", BiosReleaseDate = new DateTime(2025, 2, 1) },
        Devices = new List<DeviceInfo> { Thunderbolt() },
        Candidates = new List<DriverCandidate>(),
        Firmware = firmware,
        Health = Health(firmware),
    };

    [Fact]
    public void Scan_report_publishes_the_firmware_object_without_changing_the_health_contract()
    {
        var firmware = Outdated(VendorImportance.Critical);
        firmware.Components.Add(new FirmwareComponentInfo { Kind = "thunderbolt", Title = "Intel Thunderbolt 4 Controller Firmware Update", Version = "37.0", Importance = VendorImportance.Recommended, Devices = { "Contrôleur" } });
        using var doc = JsonDocument.Parse(ScanReport.From(Scan(firmware)).ToJson());

        var bios = doc.RootElement.GetProperty("firmware").GetProperty("bios");
        Assert.Equal("outdated", bios.GetProperty("state").GetString());
        Assert.Equal("critical", bios.GetProperty("importance").GetString());
        Assert.Equal("1.12.0", bios.GetProperty("installedVersion").GetString());
        Assert.Equal("1.18.0", bios.GetProperty("latestVersion").GetString());
        Assert.StartsWith("https://www.dell.com/", bios.GetProperty("infoUrl").GetString());
        Assert.False(bios.TryGetProperty("candidateIds", out _));
        Assert.Equal("recommended", doc.RootElement.GetProperty("firmware").GetProperty("components")[0].GetProperty("importance").GetString());
        Assert.Equal("2025-02-01T00:00:00", doc.RootElement.GetProperty("system").GetProperty("biosReleaseDate").GetString());

        var health = doc.RootElement.GetProperty("health");
        Assert.Equal(new[] { "score", "grade", "findings" }, health.EnumerateObject().Select(p => p.Name));
        var finding = health.GetProperty("findings")[0];
        Assert.Equal("bios-outdated", finding.GetProperty("code").GetString());
        Assert.Equal("warning", finding.GetProperty("severity").GetString());
        Assert.Equal(new[] { "severity", "code", "title", "detail" }, finding.EnumerateObject().Select(p => p.Name)); // constat global : sans deviceId
    }

    [Fact]
    public void Firmware_survives_a_round_trip_and_old_reports_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp-firmware-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ScanReport.From(Scan(Outdated(VendorImportance.Critical))).ToJson());
            var reloaded = ScanReport.Load(path);
            Assert.Equal(BiosState.Outdated, reloaded.Firmware!.Bios!.State);
            Assert.Equal(VendorImportance.Critical, reloaded.Firmware.Bios.Importance);

            // Rapport d'une version précédente : ni « firmware » ni « biosReleaseDate ».
            File.WriteAllText(path, """
                { "schema": 2, "generatedBy": "SpokaPilote", "appVersion": "3.0.0", "timestamp": "2026-11-01T10:00:00",
                  "system": { "computerName": "PC-01", "manufacturer": "Dell Inc.", "model": "Latitude 7420", "biosVersion": "1.12.0" },
                  "sources": [ "Dell" ], "candidates": [],
                  "health": { "score": 100, "grade": "A", "findings": [] },
                  "devices": [ { "instanceId": "PCI\\VEN_8086&DEV_9A1B\\1", "name": "Contrôleur", "isPresent": true } ] }
                """);
            var old = ScanReport.Load(path);
            Assert.Null(old.Firmware);
            Assert.Null(old.System.BiosReleaseDate);
            Assert.Equal("1.12.0", old.System.BiosVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scan_without_oem_catalog_has_no_firmware_object()
    {
        using var doc = JsonDocument.Parse(ScanReport.From(Scan(null)).ToJson());
        Assert.False(doc.RootElement.TryGetProperty("firmware", out _));
        Assert.Contains("non vérifié", FirmwareStatus.Describe(null, new SystemInfo { Manufacturer = "Gigabyte Technology Co., Ltd.", BiosVersion = "F4" }));
    }

    [Fact]
    public void Html_report_shows_the_bios_card_with_official_links_and_warning()
    {
        var html = ReportWriter.ToHtml(Scan(Outdated(VendorImportance.Critical)));
        Assert.Contains("<h2>BIOS et firmwares</h2>", html);
        Assert.Contains("1.18.0", html);
        Assert.Contains("<a href=\"https://www.dell.com/support/home/en-us/drivers/DriversDetails?driverId=BIOS118\"", html);
        Assert.Contains("BitLocker", html);
        Assert.Contains("bios-outdated", ReportWriter.ToJson(Scan(Outdated(VendorImportance.Critical))));
    }

    // ------------------------------------------------------------------ divers

    [Theory]
    [InlineData("https://www.dell.com/support/home/drivers/driversdetails?driverid=X", true)]
    [InlineData("https://downloads.dell.com/FOLDER/1/x.exe", true)]
    [InlineData("https://ftp.hp.com/pub/softpaq/sp1/sp1.html", true)]
    [InlineData("https://download.lenovo.com/pccbbs/mobiles/x.txt", true)]
    [InlineData("http://www.dell.com/support", false)]
    [InlineData("https://www.dell.com.evil.example/support", false)]
    [InlineData("https://drivers.example.com/bios.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Only_https_official_links_are_published(string? url, bool expected)
        => Assert.Equal(expected, TrustPolicy.OfficialLink(url) is not null);

    [Theory]
    [InlineData("20230622000000.000000+000", 2023, 6, 22)]
    [InlineData("20210618000000.000000+000", 2021, 6, 18)]
    public void Bios_release_date_is_read_from_wmi(string cim, int y, int m, int d)
        => Assert.Equal(new DateTime(y, m, d), SystemInfoProvider.ParseCimDate(cim));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000000000.000000+000")]
    public void Invalid_bios_release_date_is_ignored(string? cim) => Assert.Null(SystemInfoProvider.ParseCimDate(cim));
}
