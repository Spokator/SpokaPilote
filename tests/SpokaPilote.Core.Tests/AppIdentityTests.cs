using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Identité du produit (moteur partagé avec d'autres produits, ex. SpokaPrint). Les tests n'appellent jamais
/// AppIdentity.Use : l'identité est globale et les tests s'exécutent en parallèle ; seules les surcharges pures sont testées.
/// </summary>
public class AppIdentityTests
{
    private static readonly AppIdentity Other = new()
    {
        ProductName = "SpokaPrint",
        DataFolderName = "SpokaPrint",
        HomeVariable = "SPOKAPRINT_HOME",
        PortableMarkerFileName = null,
        LogFilePrefix = "spokaprint",
        UpdateRepository = "Spokator/SpokaPrint",
        TaskFolder = @"\SpokaPrint",
    };

    [Fact]
    public void Default_identity_is_SpokaPilote()
    {
        Assert.Equal("SpokaPilote", AppIdentity.SpokaPilote.ProductName);
        Assert.Equal(AppPaths.HomeVariable, AppIdentity.SpokaPilote.HomeVariable);
        Assert.Equal(AppPaths.PortableMarkerFileName, AppIdentity.SpokaPilote.PortableMarkerFileName);
        Assert.Equal(UpdateChecker.Repository, AppIdentity.SpokaPilote.UpdateRepository);
        Assert.Equal(MaintenanceScheduler.TaskFolder, AppIdentity.SpokaPilote.TaskFolder);
        Assert.Same(AppIdentity.SpokaPilote, AppIdentity.Current);
    }

    [Fact]
    public void Another_product_gets_its_own_data_folders()
    {
        var layout = AppPaths.Resolve(Other, null, @"C:\Program Files\SpokaPrint", @"C:\ProgramData", @"C:\Users\u\AppData\Local", "PC01", null, _ => false);
        Assert.Equal(DataLocationMode.Installed, layout.Mode);
        Assert.Equal(@"C:\ProgramData\SpokaPrint", layout.Root);
        Assert.Equal(@"C:\Users\u\AppData\Local\SpokaPrint\ui.json", layout.UiPreferencesFile);

        var redirected = AppPaths.Resolve(Other, @"D:\essai", @"C:\Program Files\SpokaPrint", @"C:\ProgramData", @"C:\L", "PC01", null, _ => false);
        Assert.Equal(DataLocationMode.Override, redirected.Mode);
        Assert.Equal(@"D:\essai", redirected.Root);
    }

    [Fact]
    public void A_product_without_portable_marker_never_becomes_portable()
    {
        // Même si un fichier « SpokaPilote.portable » traîne à côté de l'exécutable.
        var layout = AppPaths.Resolve(Other, null, @"E:\Outils", @"C:\ProgramData", @"C:\L", "PC01", null, _ => true);
        Assert.Equal(DataLocationMode.Installed, layout.Mode);
        Assert.False(layout.IsPortable);
    }

    [Fact]
    public void Task_folder_is_derived_from_the_task_name()
    {
        Assert.Equal(@"\SpokaPilote", MaintenanceScheduler.TaskFolderOf(MaintenanceScheduler.TaskName));
        Assert.Equal(@"\SpokaPrint", MaintenanceScheduler.TaskFolderOf(@"\SpokaPrint\Deploiement"));
        Assert.Null(MaintenanceScheduler.TaskFolderOf(@"\Tache"));            // racine : jamais supprimée
        Assert.Null(MaintenanceScheduler.TaskFolderOf(@"\A\B\C"));            // sous-dossier : laissé en place
    }

    [Fact]
    public void Executable_check_accepts_another_cli_name()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Null(MaintenanceScheduler.ValidateExecutable(Path.Combine(pf, "SpokaPrint", "spokaprint.exe"), "spokaprint.exe", "SpokaPrint"));
        Assert.Contains("spokaprint.exe", MaintenanceScheduler.ValidateExecutable(Path.Combine(pf, "SpokaPrint", "spokapilote.exe"), "spokaprint.exe", "SpokaPrint"));
        Assert.Contains("SpokaPrint", MaintenanceScheduler.ValidateExecutable(@"C:\src\bin\Release\spokaprint.exe", "spokaprint.exe", "SpokaPrint"));
        // Contrôle historique inchangé.
        Assert.Contains("spokapilote.exe", MaintenanceScheduler.ValidateExecutable(Path.Combine(pf, "SpokaPrint", "spokaprint.exe")));
    }

    [Fact]
    public void System_task_xml_accepts_several_triggers()
    {
        var ns = MaintenanceScheduler.TaskNamespace;
        var xml = MaintenanceScheduler.BuildSystemTaskXml(@"\SpokaPrint\Deploiement", "essai",
            new[] { new XElement(ns + "BootTrigger", new XElement(ns + "Enabled", "true")), new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", "true")) },
            runOnBattery: true, TimeSpan.FromMinutes(30), @"C:\Program Files\SpokaPrint\spokaprint.exe", new[] { "deploy", @"\\srv\profil.json" });
        var doc = XDocument.Parse(xml);
        Assert.Equal(2, doc.Descendants(ns + "Triggers").Single().Elements().Count());
        Assert.Equal("S-1-5-18", doc.Descendants(ns + "UserId").Single().Value);
        var (_, arguments, trigger) = MaintenanceScheduler.ParseDefinition(xml);
        Assert.Equal(@"deploy \\srv\profil.json", arguments);
        Assert.Contains("ouverture de session", trigger);
    }

    [Fact]
    public async Task Update_check_rejects_an_invalid_repository_name()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => UpdateChecker.CheckAsync(new Version(1, 0), "pas un dépôt"));
        await Assert.ThrowsAsync<ArgumentException>(() => UpdateChecker.CheckAsync(new Version(1, 0), "a/b/../c"));
    }

    [Fact]
    public async Task Installer_download_only_accepts_the_current_product_releases()
    {
        // Un installeur d'un autre dépôt (ici SpokaPrint, alors que le produit en vigueur est SpokaPilote) est refusé
        // avant tout téléchargement.
        var update = new UpdateInfo(new Version(9, 0, 0), "v9.0.0",
            "https://github.com/Spokator/SpokaPrint/releases/download/v9.0.0/SpokaPrint-Setup-9.0.0.exe", "https://github.com/r", "");
        var ex = await Assert.ThrowsAsync<UpdateIntegrityException>(() => UpdateChecker.DownloadAsync(update, null));
        Assert.Contains("SpokaPilote", ex.Message);
    }

    [Fact]
    public void Identity_is_validated()
    {
        Assert.Throws<ArgumentException>(() => AppIdentity.Use(Other with { DataFolderName = "a\\b" }));
        Assert.Throws<ArgumentException>(() => AppIdentity.Use(Other with { TaskFolder = "SpokaPrint" }));
        Assert.Same(AppIdentity.SpokaPilote, AppIdentity.Current); // refus : identité inchangée
    }
}
