using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Tests;

public class DriverStoreCleanerTests
{
    private static int _next = 100;

    private static DriverStorePackage Pkg(string original, string version, DateTime? date = null, bool inUse = false,
        string provider = "Realtek", string cls = "MEDIA", string[]? hw = null, long size = 10_000_000, string? published = null,
        bool parsed = true) => new()
    {
        PublishedName = published ?? $"oem{Interlocked.Increment(ref _next)}.inf",
        OriginalName = original,
        Provider = provider,
        ClassName = cls,
        Version = version,
        Date = date ?? new DateTime(2024, 1, 1),
        SizeBytes = size,
        HardwareIds = hw ?? new[] { @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897" },
        InUse = inUse,
        UsedBy = inUse ? new[] { "Realtek(R) Audio" } : Array.Empty<string>(),
        InfParsed = parsed,
    };

    private static DriverStoreEntry Entry(DriverStoreAnalysis a, DriverStorePackage p) => a.Entries.Single(e => ReferenceEquals(e.Package, p));

    [Fact]
    public void Old_unused_version_replaced_by_a_newer_one_is_a_candidate()
    {
        var oldest = Pkg("hdxrt.inf", "6.0.9500.1", size: 30_000_000);
        var used = Pkg("hdxrt.inf", "6.0.9600.1", inUse: true);
        var analysis = DriverStoreCleaner.Analyze(new[] { oldest, used });
        var e = Entry(analysis, oldest);
        Assert.Equal(DriverStoreVerdict.Candidate, e.Verdict);
        Assert.Same(used, e.ReplacedBy);
        Assert.Equal(DriverStoreVerdict.InUse, Entry(analysis, used).Verdict);
        Assert.Equal(30_000_000, analysis.ReclaimableBytes);
        Assert.Single(analysis.Candidates);
    }

    [Fact]
    public void Package_in_use_is_never_a_candidate_even_if_older()
    {
        var oldUsed = Pkg("hdxrt.inf", "6.0.9000.1", inUse: true);
        var newer = Pkg("hdxrt.inf", "6.0.9600.1");
        var analysis = DriverStoreCleaner.Analyze(new[] { oldUsed, newer });
        Assert.Equal(DriverStoreVerdict.InUse, Entry(analysis, oldUsed).Verdict);
        Assert.Equal(DriverStoreVerdict.Newest, Entry(analysis, newer).Verdict);
        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public void Only_version_of_a_driver_is_kept_even_if_unused()
    {
        var only = Pkg("ch341ser.inf", "3.9.2024.9", provider: "wch.cn", cls: "Ports");
        var analysis = DriverStoreCleaner.Analyze(new[] { only });
        Assert.Equal(DriverStoreVerdict.OnlyVersion, Entry(analysis, only).Verdict);
        Assert.Equal(0, analysis.ReclaimableBytes);
    }

    [Fact]
    public void Same_inf_name_from_another_provider_is_a_different_driver()
    {
        var a = Pkg("usbser.inf", "1.0.0.0", provider: "Vendeur A");
        var b = Pkg("usbser.inf", "2.0.0.0", provider: "Vendeur B");
        var analysis = DriverStoreCleaner.Analyze(new[] { a, b });
        Assert.All(analysis.Entries, e => Assert.Equal(DriverStoreVerdict.OnlyVersion, e.Verdict));
    }

    [Fact]
    public void Newest_unused_version_is_kept()
    {
        var used = Pkg("hdxrt.inf", "6.0.9500.1", inUse: true);
        var newest = Pkg("hdxrt.inf", "6.0.9600.1");
        Assert.Equal(DriverStoreVerdict.Newest, Entry(DriverStoreCleaner.Analyze(new[] { used, newest }), newest).Verdict);
    }

    [Fact]
    public void Version_not_fully_covered_by_newer_ones_is_kept()
    {
        var old = Pkg("netwtw.inf", "22.0.0.1", hw: new[] { @"PCI\VEN_8086&DEV_2723", @"PCI\VEN_8086&DEV_A0F0" }, cls: "Bluetooth2");
        var newer = Pkg("netwtw.inf", "23.0.0.1", hw: new[] { @"PCI\VEN_8086&DEV_2723" }, inUse: true, cls: "Bluetooth2");
        var e = Entry(DriverStoreCleaner.Analyze(new[] { old, newer }), old);
        Assert.Equal(DriverStoreVerdict.NotReplaced, e.Verdict);
    }

    [Fact]
    public void Same_version_and_date_twice_is_never_removed()
    {
        var a = Pkg("hdxrt.inf", "6.0.9600.1");
        var b = Pkg("hdxrt.inf", "6.0.9600.1");
        var analysis = DriverStoreCleaner.Analyze(new[] { a, b });
        Assert.All(analysis.Entries, e => Assert.Equal(DriverStoreVerdict.Newest, e.Verdict));
    }

    [Fact]
    public void Same_version_with_a_more_recent_date_replaces_the_older_package()
    {
        var a = Pkg("hdxrt.inf", "6.0.9600.1", date: new DateTime(2024, 1, 1));
        var b = Pkg("hdxrt.inf", "6.0.9600.1", date: new DateTime(2024, 6, 1), inUse: true);
        Assert.Equal(DriverStoreVerdict.Candidate, Entry(DriverStoreCleaner.Analyze(new[] { a, b }), a).Verdict);
    }

    [Fact]
    public void Printer_drivers_are_never_cleaned_automatically()
    {
        var old = Pkg("kyocera.inf", "8.0.0.0", provider: "Kyocera", cls: "Printer");
        var newer = Pkg("kyocera.inf", "9.0.0.0", provider: "Kyocera", cls: "Printer");
        Assert.Equal(DriverStoreVerdict.SensitiveClass, Entry(DriverStoreCleaner.Analyze(new[] { old, newer }), old).Verdict);
    }

    [Fact]
    public void Sensitive_class_keeps_the_previous_version_for_rollback()
    {
        var v1 = Pkg("nv_dispi.inf", "32.0.15.5000", provider: "NVIDIA", cls: "Display", hw: new[] { @"PCI\VEN_10DE&DEV_2531" });
        var v2 = Pkg("nv_dispi.inf", "32.0.15.6000", provider: "NVIDIA", cls: "Display", hw: new[] { @"PCI\VEN_10DE&DEV_2531" });
        var v3 = Pkg("nv_dispi.inf", "32.0.15.7000", provider: "NVIDIA", cls: "Display", hw: new[] { @"PCI\VEN_10DE&DEV_2531" }, inUse: true);
        var analysis = DriverStoreCleaner.Analyze(new[] { v1, v2, v3 });
        Assert.Equal(DriverStoreVerdict.Candidate, Entry(analysis, v1).Verdict);
        Assert.Equal(DriverStoreVerdict.KeptForRollback, Entry(analysis, v2).Verdict);
        Assert.Equal(DriverStoreVerdict.InUse, Entry(analysis, v3).Verdict);
    }

    [Fact]
    public void Ordinary_class_does_not_keep_the_previous_version()
    {
        var v1 = Pkg("hdxrt.inf", "6.0.9400.1");
        var v2 = Pkg("hdxrt.inf", "6.0.9500.1");
        var v3 = Pkg("hdxrt.inf", "6.0.9600.1", inUse: true);
        var analysis = DriverStoreCleaner.Analyze(new[] { v1, v2, v3 });
        Assert.Equal(2, analysis.Candidates.Count);
    }

    [Fact]
    public void Versions_needed_to_roll_back_a_spokapilote_intervention_are_kept()
    {
        var old = Pkg("hdxrt.inf", "6.0.9500.1");
        var added = Pkg("hdxrt.inf", "6.0.9600.1", inUse: true, published: "oem77.inf");
        var history = new[] { new Intervention { Id = "20261120-101010", AddedDriverPackages = { "oem77.inf" } } };
        var e = Entry(DriverStoreCleaner.Analyze(new[] { old, added }, history), old);
        Assert.Equal(DriverStoreVerdict.KeptForRollback, e.Verdict);
        Assert.Contains("20261120-101010", e.Reason);

        // Intervention déjà annulée : plus de raison de conserver l'ancienne version.
        history[0].RolledBackAt = DateTime.Now;
        Assert.Equal(DriverStoreVerdict.Candidate, Entry(DriverStoreCleaner.Analyze(new[] { old, added }, history), old).Verdict);
    }

    [Fact]
    public void Extension_infs_are_kept_when_their_usage_cannot_be_verified()
    {
        var old = Pkg("hdxrtext.inf", "6.0.9500.1", cls: "Extension");
        var newer = Pkg("hdxrtext.inf", "6.0.9600.1", cls: "Extension");
        Assert.Equal(DriverStoreVerdict.SensitiveClass, Entry(DriverStoreCleaner.Analyze(new[] { old, newer }, extensionUsageVerified: false), old).Verdict);
        Assert.Equal(DriverStoreVerdict.Candidate, Entry(DriverStoreCleaner.Analyze(new[] { old, newer }), old).Verdict);
    }

    [Fact]
    public void Unreadable_inf_or_version_is_undetermined()
    {
        var unreadable = Pkg("hdxrt.inf", "6.0.9500.1", parsed: false);
        var badVersion = Pkg("hdxrt.inf", "inconnue");
        var newer = Pkg("hdxrt.inf", "6.0.9600.1", inUse: true);
        var analysis = DriverStoreCleaner.Analyze(new[] { unreadable, badVersion, newer });
        Assert.Equal(DriverStoreVerdict.Undetermined, Entry(analysis, unreadable).Verdict);
        Assert.Equal(DriverStoreVerdict.Undetermined, Entry(analysis, badVersion).Verdict);
        Assert.Empty(analysis.Candidates);
    }

    [Theory]
    [InlineData("nvhda.inf_amd64_30e797a68030c486", "nvhda.inf")]
    [InlineData("dptf_cpu.inf_amd64_0123456789abcdef", "dptf_cpu.inf")]
    [InlineData("XboxSyntheticDeviceExt.inf_amd64_b847e282e82e0323", "xboxsyntheticdeviceext.inf")]
    [InlineData("sans-extension", null)]
    [InlineData(null, null)]
    public void Original_inf_name_is_read_from_the_package_id(string? packageId, string? expected) =>
        Assert.Equal(expected, DriverStoreCleaner.OriginalNameFromPackageId(packageId));

    [Fact]
    public async Task Dry_run_clean_removes_nothing()
    {
        var old = Pkg("hdxrt.inf", "6.0.9500.1");
        var used = Pkg("hdxrt.inf", "6.0.9600.1", inUse: true);
        var analysis = DriverStoreCleaner.Analyze(new[] { old, used });
        var result = await DriverStoreCleaner.CleanAsync(analysis.Entries, new DriverStoreCleanOptions { DryRun = true });
        Assert.True(result.DryRun);
        var removal = Assert.Single(result.Removals); // le paquet utilisé n'est même pas transmis
        Assert.Same(old, removal.Package);
        Assert.Null(result.InterventionId);
    }

    [Fact]
    public async Task Only_published_oem_names_can_be_deleted()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PnpUtil.DeleteUnusedDriverAsync("usb.inf"));
        await Assert.ThrowsAsync<ArgumentException>(() => PnpUtil.DeleteUnusedDriverAsync("oem1.inf /force"));
    }
}
