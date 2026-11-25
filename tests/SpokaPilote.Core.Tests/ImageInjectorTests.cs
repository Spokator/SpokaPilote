using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Tests;

/// <summary>Analyse des sorties DISM réelles (capturées sur Windows 11 25H2, DISM 10.0.26100) en anglais et en français.</summary>
public class ImageInjectorTests
{
    private const string WimInfoIndexEn = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.9549

        Details for image : C:\images\winpe-amd64.wim

        Index : 1
        Name : Microsoft Windows PE (amd64)
        Description : Microsoft Windows PE (amd64)
        Size : 1 804 896 322 bytes
        WIM Bootable : No
        Architecture : x64
        Hal : <undefined>
        Version : 10.0.25398
        ServicePack Build : 1
        ServicePack Level : 0
        Edition : WindowsPE
        Installation : WindowsPE
        ProductType : WinNT
        ProductSuite :
        System Root : WINDOWS
        Directories : 3607
        Files : 16215
        Created : 11/06/2023 - 04:56:01
        Modified : 11/06/2023 - 04:56:02
        Languages :
        	en-US (Default)

        The operation completed successfully.
        """;

    private const string WimInfoIndexFr = """

        Outil Gestion et maintenance des images de déploiement
        Version : 10.0.26100.9549

        Détails pour l'image : C:\images\winpe-amd64.wim

        Index : 1
        Nom : Microsoft Windows PE (amd64)
        Description : Microsoft Windows PE (amd64)
        Taille : 1 804 896 322 octets
        Peut être démarré par WIM : Non
        Architecture : x64
        Couche HAL : <non défini>
        Version : 10.0.25398
        Build du Service Pack : 1
        Niveau du Service Pack : 0
        Édition : WindowsPE
        Installation : WindowsPE
        Type de produit : WinNT
        Suite de produits :
        Racine système : WINDOWS
        Répertoires: 3607
        Fichiers : 16215
        Création : 11/06/2023 - 04:56:01
        Modification : 11/06/2023 - 04:56:02
        Langues :
        	en-US (Par défaut)

        L'opération a réussi.
        """;

    private const string WimInfoListEn = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.9549

        Details for image : D:\sources\install.wim

        Index : 1
        Name : Windows 11 Famille
        Description : Windows 11 Famille
        Size : 18,876,543,210 bytes

        Index : 6
        Name : Windows 11 Professionnel
        Description : Windows 11 Professionnel
        Size : 19 123 456 789 bytes

        The operation completed successfully.
        """;

    private const string DriversEn = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.9549

        Image Version: 10.0.26200.9974

        Obtaining list of 3rd party drivers from the driver store...

        Driver packages listing:

        Published Name : oem0.inf
        Original File Name : steamxboxndi.inf
        Inbox : No
        Class Name : NetService
        Provider Name : Valve Corporation
        Date : 21/02/2023
        Version : 1.0.0.16

        Published Name : oem1.inf
        Original File Name : axusbeth.inf
        Inbox : No
        Class Name : Net
        Provider Name : ASIX
        Date : 07/06/2024
        Version : 3.20.2.0

        The operation completed successfully.
        """;

    private const string DriversFr = """
        Outil Gestion et maintenance des images de déploiement
        Version : 10.0.26100.9549

        Version de l'image : 10.0.26200.9974

        Obtention d'une liste de pilotes tiers à partir du magasin de pilotes...

        Liste des packages de pilotes :

        Nom publié : oem0.inf
        Nom du fichier d'origine : steamxboxndi.inf
        Boîte de réception : Non
        Nom de la classe : NetService
        Nom du fournisseur : Valve Corporation
        Date : 21/02/2023
        Version : 1.0.0.16

        Nom publié : oem1.inf
        Nom du fichier d'origine : axusbeth.inf
        Boîte de réception : Non
        Nom de la classe : Net
        Nom du fournisseur : ASIX
        Date : 07/06/2024
        Version : 3.20.2.0

        L'opération a réussi.
        """;

    [Fact]
    public void Wim_index_details_english()
    {
        var info = Assert.Single(ImageInjector.ParseWimInfo(WimInfoIndexEn));
        Assert.Equal(1, info.Index);
        Assert.Equal("Microsoft Windows PE (amd64)", info.Name);
        Assert.Equal(1804896322L, info.SizeBytes);
        Assert.Equal("amd64", info.Architecture);
        Assert.Equal(new Version(10, 0, 25398, 1), info.Version); // build du Service Pack ajouté en révision
        Assert.Equal("WindowsPE", info.Edition);
        Assert.Equal(new[] { "en-US (Default)" }, info.Languages);
    }

    [Fact]
    public void Wim_index_details_french_give_the_same_result()
    {
        var en = Assert.Single(ImageInjector.ParseWimInfo(WimInfoIndexEn));
        var fr = Assert.Single(ImageInjector.ParseWimInfo(WimInfoIndexFr));
        Assert.Equal(en.Name, fr.Name);
        Assert.Equal(en.SizeBytes, fr.SizeBytes);
        Assert.Equal(en.Architecture, fr.Architecture);
        Assert.Equal(en.Version, fr.Version);
        Assert.Equal(en.Edition, fr.Edition);
        Assert.Equal(new[] { "en-US (Par défaut)" }, fr.Languages);
    }

    [Fact]
    public void Wim_index_list_with_thousand_separators_and_tool_version_ignored()
    {
        var images = ImageInjector.ParseWimInfo(WimInfoListEn);
        Assert.Equal(new[] { 1, 6 }, images.Select(i => i.Index));
        Assert.Equal("Windows 11 Professionnel", images[1].Name);
        Assert.Equal(18876543210L, images[0].SizeBytes);
        Assert.Null(images[0].Version); // « Version: 10.0.26100 » de l'outil n'appartient à aucune image
        Assert.Null(images[0].Architecture);
    }

    [Fact]
    public void Undefined_values_of_a_non_windows_image_are_null()
    {
        var info = Assert.Single(ImageInjector.ParseWimInfo("Index : 1\nName : Test\nArchitecture : <undefined>\nVersion : <undefined>\nEdition : <undefined>\nLanguages : <undefined>\n"));
        Assert.Null(info.Architecture);
        Assert.Null(info.Version);
        Assert.Null(info.Edition);
        Assert.Empty(info.Languages);
    }

    [Fact]
    public void Accents_badly_decoded_by_the_console_code_page_do_not_break_keys()
    {
        // « Nom publié » lu en UTF-8 alors que DISM écrit en page de code OEM : le « é » devient U+FFFD.
        var drivers = ImageInjector.ParseDrivers("Nom publi\uFFFD : oem3.inf\nNom du fichier d'origine : a.inf\nBo\uFFFDte de r\uFFFDception : Non\n");
        var d = Assert.Single(drivers);
        Assert.Equal("oem3.inf", d.PublishedName);
        Assert.Equal("a.inf", d.OriginalFileName);
    }

    [Theory]
    [InlineData(DriversEn)]
    [InlineData(DriversFr)]
    public void Third_party_drivers_list_english_and_french(string output)
    {
        var drivers = ImageInjector.ParseDrivers(output);
        Assert.Equal(2, drivers.Count);
        Assert.Equal("oem1.inf", drivers[1].PublishedName);
        Assert.Equal("axusbeth.inf", drivers[1].OriginalFileName);
        Assert.False(drivers[1].Inbox);
        Assert.Equal("Net", drivers[1].ClassName);
        Assert.Equal("ASIX", drivers[1].Provider);
        Assert.Equal("3.20.2.0", drivers[1].Version);
    }

    [Fact]
    public void Add_driver_lines_english_and_french()
    {
        var output = """
            Searching for driver packages to install...
            Found 3 driver package(s) to install.
            Installing 1 of 3 - C:\Pilotes\Dell\a\net.inf: The driver package was successfully installed.
            Installing 2 of 3 - C:\Pilotes\Dell\b\audio.inf: Error - An error occurred. The driver package could not be installed.
            Installation de 3 sur 3 - C:\Pilotes\Dell\c\video.inf : Le package de pilotes a été installé.
            The operation completed successfully.
            """;
        var results = ImageInjector.ParseAddDriver(output);
        Assert.Equal(3, results.Count);
        Assert.True(results[0].Success);
        Assert.Equal(@"C:\Pilotes\Dell\a\net.inf", results[0].InfPath);
        Assert.False(results[1].Success);
        Assert.True(results[2].Success);
        Assert.Equal(@"C:\Pilotes\Dell\c\video.inf", results[2].InfPath);
    }

    [Fact]
    public void Failure_description_uses_the_error_line()
    {
        var result = new ProcessResult(87, """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.9549

            Error: 87

            An initialization error occurred.
            For more information, review the log file.

            The DISM log file can be found at C:\WINDOWS\Logs\DISM\dism.log
            """, "", false);
        Assert.Equal("Error: 87 — An initialization error occurred.", ImageInjector.DescribeFailure(result));
        var fr = new ProcessResult(2, "Erreur : 2\n\nLe fichier spécifié est introuvable.\n", "", false);
        Assert.Equal("Erreur : 2 — Le fichier spécifié est introuvable.", ImageInjector.DescribeFailure(fr));
    }

    [Fact]
    public void Mounted_images_detection()
    {
        Assert.False(ImageInjector.HasMountedImages("Mounted images:\n\n\nNo mounted images found.\n"));
        Assert.True(ImageInjector.HasMountedImages("Mounted images:\n\nMount Dir : C:\\Mount\nImage File : C:\\x.wim\nImage Index : 1\nMounted Read/Write : Yes\nStatus : Ok\n"));
    }

    [Fact]
    public void Progress_bars_are_removed_from_logged_output()
    {
        var cleaned = ImageInjector.CleanOutput("Mounting image\r\n\r\n[=====                      10.0%                          ] \r\n[==========================100.0%==========================] \r\nThe operation completed successfully.\r\n");
        Assert.Equal("Mounting image\nThe operation completed successfully.", cleaned);
    }

    // ------------------------------------------------------------------ arguments

    [Fact]
    public void Dism_arguments_are_english_and_quoted()
    {
        Assert.Equal("/English /Get-WimInfo /WimFile:\"D:\\sources\\install.wim\"", ImageInjector.WimInfoArgs(@"D:\sources\install.wim"));
        Assert.Equal("/English /Get-WimInfo /WimFile:\"D:\\sources\\install.wim\" /Index:6", ImageInjector.WimInfoArgs(@"D:\sources\install.wim", 6));
        Assert.Equal("/English /Mount-Image /ImageFile:\"D:\\install.wim\" /Index:6 /MountDir:\"C:\\Mount\\x\"",
            ImageInjector.MountArgs(@"D:\install.wim", 6, @"C:\Mount\x\"));
        Assert.Equal("/English /Unmount-Image /MountDir:\"C:\\Mount\\x\" /Commit", ImageInjector.UnmountArgs(@"C:\Mount\x", commit: true));
        Assert.Equal("/English /Unmount-Image /MountDir:\"C:\\Mount\\x\" /Discard", ImageInjector.UnmountArgs(@"C:\Mount\x", commit: false));
    }

    [Fact]
    public void Add_driver_arguments_for_folder_and_offline_drive()
    {
        Assert.Equal("/English /Image:\"C:\\Mount\\x\" /LogPath:\"C:\\Logs\\dism.log\" /Add-Driver /Driver:\"\\\\srv\\pilotes\" /Recurse",
            ImageInjector.AddDriverArgs(@"C:\Mount\x", @"\\srv\pilotes", recurse: true, @"C:\Logs\dism.log"));
        // Racine d'un disque : « D:\ » sans guillemets (un \" serait lu comme un guillemet échappé).
        Assert.Equal("/English /Image:D:\\ /Add-Driver /Driver:\"E:\\Pilotes\\a.inf\"",
            ImageInjector.AddDriverArgs("D:", @"E:\Pilotes\a.inf", recurse: false, null));
        Assert.Equal("/English /Image:D:\\ /Get-Drivers", ImageInjector.GetDriversArgs(@"D:\"));
    }

    [Fact]
    public void Add_driver_plan_never_includes_a_refused_inf()
    {
        var root = @"C:\Depot";
        var checks = new List<InfCheck>
        {
            new(@"C:\Depot\Dell\audio\a.inf", true, "ok"),
            new(@"C:\Depot\Dell\audio\sub\b.inf", true, "ok"),
            new(@"C:\Depot\Dell\net\n1.inf", true, "ok"),
            new(@"C:\Depot\Dell\net\n2.inf", false, "non signé"),
            new(@"C:\Depot\Dell\video\v.inf", true, "ok"),
            new(@"C:\Depot\Dell\video\arm\v.inf", false, "arm64"),
            new(@"C:\Depot\HP\x.inf", false, "non signé"),
        };
        var plan = ImageInjector.PlanAddDriverCalls(root, checks);
        Assert.Equal(new[]
        {
            (@"C:\Depot\Dell\audio", true),
            (@"C:\Depot\Dell\net\n1.inf", false),
            (@"C:\Depot\Dell\video", false),
        }, plan);
    }

    [Fact]
    public void Add_driver_plan_is_a_single_recursive_call_when_everything_is_accepted()
    {
        var plan = ImageInjector.PlanAddDriverCalls(@"\\srv\pilotes\", new List<InfCheck>
        {
            new(@"\\srv\pilotes\Dell\a.inf", true, "ok"),
            new(@"\\srv\pilotes\HP\b\c.inf", true, "ok"),
        });
        Assert.Equal(new[] { (@"\\srv\pilotes", true) }, plan);
    }

    // ------------------------------------------------------------------ contrôle des INF

    [Fact]
    public void Inf_for_another_architecture_or_unsigned_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-img-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var arm = Path.Combine(dir, "arm.inf");
            File.WriteAllText(arm, "[Version]\nSignature=\"$Windows NT$\"\nCatalogFile=arm.cat\n[Manufacturer]\n%M%=M,NTarm64\n[M.NTarm64]\n%D%=I,PCI\\VEN_1&DEV_2\n[Strings]\nM=\"X\"\nD=\"Y\"\n");
            var x64 = Path.Combine(dir, "x64.inf");
            File.WriteAllText(x64, "[Version]\nSignature=\"$Windows NT$\"\n[Manufacturer]\n%M%=M,NTamd64.10.0...22000\n[M.NTamd64.10.0...22000]\n%D%=I,PCI\\VEN_1&DEV_2\n[Strings]\nM=\"X\"\nD=\"Y\"\n");

            var armCheck = ImageInjector.CheckInf(arm, "amd64", new Version(10, 0, 26100));
            Assert.False(armCheck.Accepted);
            Assert.Contains("ne convient pas", armCheck.Reason);
            Assert.Contains("arm64", armCheck.Reason);

            // Bonne architecture mais Windows trop ancien pour la section (build 22000 requis).
            Assert.Contains("ne convient pas", ImageInjector.CheckInf(x64, "amd64", new Version(10, 0, 19045)).Reason);

            var unsigned = ImageInjector.CheckInf(x64, "amd64", new Version(10, 0, 26100));
            Assert.False(unsigned.Accepted);
            Assert.Contains("pas signé", unsigned.Reason);

            Assert.Equal(2, ImageInjector.VerifyRepository(dir, "amd64", new Version(10, 0, 26100)).Count(c => !c.Accepted));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Architecture_specific_catalog_wins_over_undecorated_one()
    {
        // Cas réel (pack Lenovo, pilote Bluetooth MediaTek) : seul mtkbtfilterx.cat existe dans le paquet.
        var inf = Matching.InfParser.ParseText("[Version]\nCatalogFile         = mtkbtfilter.cat\nCatalogFile.NTamd64 = mtkbtfilterx.cat\n");
        Assert.Equal("mtkbtfilterx.cat", inf.CatalogFileFor("amd64"));
        Assert.Equal("mtkbtfilter.cat", inf.CatalogFileFor("arm64"));
        Assert.Equal("mtkbtfilter.cat", inf.CatalogFile);
        Assert.Equal("a.cat", Matching.InfParser.ParseText("[Version]\nCatalogFile.NTarm64=a.cat\n").CatalogFileFor("arm64"));
    }

    [Fact]
    public void Offline_windows_architecture_is_read_from_the_pe_header()
    {
        // Le noyau du poste de test : même lecture que pour un Windows hors ligne.
        var kernel = Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe");
        var expected = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
        Assert.Equal(expected, ImageInjector.ReadPeArchitecture(kernel));
        var offline = ImageInjector.ReadOfflineWindows(Path.GetPathRoot(Environment.SystemDirectory)!);
        Assert.NotNull(offline);
        Assert.Equal(10, offline!.Value.Version.Major);
    }

    [Theory]
    [InlineData("D:", @"D:\")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"C:\Mount\x\", @"C:\Mount\x")]
    public void Image_root_normalization(string input, string expected) => Assert.Equal(expected, ImageInjector.ImageRoot(input));
}
