using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using SpokaPilote.Core.Sources.Gpu;

namespace SpokaPilote.Core.Tests;

public class GpuSourcesTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static SystemInfo Win11(string arch = "amd64") => new()
    {
        Manufacturer = "Gigabyte Technology Co., Ltd.",
        Model = "Z590 GAMING X",
        OsVersion = new Version(10, 0, 26200),
        Architecture = arch,
    };

    private static SystemInfo Win10() => new() { OsVersion = new Version(10, 0, 19045), Architecture = "amd64" };

    private static DeviceInfo Gpu(string vendor, string device, string name, string? version, int error = 0,
        string cls = "Display", string? provider = null, string? inf = "oem12.inf") => new()
    {
        InstanceId = $@"PCI\VEN_{vendor}&DEV_{device}&SUBSYS_53221462&REV_A1\4&1",
        Name = name,
        ClassName = cls,
        HardwareIds = new[]
        {
            $@"PCI\VEN_{vendor}&DEV_{device}&SUBSYS_53221462&REV_A1",
            $@"PCI\VEN_{vendor}&DEV_{device}&SUBSYS_53221462",
            $@"PCI\VEN_{vendor}&DEV_{device}&CC_030000",
            $@"PCI\VEN_{vendor}&DEV_{device}&CC_0300",
        },
        CompatibleIds = new[] { $@"PCI\VEN_{vendor}&CC_030000", @"PCI\CC_030000", @"PCI\CC_0300" },
        DriverVersion = version,
        DriverProvider = provider ?? (vendor == GpuVendorIds.Nvidia ? "NVIDIA" : vendor == GpuVendorIds.Intel ? "Intel Corporation" : "Advanced Micro Devices, Inc."),
        InfName = inf,
        ErrorCode = error,
    };

    private static DeviceInfo Rtx5070(string? version = "32.0.16.1088", int error = 0) =>
        Gpu(GpuVendorIds.Nvidia, "2F04", "NVIDIA GeForce RTX 5070", version, error);

    private static ScanContext Context(SystemInfo system, Settings? settings = null, params DeviceInfo[] devices) => new()
    {
        System = system,
        Devices = devices,
        Settings = settings ?? new Settings(),
        Options = new ScanOptions(),
    };

    // ------------------------------------------------------------------ conversion de versions NVIDIA

    [Theory]
    [InlineData("31.0.15.6094", "560.94")]
    [InlineData("32.0.16.1088", "610.88")]
    [InlineData("32.0.15.8180", "581.80")]
    [InlineData("30.0.14.7212", "472.12")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("31.0.15.3179", "531.79")]
    public void Nvidia_windows_version_maps_to_retail_version(string windows, string retail)
        => Assert.Equal(retail, NvidiaSource.FromWindowsVersion(windows));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("621.40")]
    [InlineData("32.0.16.12345")]
    public void Nvidia_windows_version_rejects_other_formats(string? value)
        => Assert.Null(NvidiaSource.FromWindowsVersion(value));

    [Theory]
    [InlineData("621.40", "16.2140")]
    [InlineData("560.94", "15.6094")]
    [InlineData("472.12", "14.7212")]
    public void Nvidia_retail_version_maps_to_windows_suffix(string retail, string suffix)
        => Assert.Equal(suffix, NvidiaSource.ToWindowsSuffix(retail));

    [Fact]
    public void Nvidia_version_label_shows_both_formats()
        => Assert.Equal("621.40 (Windows x.x.16.2140)", NvidiaSource.FormatVersion("621.40"));

    [Theory]
    [InlineData("621.40", "32.0.16.1088", true)]
    [InlineData("610.88", "32.0.16.1088", false)]
    [InlineData("610.62", "32.0.16.1088", false)]
    [InlineData("610.90", "32.0.16.1088", true)]
    [InlineData("560.94", "31.0.15.6094", false)]
    public void Nvidia_newer_comparison_uses_retail_versions(string candidate, string installed, bool newer)
        => Assert.Equal(newer, NvidiaSource.IsNewer(candidate, installed));

    // ------------------------------------------------------------------ analyse des réponses réelles

    [Fact]
    public void Nvidia_parses_studio_response()
    {
        var d = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!;
        Assert.Equal("621.40", d.Version);
        Assert.True(d.IsStudio);
        Assert.False(d.IsBeta);
        Assert.Equal("288692", d.Id);
        Assert.Equal(new DateTime(2026, 11, 24), d.Date);
        // Paquet standard (DownloadURLAdmin), pas la variante « -g » réservée à l'application NVIDIA.
        Assert.Equal("https://us.download.nvidia.com/Windows/621.40/621.40-desktop-win10-win11-64bit-international-nsd-dch-whql.exe", d.DownloadUrl);
        Assert.Equal("https://www.nvidia.com/en-us/drivers/details/288692/", d.DetailsUrl);
    }

    [Fact]
    public void Nvidia_parses_game_ready_response()
    {
        var d = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-gameready.json"))!;
        Assert.False(d.IsStudio);
        Assert.Equal("GeForce Game Ready Driver", d.Name);
        Assert.EndsWith("621.40-desktop-win10-win11-64bit-international-dch-whql.exe", d.DownloadUrl);
    }

    [Fact]
    public void Nvidia_unknown_card_returns_nothing()
        => Assert.Null(NvidiaSource.ParseLookup(Fixture("nvidia-notfound.json")));

    [Fact]
    public void Nvidia_service_selects_notebook_professional_and_legacy_packages()
    {
        Assert.Contains("-notebook-", NvidiaSource.ParseLookup(Fixture("nvidia-28E0-notebook.json"))!.DownloadUrl);
        var pro = NvidiaSource.ParseLookup(Fixture("nvidia-27B2-rtx-pro.json"))!;
        Assert.Contains("/Quadro_Certified/", pro.DownloadUrl);
        var legacy = NvidiaSource.ParseLookup(Fixture("nvidia-1380-legacy.json"))!;
        Assert.Equal("582.78", legacy.Version);
    }

    [Fact]
    public void Nvidia_titles_name_the_branch()
    {
        var adapters = new[] { new GpuAdapter(Rtx5070("32.0.15.8180"), GpuVendorIds.Nvidia, "2F04") };
        string Title(string fixture) => NvidiaSource.BuildCandidate(NvidiaSource.ParseLookup(Fixture(fixture))!, adapters, null)!.Title;
        Assert.Equal("Pilote graphique NVIDIA Studio", Title("nvidia-2F04-studio.json"));
        Assert.Equal("Pilote graphique NVIDIA Game Ready", Title("nvidia-2F04-gameready.json"));
        Assert.Equal("Pilote graphique NVIDIA Game Ready (portable)", Title("nvidia-28E0-notebook.json"));
        Assert.Contains("RTX Enterprise", Title("nvidia-27B2-rtx-pro.json"));
        Assert.Contains("branche héritée", Title("nvidia-1380-legacy.json"));
    }

    // ------------------------------------------------------------------ sélection et candidats

    [Fact]
    public void Nvidia_branch_attempts_prefer_studio_then_fall_back()
    {
        Assert.Equal(new[] { true, false }, NvidiaSource.BranchAttempts(NvidiaSource.ParseBranch(null)));
        Assert.Equal(new[] { true, false }, NvidiaSource.BranchAttempts(NvidiaSource.ParseBranch("studio")));
        Assert.Equal(new[] { false }, NvidiaSource.BranchAttempts(NvidiaSource.ParseBranch("GameReady")));
        Assert.Equal(new[] { false }, NvidiaSource.BranchAttempts(NvidiaSource.ParseBranch("game-ready")));
    }

    [Fact]
    public void Nvidia_lookup_url_describes_the_system()
    {
        var url = Uri.UnescapeDataString(NvidiaSource.BuildLookupUrl("2f04", Win11(), studio: true));
        Assert.StartsWith("https://gfwsl.geforce.com/", url);
        Assert.Contains("\"dIDa\":[\"2F04_10DE\"]", url);
        Assert.Contains("\"osB\":\"26200\"", url);
        Assert.Contains("\"is6\":\"1\"", url);
        Assert.Contains("\"dch\":\"1\"", url);
        Assert.Contains("\"isCRD\":\"1\"", url);
        Assert.Contains("\"gIsB\":\"0\"", url);
        Assert.Contains("\"isCRD\":\"0\"", Uri.UnescapeDataString(NvidiaSource.BuildLookupUrl("2F04", Win10(), studio: false)));
    }

    [Fact]
    public void Nvidia_candidate_is_a_silent_driver_only_vendor_installer()
    {
        var device = Rtx5070();
        var driver = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!;
        var c = NvidiaSource.BuildCandidate(driver, new[] { new GpuAdapter(device, "10DE", "2F04") }, 1_035_810_304, "C060F5AF5042EDC2D7FF3F16F99E90B3")!;

        Assert.Equal(DriverSourceKind.Nvidia, c.Source);
        Assert.Equal(DriverPackageKind.VendorInstaller, c.PackageKind);
        Assert.Equal(CandidateKind.Update, c.Kind);
        Assert.Equal("621.40 (Windows x.x.16.2140)", c.Version);
        Assert.Equal("987,8 Mo", c.SizeLabel.Replace('.', ','));
        Assert.Equal("C060F5AF5042EDC2D7FF3F16F99E90B3", c.Md5);
        Assert.True(c.ExtractToTempFolder);
        Assert.Contains("-nr", c.ExtractCommand);
        Assert.Contains("-InstallPath=\"%PACKAGEPATH%\"", c.ExtractCommand);
        Assert.StartsWith("\"%PACKAGEPATH%\\setup.exe\" -s -n Display.Driver", c.SilentCommand);
        Assert.DoesNotContain("NvApp", c.SilentCommand);
        Assert.Equal(new[] { 0 }, c.SuccessExitCodes);
        Assert.Equal(new[] { 1 }, c.RebootExitCodes);
        Assert.Contains(device.InstanceId, c.TargetDeviceIds);
        Assert.Equal(@"PCI\VEN_10DE&DEV_2F04", c.MatchedHardwareId);
        Assert.Equal(0, c.MatchRank);
        Assert.Equal("nvidia:288692", c.Id);
    }

    [Fact]
    public void Nvidia_not_proposed_when_installed_driver_is_current_or_newer()
    {
        var driver = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!;
        Assert.Null(NvidiaSource.BuildCandidate(driver, new[] { new GpuAdapter(Rtx5070("32.0.16.2140"), "10DE", "2F04") }, null));
        Assert.Null(NvidiaSource.BuildCandidate(driver, new[] { new GpuAdapter(Rtx5070("32.0.16.2512"), "10DE", "2F04") }, null));
    }

    [Fact]
    public void Nvidia_missing_driver_when_adapter_in_error_or_on_basic_display()
    {
        var driver = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!;
        var inError = NvidiaSource.BuildCandidate(driver, new[] { new GpuAdapter(Rtx5070("32.0.16.2140", error: 43), "10DE", "2F04") }, null)!;
        Assert.Equal(CandidateKind.MissingDriver, inError.Kind);

        var basic = Gpu("10DE", "2F04", "Carte graphique de base Microsoft", "10.0.26100.1", provider: "Microsoft", inf: "display.inf");
        var onBasic = NvidiaSource.BuildCandidate(driver, new[] { new GpuAdapter(basic, "10DE", "2F04") }, null)!;
        Assert.Equal(CandidateKind.MissingDriver, onBasic.Kind);
    }

    [Fact]
    public void Nvidia_filters_os_architecture_and_package_type()
    {
        var driver = NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!;
        Assert.True(NvidiaSource.IsAcceptable(driver, Win11(), out _));
        Assert.True(NvidiaSource.IsAcceptable(driver, Win10(), out _));
        Assert.False(NvidiaSource.IsAcceptable(driver with { DownloadUrl = "https://us.download.nvidia.com/Windows/1/1-desktop-win11-64bit-international-dch-whql.exe" }, Win10(), out var why));
        Assert.Contains("Windows 10", why);
        Assert.False(NvidiaSource.IsAcceptable(driver with { DownloadUrl = driver.DownloadUrl.Replace("64bit", "32bit") }, Win11(), out _));
        Assert.False(NvidiaSource.IsAcceptable(driver with { DownloadUrl = driver.DownloadUrl.Replace("-dch", "") }, Win11(), out _));
        Assert.False(NvidiaSource.IsAcceptable(driver with { DownloadUrl = driver.DownloadUrl.Replace("https://", "http://") }, Win11(), out _));
        Assert.False(NvidiaSource.IsAcceptable(driver with { IsBeta = true }, Win11(), out _));

        Assert.True(NvidiaSource.IsSupportedSystem(Win11()));
        Assert.False(NvidiaSource.IsSupportedSystem(Win11("arm64")));
        Assert.False(NvidiaSource.IsSupportedSystem(Win11("x86")));
    }

    [Fact]
    public void Nvidia_download_hosts_are_trusted()
    {
        var trust = new TrustPolicy(new Settings());
        Assert.True(trust.IsTrustedHost(new Uri(NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!.DownloadUrl)));
        Assert.True(trust.IsTrustedHost(new Uri("https://international.download.nvidia.com/Windows/621.40/x.exe")));
        Assert.False(trust.IsTrustedHost(new Uri("https://international-gfe.download.nvidia.com/Windows/621.40/x-g.exe")));
        Assert.True(trust.IsTrustedPublisher("CN=NVIDIA Corporation, OU=2008B9F, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US"));
    }

    [Theory]
    [InlineData("\"c060f5af5042edc2d7ff3f16f99e90b3:1795525980.689527\"", "AkamaiNetStorage", "C060F5AF5042EDC2D7FF3F16F99E90B3")]
    [InlineData("\"c060f5af5042edc2d7ff3f16f99e90b3:1795525980\"", "AkamaiNetStorage", "C060F5AF5042EDC2D7FF3F16F99E90B3")]
    [InlineData("\"c060f5af5042edc2d7ff3f16f99e90b3:1795525980.689527\"", "nginx", null)]
    [InlineData("W/\"abc\"", "AkamaiNetStorage", null)]
    [InlineData(null, "AkamaiNetStorage", null)]
    public void Nvidia_md5_only_from_netstorage_etag(string? etag, string server, string? expected)
        => Assert.Equal(expected, NvidiaSource.Md5FromETag(etag, server));

    // ------------------------------------------------------------------ activation et détection des adaptateurs

    [Fact]
    public void Gpu_adapters_are_found_by_pci_vendor_and_display_class()
    {
        var devices = new[]
        {
            Rtx5070(),
            Gpu("8086", "4680", "Intel(R) UHD Graphics 770", "32.0.101.6881"),
            new DeviceInfo { InstanceId = @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A3", Name = "NVIDIA High Definition Audio", ClassName = "MEDIA",
                HardwareIds = new[] { @"PCI\VEN_10DE&DEV_22BC&SUBSYS_53221462" }, CompatibleIds = new[] { @"PCI\CC_0403" } }, // audio HDMI : ignoré
            new DeviceInfo { InstanceId = @"PCI\VEN_1002&DEV_73BF\1", Name = "Contrôleur vidéo", ErrorCode = 28,
                HardwareIds = new[] { @"PCI\VEN_1002&DEV_73BF" }, CompatibleIds = new[] { @"PCI\CC_030000" } }, // sans pilote
            new DeviceInfo { InstanceId = @"PCI\VEN_10DE&DEV_2F04\absent", Name = "Ancienne carte", ClassName = "Display", IsPresent = false,
                HardwareIds = new[] { @"PCI\VEN_10DE&DEV_2F04" } },
        };
        var nvidia = Assert.Single(GpuAdapters.Find(devices, GpuVendorIds.Nvidia));
        Assert.Equal("2F04", nvidia.DeviceId);
        Assert.Single(GpuAdapters.Find(devices, GpuVendorIds.Intel));
        var amd = Assert.Single(GpuAdapters.Find(devices, GpuVendorIds.Amd));
        Assert.True(amd.NeedsDriver);
    }

    [Fact]
    public void Gpu_sources_enabled_only_with_setting_and_matching_adapter()
    {
        var nvidia = new NvidiaSource();
        Assert.True(nvidia.IsEnabled(Context(Win11(), null, Rtx5070())));
        Assert.False(nvidia.IsEnabled(Context(Win11(), new Settings { UseGpuVendorSources = false }, Rtx5070())));
        Assert.False(nvidia.IsEnabled(Context(Win11("arm64"), null, Rtx5070())));
        Assert.False(nvidia.IsEnabled(Context(Win11(), null, Gpu("8086", "4680", "Intel(R) UHD Graphics 770", "32.0.101.6881"))));
        Assert.True(new IntelGraphicsSource().IsEnabled(Context(Win11(), null, Gpu("8086", "4680", "Intel(R) UHD Graphics 770", "32.0.101.6881"))));
        Assert.False(new AmdSource().IsEnabled(Context(Win11(), null, Rtx5070())));
    }

    [Fact]
    public void Engine_includes_gpu_sources_with_oem_priority()
    {
        Assert.Equal(DriverScanEngine.Priority(DriverSourceKind.Dell), DriverScanEngine.Priority(DriverSourceKind.Nvidia));
        Assert.True(DriverScanEngine.Priority(DriverSourceKind.Nvidia) < DriverScanEngine.Priority(DriverSourceKind.WindowsUpdate));
    }

    [Fact]
    public void Nvidia_candidate_takes_precedence_over_windows_update_for_the_card()
    {
        var device = Rtx5070("32.0.15.8180");
        var nv = NvidiaSource.BuildCandidate(NvidiaSource.ParseLookup(Fixture("nvidia-2F04-studio.json"))!, new[] { new GpuAdapter(device, "10DE", "2F04") }, null)!;
        var wu = new DriverCandidate { Id = "wu:1", Source = DriverSourceKind.WindowsUpdate, Title = "NVIDIA - Display - 32.0.15.8180" };
        wu.TargetDeviceIds.Add(device.InstanceId);
        var merged = DriverScanEngine.Merge(new[] { wu, nv });
        Assert.Equal("nvidia:288692", Assert.Single(merged).Id);
    }

    // ------------------------------------------------------------------ Intel et AMD : candidats informatifs

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Current")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Current")]
    [InlineData("Intel(R) UHD Graphics 770", "Current")]
    [InlineData("Intel(R) Graphics", "Current")]
    [InlineData("Intel(R) UHD Graphics 630", "Legacy")]
    [InlineData("Intel(R) HD Graphics 530", "Legacy")]
    [InlineData("Intel(R) Iris(R) Plus Graphics 655", "Legacy")]
    [InlineData("Intel(R) UHD Graphics", "Unknown")]
    public void Intel_family_selects_the_official_page(string name, string family)
        => Assert.Equal(family, IntelGraphicsSource.Classify(name).ToString());

    [Fact]
    public void Intel_candidate_is_informational_and_never_targets_devices()
    {
        var adapters = GpuAdapters.Find(new[] { Gpu("8086", "4680", "Intel(R) UHD Graphics 770", "32.0.101.6881") }, GpuVendorIds.Intel);
        var c = Assert.Single(IntelGraphicsSource.BuildCandidates(adapters, Win11()));
        Assert.True(c.IsInformational);
        Assert.Equal(CandidateKind.Optional, c.Kind);
        Assert.Null(c.DownloadUrl);
        Assert.Empty(c.TargetDeviceIds); // ne masque pas Windows Update / catalogue Microsoft lors de la fusion
        Assert.Equal(IntelGraphicsSource.ArcPage, c.InfoUrl);
        Assert.Contains("32.0.101.6881", c.Title);
        Assert.StartsWith("https://www.intel.com/", c.InfoUrl);
    }

    [Fact]
    public void Amd_candidate_is_informational_with_pro_edition_for_workstation_cards()
    {
        var adapters = GpuAdapters.Find(new[]
        {
            Gpu("1002", "747E", "AMD Radeon RX 7800 XT", "32.0.21030.2001"),
            Gpu("1002", "7448", "AMD Radeon PRO W7900", "31.0.24033.1003"),
        }, GpuVendorIds.Amd);
        var result = AmdSource.BuildCandidates(adapters, Win11());
        Assert.Equal(2, result.Count);
        Assert.All(result, c =>
        {
            Assert.True(c.IsInformational);
            Assert.Equal(CandidateKind.Optional, c.Kind);
            Assert.Null(c.DownloadUrl);
            Assert.Empty(c.TargetDeviceIds);
            Assert.StartsWith("https://www.amd.com/", c.InfoUrl);
        });
        Assert.Contains(result, c => c.Title.Contains("PRO Edition") && c.InfoUrl == AmdSource.ProPage);
        Assert.Contains(result, c => c.Title.Contains("Adrenalin") && c.InfoUrl == AmdSource.DriversPage);
        Assert.Empty(AmdSource.BuildCandidates(adapters, Win11("arm64")));
    }

    [Fact]
    public async Task Informational_candidates_are_skipped_by_the_installer()
    {
        var c = IntelGraphicsSource.BuildCandidates(
            GpuAdapters.Find(new[] { Gpu("8086", "4680", "Intel(R) UHD Graphics 770", "32.0.101.6881") }, GpuVendorIds.Intel), Win11())[0];
        var installer = new SpokaPilote.Core.Install.DriverInstaller(new Settings(), Win11());
        var outcome = Assert.Single(await installer.InstallAsync(new[] { c }, Array.Empty<DeviceInfo>(),
            new SpokaPilote.Core.Install.InstallOptions { DryRun = true }));
        Assert.Equal(SpokaPilote.Core.Install.InstallStatus.Skipped, outcome.Status);
        Assert.Contains("informatif", outcome.Message);
    }
}
