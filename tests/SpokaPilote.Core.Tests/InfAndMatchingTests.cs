using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Tests;

public class InfAndMatchingTests
{
    private const string SampleInf = """
        ; Pilote réseau d'exemple
        [Version]
        Signature   = "$WINDOWS NT$"
        Class       = Net
        ClassGUID   = {4d36e972-e325-11ce-bfc1-08002be10318}
        Provider    = %Intel%
        CatalogFile = e1d.cat
        DriverVer   = 06/21/2024,12.19.2.45

        [Manufacturer]
        %Intel% = Intel, NTamd64.10.0...16299, NTarm64

        [Intel.NTamd64.10.0...16299]
        %E15F3NC.DeviceDesc% = E15F3.ndi, PCI\VEN_8086&DEV_15F3&SUBSYS_00008086, \
                               PCI\VEN_8086&DEV_15F3
        %Generic%            = Gen.ndi, PCI\VEN_8086&DEV_1234

        [Intel.NTarm64]
        %Arm.DeviceDesc% = Arm.ndi, PCI\VEN_8086&DEV_9999

        [Intel]
        %Old.DeviceDesc% = Old.ndi, PCI\VEN_8086&DEV_0001

        [Strings]
        Intel = "Intel"
        E15F3NC.DeviceDesc = "Intel(R) Ethernet Controller I225-V"
        Generic = "Intel ; pas un commentaire"
        Arm.DeviceDesc = "ARM device"
        Old.DeviceDesc = "x86 only"
        """;

    [Fact]
    public void Parses_version_section_and_strings()
    {
        var inf = InfParser.ParseText(SampleInf);
        Assert.Equal("Net", inf.Class);
        Assert.Equal("Intel", inf.Provider);
        Assert.Equal("e1d.cat", inf.CatalogFile);
        Assert.Equal("12.19.2.45", inf.DriverVersion);
        Assert.Equal(new DateTime(2024, 6, 21), inf.DriverDate);
    }

    [Fact]
    public void Parses_models_with_line_continuation_and_quoted_semicolon()
    {
        var inf = InfParser.ParseText(SampleInf);
        var ids = inf.Models.Select(m => m.HardwareId).ToList();
        Assert.Contains(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086", ids);
        Assert.Contains(@"PCI\VEN_8086&DEV_15F3", ids); // ligne continuée par « \ »
        Assert.Contains(inf.Models, m => m.Description == "Intel ; pas un commentaire");
        Assert.Equal("Intel(R) Ethernet Controller I225-V",
            inf.Models.First(m => m.HardwareId.EndsWith("SUBSYS_00008086")).Description);
    }

    [Theory]
    [InlineData("NTamd64.10.0...16299", "amd64", 26100, true)]
    [InlineData("NTamd64.10.0...16299", "amd64", 15063, false)] // build trop ancien
    [InlineData("NTarm64", "amd64", 26100, false)]               // mauvaise architecture
    [InlineData("NTarm64", "arm64", 26100, true)]
    [InlineData("", "amd64", 26100, false)]                      // non décoré : x86 seulement
    [InlineData("", "x86", 19045, true)]
    [InlineData("NTamd64.6.3", "amd64", 26100, true)]            // Windows 8.1+ ⇒ OK sur 10
    [InlineData("NTamd64.11.0", "amd64", 26100, false)]          // version majeure future
    public void Decoration_applicability(string decoration, string arch, int build, bool expected)
        => Assert.Equal(expected, InfParser.DecorationApplies(decoration, arch, new Version(10, 0, build)));

    private static DeviceInfo Device(params string[] hw) => new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_03\4&1",
        Name = "Ethernet",
        HardwareIds = hw,
        CompatibleIds = new[] { @"PCI\VEN_8086&CC_020000", @"PCI\CC_0200" },
    };

    [Fact]
    public void Best_match_prefers_most_specific_hardware_id()
    {
        var device = Device(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_03", @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086", @"PCI\VEN_8086&DEV_15F3&REV_03", @"PCI\VEN_8086&DEV_15F3");
        var inf = InfParser.ParseText(SampleInf);
        var match = HardwareIdMatcher.BestMatch(device, inf, "amd64", new Version(10, 0, 26100));
        Assert.NotNull(match);
        Assert.Equal(1, match!.Rank);
        Assert.Equal(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086", match.MatchedId);
    }

    [Fact]
    public void No_match_for_other_architecture()
    {
        var device = Device(@"PCI\VEN_8086&DEV_9999");
        var inf = InfParser.ParseText(SampleInf);
        Assert.Null(HardwareIdMatcher.BestMatch(device, inf, "amd64", new Version(10, 0, 26100)));
        Assert.NotNull(HardwareIdMatcher.BestMatch(device, inf, "arm64", new Version(10, 0, 26100)));
    }

    [Fact]
    public void Compatible_ids_rank_after_hardware_ids()
    {
        var device = Device(@"PCI\VEN_8086&DEV_15F3");
        Assert.Equal(0, HardwareIdMatcher.RankFor(device, @"pci\ven_8086&dev_15f3"));
        Assert.Equal(HardwareIdMatcher.CompatibleIdOffset + 1, HardwareIdMatcher.RankFor(device, @"PCI\CC_0200"));
        Assert.Null(HardwareIdMatcher.RankFor(device, @"PCI\VEN_10EC&DEV_8168"));
    }

    [Fact]
    public void Generic_compatible_ids_are_detected()
    {
        Assert.True(HardwareIdMatcher.IsGenericCompatibleId(@"PCI\CC_0200"));
        Assert.True(HardwareIdMatcher.IsGenericCompatibleId(@"USB\Class_03&SubClass_01"));
        Assert.False(HardwareIdMatcher.IsGenericCompatibleId(@"PCI\VEN_8086&DEV_15F3"));
    }

    [Fact]
    public void Catalog_queries_go_from_specific_to_broad()
    {
        var device = Device(@"PCI\VEN_8086&DEV_43C8&SUBSYS_A1941458&REV_11", @"PCI\VEN_8086&DEV_43C8&SUBSYS_A1941458");
        var q = HardwareIdMatcher.CatalogQueries(device);
        Assert.Equal(@"PCI\VEN_8086&DEV_43C8&SUBSYS_A1941458", q[0]);
        Assert.Equal("VEN_8086&DEV_43C8", q[^1]);
        Assert.Equal(2, q.Count); // doublon sans REV supprimé
    }

    [Fact]
    public void Catalog_queries_for_usb_and_hdaudio()
    {
        var usb = Device(@"USB\VID_0B95&PID_1790&REV_0200", @"USB\VID_0B95&PID_1790");
        Assert.Contains("VID_0B95&PID_1790", HardwareIdMatcher.CatalogQueries(usb));
        var audio = Device(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A194&REV_1001");
        Assert.Contains("VEN_10EC&DEV_0897", HardwareIdMatcher.CatalogQueries(audio));
    }

    [Theory]
    [InlineData("31.0.101.4502", "31.0.101.4314", 1)]
    [InlineData("2.1.5.10", "2.1.5.10", 0)]
    [InlineData("10.29.0.12635", "10.0.26100.1", 1)]
    [InlineData("1.2", "1.2.0.0", 0)]
    [InlineData("4.20.2.0 (A05)", "4.20.1.9", 1)]
    public void Driver_version_comparison(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(DriverVersion.Compare(a, b)!.Value));

    [Fact]
    public void Driver_version_incomparable_returns_null()
    {
        Assert.Null(DriverVersion.Compare("n/a", "1.0"));
        Assert.Null(DriverVersion.Compare(null, "1.0"));
    }
}
