using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using SpokaPilote.Core.Sources.Oem;

namespace SpokaPilote.Core.Tests;

public class OemSourceTests
{
    private static ScanContext Context(string manufacturer, bool firmware = false, params DeviceInfo[] devices) => new()
    {
        System = new SystemInfo
        {
            Manufacturer = manufacturer,
            Model = "20XWCTO1WW",
            SystemSku = "0A6B",
            BaseBoardProduct = "8549",
            OsVersion = new Version(10, 0, 26100),
            OsDisplayVersion = "24H2",
            Architecture = "amd64",
        },
        Devices = devices,
        Settings = new Settings { IncludeBiosAndFirmware = firmware },
        Options = new ScanOptions(),
    };

    private static DeviceInfo Nic(string version, int error = 0) => new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028&REV_03\3&1",
        Name = "Intel(R) Ethernet Controller I225-V",
        HardwareIds = new[] { @"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028&REV_03", @"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028", @"PCI\VEN_8086&DEV_15F3" },
        DriverVersion = version,
        ErrorCode = error,
    };

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Dell_keeps_latest_release_for_this_model_and_maps_devices()
    {
        var result = DellSource.Parse(Fixture("dell-catalog.xml"), Context("Dell Inc.", false, Nic("12.19.1.1")));
        var c = Assert.Single(result);
        Assert.Equal("Intel Ethernet Driver", c.Title);
        Assert.Equal("12.19.2.60 (A01)", c.Version);
        Assert.Equal("https://downloads.dell.com/FOLDER1/1/Intel-Ethernet_AAA01_WIN64_12.19.2.60_A01.EXE", c.DownloadUrl);
        Assert.Equal("6ca1a71bbaa8a53f3509858d3b39033f", c.Md5);
        Assert.Contains(@"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028&REV_03\3&1", c.TargetDeviceIds);
        Assert.Contains(3, c.NotApplicableExitCodes);
    }

    [Fact]
    public void Dell_skips_drivers_already_up_to_date()
        => Assert.Empty(DellSource.Parse(Fixture("dell-catalog.xml"), Context("Dell Inc.", false, Nic("12.19.2.60"))));

    [Fact]
    public void Dell_bios_only_when_firmware_enabled()
    {
        var result = DellSource.Parse(Fixture("dell-catalog.xml"), Context("Dell Inc.", true, Nic("12.19.2.60")));
        var bios = Assert.Single(result);
        Assert.Equal(CandidateKind.Firmware, bios.Kind);
    }

    [Fact]
    public void Hp_maps_softpaqs_to_devices_and_filters_software()
    {
        var hpButton = new DeviceInfo { InstanceId = @"ACPI\HPQ6001\0", Name = "HP Wireless Button", HardwareIds = new[] { @"ACPI\HPQ6001", "*HPQ6001" }, DriverVersion = "2.1.17.7" };
        var result = HpSource.Parse(Fixture("hp-reference.xml"), Context("HP", false, Nic("12.19.1.1", error: 28), hpButton));

        var nic = Assert.Single(result, c => c.SourceItemId == "sp170001");
        Assert.Equal(CandidateKind.MissingDriver, nic.Kind);
        Assert.Equal("https://ftp.hp.com/pub/softpaq/sp170001-170500/sp170001.exe", nic.DownloadUrl);
        Assert.Equal("\"Setup.exe\" -s", nic.SilentCommand);
        Assert.NotNull(nic.Sha256);

        Assert.DoesNotContain(result, c => c.SourceItemId == "sp170002"); // bouton : déjà à jour
        Assert.DoesNotContain(result, c => c.SourceItemId == "sp170003"); // logiciel : exclu
        Assert.Equal(CandidateKind.Optional, Assert.Single(result, c => c.SourceItemId == "sp170004").Kind); // pas d'ID matériel
    }

    [Fact]
    public void Lenovo_descriptor_maps_pnp_ids_and_builds_commands()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package name="INTEL_LAN" id="n32ul16w" version="12.19.2.60">
              <Title default="EN"><Desc id="EN">Intel Ethernet Driver - 11</Desc></Title>
              <Vendor>Intel</Vendor>
              <ReleaseDate>2026-03-10</ReleaseDate>
              <Reboot type="3" />
              <PackageType type="2" />
              <ExtractCommand>n32ul16w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT="YES"</ExtractCommand>
              <Install rc="0,3010" type="cmd" default="EN"><Cmdline id="EN">%PACKAGEPATH%\setup.exe -s</Cmdline></Install>
              <DetectInstall><_Driver><HardwareID><![CDATA[PCI\VEN_8086&DEV_15F3]]></HardwareID><Version>12.19.2.60^</Version></_Driver></DetectInstall>
              <Dependencies><_PnPID><![CDATA[PCI\VEN_8086&DEV_15F3*]]></_PnPID></Dependencies>
              <Files><Installer><File><Name>n32ul16w.exe</Name><CRC>C030171D1BB5E65B8F9B9BCD0234BA0D805F2635F6E96A5951B78064347F9A90</CRC><Size>1000</Size></File></Installer></Files>
            </Package>
            """;
        var uri = new Uri("https://download.lenovo.com/pccbbs/mobiles/n32ul16w_2_.xml");

        var outdated = LenovoSource.ParseDescriptor(xml, uri, "Networking", Context("LENOVO", false, Nic("12.19.1.1")));
        Assert.NotNull(outdated);
        Assert.Equal("https://download.lenovo.com/pccbbs/mobiles/n32ul16w.exe", outdated!.DownloadUrl);
        Assert.Equal(new[] { 0, 3010 }, outdated.SuccessExitCodes);
        Assert.Equal(64, outdated.Sha256!.Length);
        Assert.Single(outdated.TargetDeviceIds);
        Assert.True(outdated.RebootRequired);

        Assert.Null(LenovoSource.ParseDescriptor(xml, uri, "Networking", Context("LENOVO", false, Nic("12.19.2.60")))); // à jour
        Assert.Null(LenovoSource.ParseDescriptor(xml, uri, "Networking", Context("LENOVO"))); // matériel absent
    }

    [Theory]
    [InlineData("Dell Inc.", OemVendor.Dell)]
    [InlineData("HP", OemVendor.HP)]
    [InlineData("Hewlett-Packard", OemVendor.HP)]
    [InlineData("LENOVO", OemVendor.Lenovo)]
    [InlineData("Gigabyte Technology Co., Ltd.", OemVendor.Other)]
    [InlineData("", OemVendor.Unknown)]
    public void Detects_oem_vendor(string manufacturer, OemVendor expected)
        => Assert.Equal(expected, OemVendorDetector.Detect(manufacturer));
}
