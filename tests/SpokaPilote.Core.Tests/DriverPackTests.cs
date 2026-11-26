using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;

namespace SpokaPilote.Core.Tests;

public class DriverPackTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<OemDriverPack> Dell() => OemDriverPacks.ParseDell(Fixture("dell-driverpacks.xml"));
    private static List<OemDriverPack> Hp() => OemDriverPacks.ParseHp(Fixture("hp-driverpacks.xml"));
    private static List<OemDriverPack> Lenovo() => OemDriverPacks.ParseLenovo(Fixture("lenovo-catalogv2.xml"));

    // ------------------------------------------------------------------ Dell

    [Fact]
    public void Dell_catalog_is_parsed_with_namespace_hashes_and_systems()
    {
        var packs = Dell();
        Assert.DoesNotContain(packs, p => p.Id == "ABCDE"); // pack WinPE exclu
        var win11 = packs.Single(p => p.Id == "16KXP");
        Assert.Equal("win11", win11.Os);
        Assert.Equal("amd64", win11.Architecture);
        Assert.Equal("A07", win11.Version);
        Assert.Equal("https://downloads.dell.com/FOLDER10951470M/1/7420-16KXP_Win11_1.0_A07.exe", win11.Url);
        Assert.Equal("21F923F4512EBAB5B3502E0E50EC0E854705C46AD783BD60BE2469B5DEA5B336", win11.Sha256);
        Assert.Equal("88B026A0402E57C3F5438B8D73EDD3B0", win11.Md5);
        var system = Assert.Single(win11.Systems);
        Assert.Equal("Latitude 7420", system.Name);
        Assert.Equal(new[] { "0A36", "0A37" }, system.Ids);
        Assert.Equal("exe", win11.Format);
        Assert.Equal("Win11-amd64", win11.FolderOs);
    }

    [Fact]
    public void Dell_arm64_cab_with_uppercase_extension_and_space_in_path()
    {
        var arm = Dell().Single(p => p.Id == "W394W");
        Assert.Equal("arm64", arm.Architecture);
        Assert.Equal("cab", arm.Format);
        Assert.Equal("Latitude 7455-Win11-A01-W394W.CAB", arm.FileName);
        Assert.Equal("Win11-arm64", arm.FolderOs);
    }

    [Fact]
    public void Dell_current_machine_sku_gets_latest_pack_per_os()
    {
        var system = new SystemInfo { Manufacturer = "Dell Inc.", SystemSku = "0A37", Architecture = "amd64" };
        var query = DriverPackQuery.ForSystem(system);
        Assert.Equal(OemVendor.Dell, query.Vendor);
        Assert.Equal("0A37", query.SystemId);

        var result = OemDriverPacks.Find(Dell(), query);
        Assert.Equal(new[] { "16KXP", "TXK42" }, result.Select(m => m.Pack.Id)); // Windows 11 d'abord, puis Windows 10
    }

    [Fact]
    public void Dell_search_by_name_keeps_most_recent_unless_all_versions()
    {
        var latest = OemDriverPacks.Find(Dell(), new DriverPackQuery { Text = "Dell Latitude 7420", Os = "win11" });
        Assert.Equal("16KXP", Assert.Single(latest).Pack.Id); // OLD01 (2023) écarté

        var all = OemDriverPacks.Find(Dell(), new DriverPackQuery { Text = "latitude 7420", Os = "win11", AllVersions = true });
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Architecture_filter_is_applied()
    {
        Assert.Empty(OemDriverPacks.Find(Dell(), new DriverPackQuery { Text = "Latitude 7455", Architecture = "amd64" }));
        Assert.Single(OemDriverPacks.Find(Dell(), new DriverPackQuery { Text = "Latitude 7455", Architecture = "arm64" }));
    }

    // ------------------------------------------------------------------ HP

    [Fact]
    public void Hp_groups_rows_by_softpaq_and_windows_and_ignores_iot()
    {
        var packs = Hp();
        var sp = packs.Single(p => p.Id == "sp114161");
        Assert.Equal("win10", sp.Os);
        Assert.Equal(new[] { "2004", "21H1" }, sp.OsReleases);
        Assert.Equal(new[] { "HP EliteBook 840 G6 Notebook PC", "HP EliteBook 850 G6 Notebook PC" }, sp.Systems.Select(s => s.Name));
        Assert.Equal("https://ftp.hp.com/pub/softpaq/sp114001-114500/sp114161.exe", sp.Url); // schéma ajouté
        Assert.Equal("6.00 A 1", sp.Version);
        Assert.Equal(new DateTime(2021, 6, 9), sp.Date);
    }

    [Fact]
    public void Hp_platform_id_is_matched_case_insensitively_in_comma_lists()
    {
        var system = new SystemInfo { Manufacturer = "HP", BaseBoardProduct = "8750", Architecture = "amd64" };
        var match = Assert.Single(OemDriverPacks.Find(Hp(), DriverPackQuery.ForSystem(system)));
        Assert.Equal("sp170248", match.Pack.Id);
        Assert.Equal(new[] { "874F", "8750" }, match.Pack.Systems.Single().Ids);
        Assert.Null(match.Pack.Size); // taille 0 dans le catalogue = inconnue
    }

    [Fact]
    public void Hp_840_G6_latest_pack_and_no_windows11()
    {
        var latest = Assert.Single(OemDriverPacks.Find(Hp(), new DriverPackQuery { Text = "EliteBook 840 G6" }));
        Assert.Equal("sp114161", latest.Pack.Id); // et non sp110579 (2020)
        Assert.Equal(new[] { "HP EliteBook 840 G6 Notebook PC" }, latest.MatchedModels);
        Assert.Empty(OemDriverPacks.Find(Hp(), new DriverPackQuery { Text = "EliteBook 840 G6", Os = "win11" }));
    }

    [Fact]
    public void Platform_id_typed_as_text_merges_all_models_of_the_platform()
    {
        var match = Assert.Single(OemDriverPacks.Find(Hp(), new DriverPackQuery { Text = "8549" }));
        Assert.Equal(2, match.MatchedModels.Count); // 840 G6 + 850 G6 : un seul fichier
    }

    // ------------------------------------------------------------------ Lenovo

    [Fact]
    public void Lenovo_sccm_packs_are_parsed_and_shared_urls_merged()
    {
        var packs = Lenovo();
        Assert.DoesNotContain(packs, p => p.Url.Contains("tp_iot") || p.Url.Contains("tp_hsa") || p.Url.Contains("n35uj"));
        var shared = packs.Single(p => p.Url.EndsWith("w11_24_202409.exe"));
        Assert.Equal(2, shared.Systems.Count); // T14s Gen 2 et X13 Gen 2
        Assert.Equal("bec42c69ea9804a71116f601c3812aa8e179f24eea4ae119404e6a6c401d91e9", shared.Sha256);
        Assert.Equal(new[] { "24H2" }, shared.OsReleases);
        Assert.Equal("amd64", shared.Architecture);
        Assert.StartsWith("https://", packs.Single(p => p.Url.Contains("tp_t14_gen2")).Url); // http forcé en https
    }

    [Fact]
    public void Lenovo_machine_type_from_model_prefix()
    {
        var system = new SystemInfo { Manufacturer = "LENOVO", Model = "20WMCTO1WW", Architecture = "amd64", OsVersion = new Version(10, 0, 26100) };
        var query = DriverPackQuery.ForSystem(system);
        Assert.Equal("20WM", query.SystemId);
        query.Os = "win11";
        var match = Assert.Single(OemDriverPacks.Find(Lenovo(), query));
        Assert.EndsWith("w11_24_202409.exe", match.Pack.Url); // 24H2 (2024) plutôt que 22H2 (2023)
        Assert.Equal(new[] { "ThinkPad T14S Gen 2 Type 20WM 20WN" }, match.MatchedModels);
    }

    [Fact]
    public void Name_search_uses_whole_words_t14_does_not_match_t14s()
    {
        var t14 = OemDriverPacks.Find(Lenovo(), new DriverPackQuery { Text = "ThinkPad T14 Gen 2" });
        Assert.Equal("ThinkPad T14 Gen 2 Type 20W0 20W1", Assert.Single(t14).ModelLabel);
        Assert.Equal(2, OemDriverPacks.Find(Lenovo(), new DriverPackQuery { Text = "T14s Gen 2" }).Count); // Win10 + Win11
    }

    // ------------------------------------------------------------------ outils

    [Theory]
    [InlineData("Latitude 7420", OemVendor.Dell)]
    [InlineData("EliteBook 840 G6", OemVendor.HP)]
    [InlineData("ThinkPad T14", OemVendor.Lenovo)]
    public void Vendor_is_inferred_from_product_family(string text, OemVendor vendor)
        => Assert.Equal(new[] { vendor }, OemDriverPacks.InferVendors(text));

    [Fact]
    public void Unknown_text_searches_every_catalog()
        => Assert.Equal(4, OemDriverPacks.InferVendors("20XW").Length); // Dell, HP, Lenovo, Surface (aucune page Surface lue : aucun modèle ne correspond)

    [Fact]
    public void Windows_releases_are_ordered()
    {
        var ordered = new[] { "24H2", "1909", "2004", "21H1", "20H2", "22H2" }.OrderBy(OemDriverPacks.ReleaseRank);
        Assert.Equal(new[] { "1909", "2004", "20H2", "21H1", "22H2", "24H2" }, ordered);
    }

    [Fact]
    public void Extraction_switches_per_vendor()
    {
        OemDriverPack Pack(OemVendor v, string url) => new() { Vendor = v, Id = "X1", Os = "win11", Architecture = "amd64", Url = url };
        var dest = @"D:\Pilotes\Dell\Latitude_7420\Win11-amd64\";

        var dell = OemDriverPackDownloader.ExtractionArguments(Pack(OemVendor.Dell, "https://downloads.dell.com/a/x.exe"), dest)!;
        Assert.StartsWith("/s /e=\"D:\\Pilotes\\Dell\\Latitude_7420\\Win11-amd64\" /l=\"", dell); // pas de \ avant le guillemet
        Assert.Equal("/s /e /f \"D:\\Pilotes\\Dell\\Latitude_7420\\Win11-amd64\"",
            OemDriverPackDownloader.ExtractionArguments(Pack(OemVendor.HP, "https://ftp.hp.com/sp1.exe"), dest));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"D:\\Pilotes\\Dell\\Latitude_7420\\Win11-amd64\" /EXTRACT=\"YES\"",
            OemDriverPackDownloader.ExtractionArguments(Pack(OemVendor.Lenovo, "https://download.lenovo.com/tp.exe"), dest));
        Assert.Null(OemDriverPackDownloader.ExtractionArguments(Pack(OemVendor.Dell, "https://downloads.dell.com/a/x.CAB"), dest));
    }

    [Fact]
    public void Target_folder_follows_repository_layout()
    {
        var pack = Dell().Single(p => p.Id == "16KXP");
        var folder = OemDriverPackDownloader.TargetFolder(@"\\srv\pilotes", new DriverPackMatch(pack, new[] { "Latitude 7420" }));
        Assert.Equal(@"\\srv\pilotes\Dell\Latitude_7420\Win11-amd64", folder);
    }

    [Fact]
    public void Manifest_matches_same_pack_only()
    {
        var pack = Dell().Single(p => p.Id == "16KXP");
        var manifest = new OemDriverPackDownloader.PackManifest { PackId = "16KXP", Sha256 = pack.Sha256 };
        Assert.True(manifest.Matches(pack));
        Assert.False(new OemDriverPackDownloader.PackManifest { PackId = "16KXP", Sha256 = "00" }.Matches(pack));
    }

    [Fact]
    public void Unsigned_inf_is_reported_by_pack_verification()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-pack-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.inf"), "[Version]\nSignature=\"$Windows NT$\"\nClass=Net\n[Manufacturer]\n%M%=M,NTamd64\n[M.NTamd64]\n%D%=I,PCI\\VEN_8086&DEV_1234\n[Strings]\nM=\"X\"\nD=\"Y\"\n");
            var (infs, signed, rejected) = OemDriverPackDownloader.VerifyInfs(dir, "amd64");
            Assert.Equal(1, infs);
            Assert.Equal(0, signed);
            Assert.Contains("pas signé", Assert.Single(rejected));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
