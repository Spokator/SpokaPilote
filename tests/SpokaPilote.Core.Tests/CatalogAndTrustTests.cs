using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Tests;

public class CatalogAndTrustTests
{
    // Extrait fidèle de la structure HTML réelle de catalog.update.microsoft.com/Search.aspx
    private const string SearchHtml = """
        <table><tr id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_R1" style="border-width:0px;">
        <td class="resultsbottomBorder resultspadding resultsIconWidth " id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C0_R1"></td>
        <td class="resultsbottomBorder resultspadding" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C1_R1">
            <a id='9eccd056-3f65-4d34-98f5-17263d1b1cb4_link' href= "javascript:void(0);" onclick='goToDetails("9eccd056-3f65-4d34-98f5-17263d1b1cb4");' class="contentTextItemSpacerNoBreakLink">
                Intel Net Driver Update (2.1.5.10)
            </a>
        </td><td class="resultsbottomBorder resultspadding" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C2_R1">
            Windows 11 Client,  version 24H2 and later,  Servicing Drivers, Windows 11 Client,  version 24H2 and later,  Upgrade &amp; Servicing Drivers
        </td><td class="resultsbottomBorder resultspadding" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C3_R1">
            Drivers (Networking)
        </td><td class="resultsbottomBorder resultspadding " id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C4_R1">
            12/28/2025
        </td><td class="resultsbottomBorder resultspadding" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C5_R1">
            2.1.5.10
        </td><td class="resultsbottomBorder resultspadding resultsSizeWidth" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C6_R1">
           <span id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_size">377 KB</span>
           <span class="noDisplay" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_originalSize">386103</span>
        </td><td class="resultsbottomBorder resultsButtonWidth" id="9eccd056-3f65-4d34-98f5-17263d1b1cb4_C7_R1">
            <input id="9eccd056-3f65-4d34-98f5-17263d1b1cb4" class="flatBlueButtonDownload focus-only" type="button" value='Download' />
        </td>
        </tr><tr id="8be03716-0402-4971-bfec-3424d3a68ac7_R2" style="border-width:0px;">
        <td class="resultsbottomBorder resultspadding" id="8be03716-0402-4971-bfec-3424d3a68ac7_C1_R2"><a>Realtek - Printer - 1.0</a></td>
        <td class="resultsbottomBorder resultspadding" id="8be03716-0402-4971-bfec-3424d3a68ac7_C2_R2">Windows 10, version 1903 and later, Servicing Drivers</td>
        <td class="resultsbottomBorder resultspadding" id="8be03716-0402-4971-bfec-3424d3a68ac7_C3_R2">Drivers (Printers)</td>
        <td class="resultsbottomBorder resultspadding " id="8be03716-0402-4971-bfec-3424d3a68ac7_C4_R2">1/5/2021</td>
        <td class="resultsbottomBorder resultspadding" id="8be03716-0402-4971-bfec-3424d3a68ac7_C5_R2">n/a</td>
        <td class="resultsbottomBorder resultspadding resultsSizeWidth" id="8be03716-0402-4971-bfec-3424d3a68ac7_C6_R2"><span class="noDisplay" id="x_originalSize">1024</span></td>
        </tr></table>
        """;

    [Fact]
    public void Parses_catalog_search_rows()
    {
        var entries = MicrosoftCatalogClient.ParseSearchResults(SearchHtml);
        Assert.Equal(2, entries.Count);
        var e = entries[0];
        Assert.Equal("9eccd056-3f65-4d34-98f5-17263d1b1cb4", e.UpdateId);
        Assert.Equal("Intel Net Driver Update (2.1.5.10)", e.Title);
        Assert.Equal("Drivers (Networking)", e.Classification);
        Assert.Equal("2.1.5.10", e.Version);
        Assert.Equal(386103, e.SizeBytes);
        Assert.Equal(new DateTime(2025, 12, 28), e.LastUpdated);
        Assert.Contains("Upgrade & Servicing Drivers", e.Products);
        Assert.Null(entries[1].Version); // « n/a »
    }

    [Theory]
    [InlineData("Windows 11 Client, version 24H2 and later, Servicing Drivers", 26200, 2)]
    [InlineData("Windows 11 Client, version 25H2 and later, Servicing Drivers", 26100, 0)] // trop récent pour 24H2
    [InlineData("Windows 11 Client, version 21H2 and later, Servicing Drivers", 22631, 2)]
    [InlineData("Windows 10, version 1903 and later, Servicing Drivers", 26100, 1)]        // pilote Win10 sur Win11
    [InlineData("Windows 10, version 1903 and later, Servicing Drivers", 19045, 2)]
    [InlineData("Windows 11 Client, version 24H2 and later, Servicing Drivers", 19045, 0)] // pilote Win11 sur Win10
    [InlineData("Windows 10 and later drivers", 26100, 1)]
    [InlineData("Windows Server 2016 and Later Servicing Drivers", 26100, 0)]
    public void Os_applicability(string products, int build, int expected)
        => Assert.Equal(expected, MicrosoftCatalogClient.OsApplicability(products, build));

    [Fact]
    public void Extracts_sha1_from_catalog_file_name()
    {
        const string url = "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/04/2d479653-a9a3-4802-99b8-caa1a8056f11_9f4fb742473ade27748eee86fbe60f29533cd88a.cab";
        Assert.Equal("9f4fb742473ade27748eee86fbe60f29533cd88a", MicrosoftCatalogClient.Sha1FromUrl(url));
        Assert.Null(MicrosoftCatalogClient.Sha1FromUrl("https://example.com/driver.cab"));
    }

    [Fact]
    public void Trust_policy_only_allows_official_https_hosts()
    {
        var policy = new TrustPolicy(new Settings());
        Assert.True(policy.IsTrustedHost(new Uri("https://catalog.s.download.windowsupdate.com/c/x.cab")));
        Assert.True(policy.IsTrustedHost(new Uri("https://au.download.windowsupdate.com/x.cab")));
        Assert.True(policy.IsTrustedHost(new Uri("https://downloads.dell.com/FOLDER/x.exe")));
        Assert.True(policy.IsTrustedHost(new Uri("https://ftp.hp.com/pub/softpaq/sp1.exe")));
        Assert.False(policy.IsTrustedHost(new Uri("http://downloads.dell.com/x.exe")));          // pas de HTTP
        Assert.False(policy.IsTrustedHost(new Uri("https://downloads.dell.com.evil.io/x.exe")));  // pas de faux sous-domaine
        Assert.False(policy.IsTrustedHost(new Uri("https://www.driverpack.io/x.exe")));
        Assert.False(policy.IsTrustedHost(new Uri("https://evildownload.windowsupdate.com.attacker.net/x")));
    }

    [Fact]
    public void Extra_trusted_hosts_from_settings()
    {
        var policy = new TrustPolicy(new Settings { ExtraTrustedHosts = { "pilotes.mon-entreprise.fr" } });
        Assert.True(policy.IsTrustedHost(new Uri("https://pilotes.mon-entreprise.fr/hp.zip")));
    }

    [Theory]
    [InlineData("CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=Dell Inc, O=Dell Inc, L=Round Rock, S=Texas, C=US", true)]
    [InlineData("CN=HP Inc., O=HP Inc., L=Palo Alto, S=California, C=US", true)]
    [InlineData("CN=\"Lenovo (Beijing) Limited\", O=\"Lenovo (Beijing) Limited\", C=CN", true)]
    [InlineData("CN=Random Driver Updater Ltd, O=Random Driver Updater Ltd", false)]
    [InlineData(null, false)]
    // Sujets réels relevés sur des paquets officiels (catalogues Dell/HP 2026, descripteurs Lenovo, pilotes, .NET).
    [InlineData("CN=Dell Technologies Inc., OU=DUP Client Creation Service, O=Dell Technologies Inc., L=Round Rock, S=Texas, C=US", true)]
    [InlineData("CN=Lenovo, OU=G09, O=Lenovo, L=Morrisville, S=North Carolina, C=US", true)]
    [InlineData("CN=Intel Corporation, O=Intel Corporation, L=Santa Clara, S=California, C=US", true)]
    [InlineData("CN=NVIDIA Corporation, OU=2008B9F, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US", true)]
    [InlineData("CN=Realtek Semiconductor Corp., O=Realtek Semiconductor Corp., L=Hsinchu, C=TW", true)]
    [InlineData("CN=\"Advanced Micro Devices, Inc.\", O=\"Advanced Micro Devices, Inc.\", L=Santa Clara, S=California, C=US", true)]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=\"Brother Industries, Ltd.\", O=\"Brother Industries, Ltd.\", L=Nagoya, S=Aichi, C=JP", true)]
    [InlineData("CN=\"Ricoh Company, Ltd.\", O=\"Ricoh Company, Ltd.\", L=Ohta-ku, S=Tokyo, C=JP", true)]
    [InlineData("CN=KYOCERA Document Solutions Inc., O=KYOCERA Document Solutions Inc., L=Osaka, C=JP", true)]
    [InlineData("CN=\"Logitech Europe S.A.\", O=\"Logitech Europe S.A.\", L=Lausanne, C=CH", true)]
    [InlineData("CN=Hewlett-Packard Company, O=Hewlett-Packard Company, L=Palo Alto, S=California, C=US", true)]
    [InlineData("CN=hp inc, O=HP INC, C=US", true)]                                         // casse, ponctuation
    // Préfixes, homonymes et contrefaçons : refusés (l'ancienne comparaison « commence par » les acceptait).
    [InlineData("CN=Canonical Ltd., O=Canonical Ltd., L=London, C=GB", false)]
    [InlineData("CN=Dell Inc Malware Unit, O=Dell Inc Malware Unit, C=US", false)]
    [InlineData("CN=Intel Corporation Fake, O=Intel Corporation Fake, C=US", false)]
    [InlineData("CN=HP Incorporated Tools, O=HP Incorporated Tools", false)]
    [InlineData("CN=Lenovo, O=Evil Corp, C=US", false)]                                     // CN trompeur : seul O compte
    [InlineData("CN=Microsoft Windows Hardware Compatibility Publisher, O=Contoso, C=US", false)]
    [InlineData("CN=NVIDIA Corporation, O=Evil, O=NVIDIA Corporation", false)]              // O ambigu
    [InlineData("CN=Sharpie Tools, O=Sharpie Tools", false)]
    [InlineData("CN=Νvidia Corporation, O=Νvidia Corporation", false)]           // N grec
    [InlineData("", false)]
    public void Trusted_publishers(string? subject, bool expected)
        => Assert.Equal(expected, new TrustPolicy(new Settings()).IsTrustedPublisher(subject));

    [Fact]
    public void Extra_trusted_publishers_match_exactly_and_ignore_blank_entries()
    {
        var policy = new TrustPolicy(new Settings { ExtraTrustedPublishers = { "", "   ", "Contoso Pilotes SAS", "Fabrikam" } });
        Assert.False(policy.IsTrustedPublisher("CN=Random Driver Updater Ltd, O=Random Driver Updater Ltd")); // entrée vide ≠ tout le monde
        Assert.True(policy.IsTrustedPublisher("CN=Contoso Pilotes, O=Contoso Pilotes SAS, C=FR"));
        Assert.True(policy.IsTrustedPublisher("CN=Fabrikam, O=\"Fabrikam, Inc.\", C=US"));                  // forme juridique
        Assert.False(policy.IsTrustedPublisher("CN=Fabrikam Evil, O=Fabrikamevil Ltd"));
        Assert.True(policy.IsTrustedPublisher("CN=Fabrikam, C=US"));                                       // sans O= : CN
        Assert.False(new TrustPolicy(new Settings { ExtraTrustedHosts = { "", "*." } }).IsTrustedHost(new Uri("https://evil.example/x.exe")));
    }

    [Theory]
    [InlineData("HP Inc.", "hp")]
    [InlineData("Ricoh Company, Ltd.", "ricoh")]
    [InlineData("Logitech Europe S.A.", "logitech europe")]
    [InlineData("Lenovo (Beijing) Limited", "lenovo (beijing)")]
    [InlineData("Canonical Ltd.", "canonical")]
    [InlineData("  Dell   Technologies Inc. ", "dell technologies")]
    [InlineData("Inc", "inc")]
    public void Publisher_names_are_normalized(string name, string expected) => Assert.Equal(expected, TrustPolicy.NormalizePublisher(name));

    [Fact]
    public void Each_official_host_expects_its_own_vendor()
    {
        var nvidia = "CN=NVIDIA Corporation, OU=2008B9F, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US";
        var dell = "CN=Dell Technologies Inc., OU=DUP Client Creation Service, O=Dell Technologies Inc., L=Round Rock, S=Texas, C=US";
        Assert.True(TrustPolicy.IsExpectedPublisher(new Uri("https://us.download.nvidia.com/Windows/x.exe"), nvidia));
        Assert.False(TrustPolicy.IsExpectedPublisher(new Uri("https://us.download.nvidia.com/Windows/x.exe"), dell));
        Assert.True(TrustPolicy.IsExpectedPublisher(new Uri("https://downloads.dell.com/catalog/CatalogPC.cab"), dell));
        Assert.True(TrustPolicy.IsExpectedPublisher(new Uri("https://hpia.hpcloud.hp.com/ref/8723/8723_64_11.0.23h2.cab"), "CN=HP Inc., O=HP Inc., L=Palo Alto, S=California, C=US"));
        Assert.True(TrustPolicy.IsExpectedPublisher(new Uri("https://au.download.windowsupdate.com/x.cab"), "CN=Microsoft Corporation, O=Microsoft Corporation, C=US"));
        Assert.False(TrustPolicy.IsExpectedPublisher(new Uri("https://pilotes.exemple.fr/x.exe"), nvidia));
        // Chaque domaine officiel a un éditeur attendu.
        foreach (var host in OfficialHosts.Download)
            Assert.NotNull(TrustPolicy.ExpectedPublishers(new Uri($"https://{host.Replace("*.", "a.")}/x")));
    }

    [Fact]
    public void Normalizes_scheme_less_and_http_urls_to_https()
    {
        Assert.Equal("https://ftp.hp.com/pub/softpaq/sp1.exe", TrustPolicy.NormalizeUrl("ftp.hp.com/pub/softpaq/sp1.exe").ToString());
        Assert.Equal("https://downloads.dell.com/a.exe", TrustPolicy.NormalizeUrl("http://downloads.dell.com/a.exe").ToString());
    }

    [Fact]
    public void Catalog_updates_require_same_vendor_and_never_replace_inbox_drivers()
    {
        Assert.True(MicrosoftCatalogSource.SameVendor("Realtek Semiconductor Corp. MEDIA Driver Update", "Realtek Semiconductor Corp."));
        Assert.False(MicrosoftCatalogSource.SameVendor("Intel(R) Corporation System Driver Update", "Microsoft"));
        Assert.True(MicrosoftCatalogSource.IsInboxDriver(new DeviceInfo { InstanceId = "x", DriverProvider = "Microsoft", InfName = "hdaudbus.inf" }));
        Assert.False(MicrosoftCatalogSource.IsInboxDriver(new DeviceInfo { InstanceId = "x", DriverProvider = "Realtek", InfName = "oem42.inf" }));
    }

    [Fact]
    public void Merge_keeps_highest_priority_source_per_device()
    {
        DriverCandidate C(DriverSourceKind src, string title, params string[] devices)
        {
            var c = new DriverCandidate { Id = title, Source = src, Title = title, MatchRank = 0 };
            c.TargetDeviceIds.AddRange(devices);
            c.TargetDeviceNames.AddRange(devices);
            return c;
        }

        var merged = DriverScanEngine.Merge(new[]
        {
            C(DriverSourceKind.MicrosoftCatalog, "catalog-net", "NET"),
            C(DriverSourceKind.WindowsUpdate, "wu-net-audio", "NET", "AUDIO"),
            C(DriverSourceKind.Dell, "dell-net", "NET"),
            C(DriverSourceKind.LocalRepository, "repo-gpu", "GPU"),
            C(DriverSourceKind.Dell, "dell-app"), // sans périphérique ciblé : conservé
        });

        Assert.Contains(merged, c => c.Title == "dell-net");
        Assert.DoesNotContain(merged, c => c.Title == "catalog-net");
        var wu = Assert.Single(merged, c => c.Title == "wu-net-audio");
        Assert.Equal(new[] { "AUDIO" }, wu.TargetDeviceIds); // NET déjà couvert par Dell
        Assert.Contains(merged, c => c.Title == "repo-gpu");
        Assert.Contains(merged, c => c.Title == "dell-app");
    }

    [Fact]
    public void Device_hint_explains_hardware_usb_failures()
    {
        var d = new DeviceInfo { InstanceId = "USB\\x", ErrorCode = 43, HardwareIds = new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" } };
        Assert.Contains("matériel", d.Hint);
        Assert.Equal(DeviceHealth.Error, d.Health);
        Assert.Equal(DeviceHealth.MissingDriver, new DeviceInfo { InstanceId = "x", ErrorCode = 28 }.Health);
    }
}
