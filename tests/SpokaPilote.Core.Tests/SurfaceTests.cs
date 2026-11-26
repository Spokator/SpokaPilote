using System.Text;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Packs Microsoft Surface : table embarquée, pages réelles du Centre de téléchargement (extraits réduits consultés le
/// 26/11/2026), convention de nommage des MSI, identification du poste par son SKU SMBIOS, firmwares UEFI.
/// </summary>
public class SurfaceTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "surface", name);

    private static DownloadCenterPage Page(int id) => SurfaceDriverPacks.ParsePage(File.ReadAllText(Fixture($"dlc-{id}.html")), id);

    private static string AppPathsSafe(string name) => SpokaPilote.Core.Infrastructure.AppPaths.SafeName(name, 60);

    private static SurfaceModel Model(int id) => SurfaceDriverPacks.Catalog.Models.Single(m => m.DownloadId == id);

    private static List<OemDriverPack> Packs(int id) =>
        Page(id).Files.Select(f => SurfaceDriverPacks.ToPack(Model(id), f)).OfType<OemDriverPack>().ToList();

    // ------------------------------------------------------------------ table embarquée

    [Fact]
    public void Embedded_table_is_consistent_and_sourced()
    {
        var catalog = SurfaceDriverPacks.Catalog;
        Assert.Equal(1, catalog.Version);
        Assert.Equal("https://learn.microsoft.com/surface/manage-surface-driver-and-firmware-updates", catalog.ModelSource);
        Assert.Equal("https://learn.microsoft.com/surface/surface-system-sku-reference", catalog.SkuSource);
        Assert.True(catalog.Models.Count >= 30);
        Assert.Equal(catalog.Models.Count, catalog.Models.Select(m => m.DownloadId).Distinct().Count());
        Assert.All(catalog.Models, m =>
        {
            Assert.Contains(m.Architecture, new[] { "amd64", "arm64" });
            Assert.NotEmpty(m.SystemSkus);
            Assert.NotEmpty(m.SystemModels);
            Assert.All(m.SystemSkus, sku => Assert.DoesNotContain(" ", sku)); // les SKU Surface n'ont pas d'espace
        });
        // Une SKU désigne un seul pack.
        var skus = catalog.Models.SelectMany(m => m.SystemSkus).ToList();
        Assert.Equal(skus.Count, skus.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(104680, "amd64", "Surface_Pro_9_for_Business_2038")]
    [InlineData(105941, "arm64", "Surface_Pro_9_With_5G_1997")]
    [InlineData(106119, "arm64", "Surface_Pro_11th_Edition_For_Business_2085")]
    [InlineData(108013, "amd64", "Surface_Pro_11th_Edition_With_Intel_For_Business_2103")]
    [InlineData(102924, "amd64", "Surface_Laptop_4_1950:1951")]
    [InlineData(104679, "amd64", "Surface_Laptop_5_for_Business_1950:1951")]
    [InlineData(106120, "arm64", "Surface_Laptop_7th_Edition_For_Business_2037")]
    [InlineData(103505, "amd64", "Surface_Laptop_Studio_1964")]
    [InlineData(105610, "amd64", "Surface_Laptop_Studio_2_2029")]
    [InlineData(103504, "amd64", "Surface_Go_3_1926")]
    [InlineData(105609, "amd64", "Surface_Go_4_2067")]
    public void Table_maps_official_skus_to_download_pages(int downloadId, string architecture, string sku)
    {
        var model = Model(downloadId);
        Assert.Equal(architecture, model.Architecture);
        Assert.Contains(sku, model.SystemSkus);
        Assert.Equal($"https://www.microsoft.com/en-us/download/details.aspx?id={downloadId}", model.PageUrl);
    }

    // ------------------------------------------------------------------ page du Centre de téléchargement

    [Fact]
    public void Real_download_center_page_is_parsed()
    {
        var page = Page(104680);
        Assert.Equal("Surface Pro 9 with Intel", page.Title);
        var file = Assert.Single(page.Files);
        Assert.Equal("SurfacePro9_Win11_22631_26.091.15297.0.msi", file.Name);
        Assert.Equal("https://download.microsoft.com/download/9a27d0e5-cbce-4933-8fc6-ec5fe5315c69/SurfacePro9_Win11_22631_26.091.15297.0.msi", file.Url);
        Assert.Equal(898347008, file.Size);
        Assert.Equal(new DateTime(2026, 10, 5, 18, 35, 43, DateTimeKind.Utc), file.Published);
    }

    [Fact]
    public void Page_without_details_block_gives_a_clear_error()
    {
        // Page de contrôle servie à la place de la page attendue (constaté avec un agent utilisateur de navigateur).
        var ex = Assert.Throws<SurfacePackUnavailableException>(() =>
            SurfaceDriverPacks.ParsePage("<!doctype html><html><head><title>Microsoft</title></head><body>Access denied</body></html>", 104680));
        Assert.Contains("format inattendu", ex.Message);
        Assert.Contains("id=104680", ex.Message);

        var error = Assert.Throws<SurfacePackUnavailableException>(() =>
            SurfaceDriverPacks.ParsePage("<script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"Not found\"}}</script>", 1));
        Assert.Contains("Not found", error.Message);
    }

    [Theory]
    [InlineData("SurfacePro9_Win11_22631_26.091.15297.0.msi", "win11", 22631, "26.091.15297.0")]
    [InlineData("SurfacePro11_ARM_Win11_26100_26.094.41119.0.msi", "win11", 26100, "26.094.41119.0")]
    [InlineData("SurfacePro9-5G_Win11_22631_26.062.19189.0.msi", "win11", 22631, "26.062.19189.0")]
    [InlineData("SurfacePro7+_Win10_19045_25.090.3497.0.msi", "win10", 19045, "25.090.3497.0")]
    [InlineData("SurfaceGo2_Win10_19044_24.092.15180_0.msi", "win10", 19044, "24.092.15180.0")] // révision séparée par « _ »
    public void Msi_naming_convention_is_parsed(string name, string os, int build, string version)
    {
        var parsed = SurfaceDriverPacks.ParseFileName(name);
        Assert.NotNull(parsed);
        Assert.Equal((os, build, version), parsed.Value);
    }

    [Theory]
    [InlineData("SurfaceLaptopStudio2_Nvidia_GPU_v32.0.15.9155.msi")]
    [InlineData("SurfacePro9_Win11_22631_26.091.15297.0.zip")]
    [InlineData("readme.txt")]
    public void Files_outside_the_convention_are_ignored(string name) => Assert.Null(SurfaceDriverPacks.ParseFileName(name));

    [Theory]
    [InlineData(22631, "23H2")]
    [InlineData(26100, "24H2")]
    [InlineData(26200, "25H2")]
    [InlineData(28000, "26H1")]
    [InlineData(19045, "22H2")]
    [InlineData(99999, null)]
    public void Build_is_mapped_to_windows_release(int build, string? release) => Assert.Equal(release, SurfaceDriverPacks.ReleaseForBuild(build));

    [Fact]
    public void Pack_is_built_from_page_and_table()
    {
        var pack = Assert.Single(Packs(104680));
        Assert.Equal(OemVendor.Microsoft, pack.Vendor);
        Assert.Equal("SurfacePro9_Win11_22631_26.091.15297.0", pack.Id);
        Assert.Equal("win11", pack.Os);
        Assert.Equal("amd64", pack.Architecture);
        Assert.Equal(22631, pack.OsBuild);
        Assert.Equal(new[] { "23H2" }, pack.OsReleases);
        Assert.Equal("26.091.15297.0", pack.Version);
        Assert.Equal("msi", pack.Format);
        Assert.Equal(898347008, pack.Size);
        Assert.False(pack.HasHash);
        Assert.True(pack.RequiresMicrosoftSignature);
        Assert.Equal("https://www.microsoft.com/en-us/download/details.aspx?id=104680", pack.InfoUrl);
        Assert.Equal(104680, SurfaceDriverPacks.PageId(pack.InfoUrl));
        Assert.Equal("Win11-amd64", pack.FolderOs);
        Assert.Contains("Surface_Pro_9_2038", pack.Systems[0].Ids);
    }

    [Fact]
    public void Gpu_only_msi_is_skipped_and_several_builds_are_kept()
    {
        // Surface Laptop Studio 2 : la page publie aussi un MSI du seul pilote graphique NVIDIA, hors convention.
        var studio = Assert.Single(Packs(105610));
        Assert.Equal("SurfaceLaptopStudio2_Win11_22631_26.084.44074.0.msi", studio.FileName);

        // Surface Pro 12e édition Intel : un MSI par build (24H2 et 25H2), même date : le plus récent l'emporte.
        var pro12 = Packs(108671);
        Assert.Equal(new[] { 26100, 26200 }, pro12.Select(p => p.OsBuild!.Value).OrderBy(b => b));
        var latest = Assert.Single(OemDriverPacks.Find(pro12, new DriverPackQuery { Text = "Surface Pro for Business 12th Edition" }));
        Assert.Equal(26200, latest.Pack.OsBuild);
        Assert.Equal(2, OemDriverPacks.Find(pro12, new DriverPackQuery { Text = "Surface Pro for Business 12th Edition", AllVersions = true }).Count);
    }

    [Fact]
    public void Links_outside_download_microsoft_com_are_rejected()
    {
        var file = new DownloadCenterFile("SurfacePro9_Win11_22631_26.091.15297.0.msi", "https://example.com/SurfacePro9_Win11_22631_26.091.15297.0.msi", 1, null);
        Assert.Null(SurfaceDriverPacks.ToPack(Model(104680), file));
        var http = file with { Url = "http://download.microsoft.com/download/x/SurfacePro9_Win11_22631_26.091.15297.0.msi" };
        Assert.Null(SurfaceDriverPacks.ToPack(Model(104680), http));
    }

    // ------------------------------------------------------------------ identification du poste et recherche

    [Fact]
    public void Current_surface_is_identified_by_its_smbios_sku()
    {
        var system = new SystemInfo { Manufacturer = "Microsoft Corporation", Model = "Surface Pro 9", SystemSku = "Surface_Pro_9_for_Business_2038", Architecture = "amd64" };
        var query = DriverPackQuery.ForSystem(system);
        Assert.Equal(OemVendor.Microsoft, query.Vendor);
        Assert.Equal("Surface_Pro_9_for_Business_2038", query.SystemId);
        Assert.Equal("Surface Pro 9", query.SystemModel);

        var candidates = SurfaceDriverPacks.Candidates(query);
        Assert.Equal(104680, Assert.Single(candidates).DownloadId); // pas la Surface Pro 9 5G (même « System Model »)
        var match = Assert.Single(OemDriverPacks.Find(Packs(104680), query));
        Assert.Equal("Surface Pro 9 (Intel)", match.ModelLabel);
    }

    [Fact]
    public void Unknown_sku_falls_back_to_the_smbios_model_name()
    {
        // Variante absente de la table : le nom de modèle SMBIOS sert de repli, l'architecture départage.
        var system = new SystemInfo { Manufacturer = "Microsoft Corporation", Model = "Surface Pro 9", SystemSku = "Surface_Pro_9_9999", Architecture = "arm64" };
        var query = DriverPackQuery.ForSystem(system);
        Assert.Equal(105941, Assert.Single(SurfaceDriverPacks.Candidates(query)).DownloadId);
    }

    [Fact]
    public void Hyper_v_virtual_machine_is_not_a_surface()
    {
        var vm = new SystemInfo { Manufacturer = "Microsoft Corporation", Model = "Virtual Machine", SystemSku = "None", Architecture = "amd64" };
        var query = DriverPackQuery.ForSystem(vm);
        Assert.Null(query.Vendor);
        Assert.Null(query.SystemId);
        Assert.Null(query.SystemModel);
    }

    [Fact]
    public void Text_search_infers_surface_and_reads_only_the_needed_pages()
    {
        Assert.Equal(new[] { OemVendor.Microsoft }, OemDriverPacks.InferVendors("Surface Laptop 4"));
        Assert.Equal(new[] { OemVendor.Microsoft }, OemDriverPacks.InferVendors("Microsoft Surface Go 4"));
        Assert.Contains(OemVendor.Microsoft, OemDriverPacks.SupportedVendors);
        Assert.Equal("Microsoft", OemDriverPacks.VendorName(OemVendor.Microsoft));

        Assert.Equal(new[] { 102923, 102924 }, SurfaceDriverPacks.Candidates(new DriverPackQuery { Text = "Surface Laptop 4" }).Select(m => m.DownloadId).OrderBy(i => i));
        Assert.Equal(105610, Assert.Single(SurfaceDriverPacks.Candidates(new DriverPackQuery { Text = "Surface_Laptop_Studio_2_2029" })).DownloadId);
        Assert.Equal(105610, Assert.Single(SurfaceDriverPacks.Candidates(new DriverPackQuery { Text = "Surface Laptop Studio 2" })).DownloadId);
        Assert.Empty(SurfaceDriverPacks.Candidates(new DriverPackQuery { Text = "Latitude 7420" }));
        Assert.Empty(SurfaceDriverPacks.Candidates(new DriverPackQuery { Text = "Surface Laptop 4", Architecture = "arm64" }));
    }

    [Fact]
    public void Download_folder_follows_repository_layout()
    {
        var pack = Assert.Single(Packs(104680));
        var match = Assert.Single(OemDriverPacks.Find(new[] { pack }, new DriverPackQuery { Text = "Surface Pro 9" }));
        Assert.Equal(Path.Combine(@"D:\Pilotes", "Microsoft", AppPathsSafe("Surface Pro 9 (Intel)"), "Win11-amd64"), OemDriverPackDownloader.TargetFolder(@"D:\Pilotes", match));
    }

    // ------------------------------------------------------------------ extraction et signature

    [Fact]
    public void Msi_is_extracted_by_administrative_install()
    {
        Assert.Equal("/a \"C:\\dl\\SurfaceGo3.msi\" /qn TARGETDIR=\"C:\\x\\pk\" /L*v \"C:\\logs\\m.log\"",
            SurfaceDriverPacks.MsiAdminArguments(@"C:\dl\SurfaceGo3.msi", @"C:\x\pk\", @"C:\logs\m.log"));
        Assert.Contains("une autre installation", SurfaceDriverPacks.DescribeMsiExitCode(1618));
        Assert.Equal("code 42", SurfaceDriverPacks.DescribeMsiExitCode(42));
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)] // MSI Surface réel
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=Microsoft Corporation Fake, O=Contoso, C=US", false)]
    [InlineData("CN=Dell Inc, O=Dell Inc, C=US", false)]
    [InlineData(null, false)]
    public void Only_microsoft_corporation_may_sign_a_surface_msi(string? subject, bool expected) =>
        Assert.Equal(expected, OemDriverPackDownloader.IsMicrosoftCorporation(subject));

    // ------------------------------------------------------------------ firmwares

    [Fact]
    public void Surface_uefi_capsule_is_a_signed_firmware_inf()
    {
        var path = Fixture(Path.Combine("uefi", "SurfaceUEFI.inf"));
        var inf = InfParser.Parse(path);
        Assert.True(inf.IsFirmware);
        Assert.Equal("Firmware", inf.Class);
        Assert.Contains(inf.Models, m => m.HardwareId.Equals("UEFI\\RES_{28D35383-B6C2-44BE-897C-AA9F24F15342}", StringComparison.OrdinalIgnoreCase));

        // Catalogue réel signé par Microsoft (Windows Hardware Compatibility Publisher).
        var (signed, reason) = PackageVerifier.VerifyInfSignature(path, inf, "amd64");
        Assert.True(signed, reason);

        Assert.Equal(1, OemDriverPackDownloader.CountFirmwareInfs(Fixture("uefi")));

        var accepted = ImageInjector.CheckInf(path, "amd64", new Version(10, 0, 26100));
        Assert.True(accepted.Accepted, accepted.Reason);
        Assert.True(accepted.IsFirmware);
        var excluded = ImageInjector.CheckInf(path, "amd64", new Version(10, 0, 26100), excludeFirmware: true);
        Assert.False(excluded.Accepted);
        Assert.Equal("firmware exclu (--no-firmware)", excluded.Reason);
        // Capsule x64 : écartée d'une image ARM64.
        Assert.False(ImageInjector.CheckInf(path, "arm64", new Version(10, 0, 26100)).Accepted);
    }

    [Fact]
    public void Firmware_class_is_recognised()
    {
        Assert.True(InfParser.IsFirmwareClass("Firmware"));
        Assert.True(InfParser.IsFirmwareClass(" firmware "));
        Assert.False(InfParser.IsFirmwareClass("System"));
        Assert.False(InfParser.IsFirmwareClass(null));
    }

    [Fact]
    public void Oem_code_page_decodes_dism_output()
    {
        // Octets réels de « Version d’évaluation » écrits par DISM (sortie redirigée, page 850) : « ’ » devient « ' », « é » = 0x82.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = new byte[] { 0x20, 0x64, 0x27, 0x82, 0x76, 0x61 };
        Assert.Equal(" d'éva", Encoding.GetEncoding(850).GetString(bytes));
        Assert.Contains("\uFFFD", Encoding.UTF8.GetString(bytes)); // ce que produisait la lecture en UTF-8
        Assert.NotNull(SpokaPilote.Core.Infrastructure.ProcessRunner.OemEncoding());
    }
}
