using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Core.Tests;

/// <summary>Mode portable (clé USB) : emplacements, cloisonnement par poste, mise à jour, tâches planifiées.</summary>
public class PortableModeTests
{
    private const string Exe = @"E:\SpokaPilote";
    private const string ProgramData = @"C:\ProgramData";
    private const string LocalAppData = @"C:\Users\tech\AppData\Local";
    private const string Guid1 = "3f2a9c1b-0000-4000-8000-000000000001";

    private static Func<string, bool> MarkerIn(string dir) =>
        path => path.Equals(Path.Combine(dir, AppPaths.PortableMarkerFileName), StringComparison.OrdinalIgnoreCase);

    private static readonly Func<string, bool> NoFile = _ => false;

    // ------------------------------------------------------------------ résolution des emplacements

    [Fact]
    public void Installed_copy_uses_ProgramData_and_LocalAppData()
    {
        var layout = AppPaths.Resolve(null, @"C:\Program Files\SpokaPilote\", ProgramData, LocalAppData, "PC-01", Guid1, NoFile);
        Assert.Equal(DataLocationMode.Installed, layout.Mode);
        Assert.False(layout.IsPortable);
        Assert.Equal(@"C:\ProgramData\SpokaPilote", layout.Root);
        Assert.Equal(layout.Root, layout.MachineRoot); // pas de cloisonnement : les chemins historiques ne changent pas
        Assert.Equal(@"C:\Users\tech\AppData\Local\SpokaPilote\ui.json", layout.UiPreferencesFile);
        Assert.Null(AppPaths.DefaultRepositoryFor(layout, _ => true));
    }

    [Fact]
    public void Marker_next_to_exe_switches_everything_to_the_key()
    {
        var layout = AppPaths.Resolve(null, Exe + @"\", ProgramData, LocalAppData, "pc-compta", Guid1, MarkerIn(Exe));
        Assert.Equal(DataLocationMode.Portable, layout.Mode);
        Assert.True(layout.IsPortable);
        Assert.Equal(Exe, layout.PortableHome);
        Assert.Equal(@"E:\SpokaPilote\Data", layout.Root);
        Assert.Equal(@"E:\SpokaPilote\Data\Postes\PC-COMPTA_3f2a9c1b", layout.MachineRoot);
        Assert.Equal(@"E:\SpokaPilote\Data\ui.json", layout.UiPreferencesFile);
        // Rien sur le poste : ni %ProgramData% ni %LocalAppData%.
        Assert.False(layout.Root.StartsWith(ProgramData, StringComparison.OrdinalIgnoreCase));
        Assert.False(layout.UiPreferencesFile.StartsWith(LocalAppData, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Mode portable", AppPaths.Describe(layout));
        Assert.Contains(@"E:\SpokaPilote\Data", AppPaths.Describe(layout));
    }

    [Fact]
    public void SPOKAPILOTE_HOME_keeps_priority_over_the_portable_marker()
    {
        var installed = AppPaths.Resolve(@"D:\tests\home", @"C:\Program Files\SpokaPilote", ProgramData, LocalAppData, "PC", Guid1, NoFile);
        Assert.Equal(DataLocationMode.Override, installed.Mode);
        Assert.Equal(@"D:\tests\home", installed.Root);
        Assert.Equal(@"D:\tests\home", installed.MachineRoot);
        Assert.False(installed.IsPortable);
        Assert.Equal(@"C:\Users\tech\AppData\Local\SpokaPilote\ui.json", installed.UiPreferencesFile);

        var portable = AppPaths.Resolve(@"D:\tests\home", Exe, ProgramData, LocalAppData, "PC", Guid1, MarkerIn(Exe));
        Assert.Equal(DataLocationMode.Override, portable.Mode);
        Assert.Equal(@"D:\tests\home", portable.Root);
        // Les exécutables restent portables (pas d'installeur, pas de tâche planifiée) et rien n'est écrit dans %LocalAppData%.
        Assert.True(portable.IsPortable);
        Assert.Equal(@"D:\tests\home\ui.json", portable.UiPreferencesFile);
    }

    [Fact]
    public void Blank_SPOKAPILOTE_HOME_is_ignored()
        => Assert.Equal(DataLocationMode.Portable, AppPaths.Resolve("  ", Exe, ProgramData, LocalAppData, "PC", Guid1, MarkerIn(Exe)).Mode);

    // ------------------------------------------------------------------ cloisonnement par poste

    [Theory]
    [InlineData("PC-COMPTA", "3F2A9C1B-1111-4000-8000-000000000001", "PC-COMPTA_3f2a9c1b")]
    [InlineData("pc-compta", "3f2a9c1b-2222-4000-8000-000000000002", "PC-COMPTA_3f2a9c1b")]
    [InlineData("ACCUEIL", null, "ACCUEIL")]
    [InlineData("ACCUEIL", "pas-un-guid", "ACCUEIL")]
    [InlineData("", null, "POSTE")]
    public void Machine_folder_combines_name_and_MachineGuid(string name, string? id, string expected)
        => Assert.Equal(expected, AppPaths.MachineFolderName(name, id));

    [Fact]
    public void One_key_keeps_each_machine_apart_even_with_cloned_names()
    {
        var a = AppPaths.Resolve(null, Exe, ProgramData, LocalAppData, "MINWINPC", "aaaaaaaa-0000-4000-8000-000000000001", MarkerIn(Exe));
        var b = AppPaths.Resolve(null, Exe, ProgramData, LocalAppData, "MINWINPC", "bbbbbbbb-0000-4000-8000-000000000001", MarkerIn(Exe));
        Assert.Equal(a.Root, b.Root); // configuration, cache et dépôt partagés
        Assert.NotEqual(a.MachineRoot, b.MachineRoot); // historique, sauvegardes, journaux séparés
        Assert.StartsWith(Path.Combine(a.Root, AppPaths.MachinesFolderName), a.MachineRoot);
    }

    [Fact]
    public void Rollback_is_refused_for_an_intervention_of_another_machine()
    {
        var mine = new Intervention { ComputerName = "PC-01", MachineId = "aaaaaaaa-0000-4000-8000-000000000001", AddedDriverPackages = { "oem42.inf" } };
        Assert.Null(InterventionHistory.CheckSameMachine(mine, "PC-01", "AAAAAAAA-0000-4000-8000-000000000001", sharedHistory: true));
        // Même nom (poste cloné) mais autre MachineGuid : oem42.inf y désigne un autre pilote.
        Assert.NotNull(InterventionHistory.CheckSameMachine(mine, "PC-01", "bbbbbbbb-0000-4000-8000-000000000001", sharedHistory: true));

        var legacy = new Intervention { ComputerName = "PC-01", MachineId = null };
        Assert.Null(InterventionHistory.CheckSameMachine(legacy, "pc-01", null, sharedHistory: true));
        Assert.NotNull(InterventionHistory.CheckSameMachine(legacy, "PC-02", null, sharedHistory: true));
        // Historique local (%ProgramData%) : toujours celui du poste, même renommé depuis.
        Assert.Null(InterventionHistory.CheckSameMachine(legacy, "PC-RENOMME", null, sharedHistory: false));
    }

    // ------------------------------------------------------------------ dépôt de la clé

    [Fact]
    public void Key_repository_is_used_automatically_and_never_saved()
    {
        var layout = AppPaths.Resolve(null, Exe, ProgramData, LocalAppData, "PC", Guid1, MarkerIn(Exe));
        var depot = AppPaths.DefaultRepositoryFor(layout, d => d == @"E:\SpokaPilote\Depot");
        Assert.Equal(@"E:\SpokaPilote\Depot", depot);
        Assert.Null(AppPaths.DefaultRepositoryFor(layout, _ => false)); // dossier supprimé : ignoré

        var settings = new Settings { RepositoryPaths = { @"\\srv\pilotes", @"Depot\Dell", @"E:\SpokaPilote\Depot\" } };
        var effective = settings.EffectiveRepositoryPaths(depot, layout.PortableHome);
        Assert.Equal(new[] { @"E:\SpokaPilote\Depot", @"\\srv\pilotes", @"E:\SpokaPilote\Depot\Dell" }, effective);
        Assert.Equal(3, settings.RepositoryPaths.Count); // la configuration enregistrée n'est pas modifiée

        // Version installée : rien n'est ajouté, les chemins restent tels quels.
        Assert.Equal(new[] { @"\\srv\pilotes", @"Depot\Dell", @"E:\SpokaPilote\Depot\" },
            settings.EffectiveRepositoryPaths(null, null));
    }

    // ------------------------------------------------------------------ tâches planifiées

    [Fact]
    public void Scheduled_tasks_are_refused_for_a_portable_copy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spokapilote-cle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "spokapilote.exe");
            Assert.NotEqual(MaintenanceScheduler.PortableRefusal, MaintenanceScheduler.ValidateExecutable(exe)); // refusé, mais pour le dossier temporaire
            File.WriteAllText(Path.Combine(dir, AppPaths.PortableMarkerFileName), "");
            Assert.True(AppPaths.IsPortableDirectory(dir));
            Assert.Equal(MaintenanceScheduler.PortableRefusal, MaintenanceScheduler.ValidateExecutable(exe));
            Assert.Contains("Mode portable", MaintenanceScheduler.PortableRefusal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ mise à jour

    private static JsonElement ReleaseWithPortableFirst() => JsonDocument.Parse("""
        {"tag_name":"v3.1.0","html_url":"https://github.com/Spokator/SpokaPilote/releases/tag/v3.1.0","assets":[
          {"name":"SpokaPilote-Portable-3.1.0-x64.zip","browser_download_url":"https://github.com/a/p-x64.zip","size":5000,
           "digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"},
          {"name":"SpokaPilote-Portable-3.1.0-arm64.zip","browser_download_url":"https://github.com/a/p-arm.zip","size":6000,
           "digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},
          {"name":"SpokaPilote-Installer-3.1.0-arm64.exe","browser_download_url":"https://github.com/a/arm.exe","size":300},
          {"name":"SpokaPilote-Setup-3.1.0.exe","browser_download_url":"https://github.com/a/x64.exe","size":200}
        ]}
        """).RootElement;

    [Fact]
    public void Update_never_selects_a_portable_zip_as_installer()
    {
        var x64 = UpdateChecker.Parse(ReleaseWithPortableFirst(), new Version(3, 0, 0), "x64")!;
        Assert.Equal("https://github.com/a/x64.exe", x64.DownloadUrl);
        Assert.Equal(200, x64.Size);
        Assert.Equal("https://github.com/a/p-x64.zip", x64.PortableUrl);
        Assert.Equal(5000, x64.PortableSize);
        Assert.Equal(new string('1', 64), x64.PortableSha256);

        var arm = UpdateChecker.Parse(ReleaseWithPortableFirst(), new Version(3, 0, 0), "arm64")!;
        Assert.Equal("https://github.com/a/arm.exe", arm.DownloadUrl);
        Assert.Equal("https://github.com/a/p-arm.zip", arm.PortableUrl);

        // Release ne contenant que des archives portables : aucune mise à jour par installeur proposée.
        var onlyZip = JsonDocument.Parse("""
            {"tag_name":"v3.1.0","assets":[{"name":"SpokaPilote-Portable-3.1.0-x64.zip","browser_download_url":"https://github.com/a/p.zip"}]}
            """).RootElement;
        Assert.Null(UpdateChecker.Parse(onlyZip, new Version(3, 0, 0), "x64"));
    }

    [Fact]
    public void Portable_asset_names_are_invisible_to_old_clients()
    {
        foreach (var arch in new[] { "x64", "arm64" })
        {
            var name = UpdateChecker.PortableAssetName("3.1.0", arch);
            Assert.Equal($"SpokaPilote-Portable-3.1.0-{arch}.zip", name);
            Assert.DoesNotContain("Setup", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Installer", name, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".zip", name);
        }
        // Logique des versions 1.0.x : premier fichier dont le nom contient « Setup ».
        var first = ReleaseWithPortableFirst().GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()!)
            .First(n => n.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("SpokaPilote-Setup-3.1.0.exe", first);
    }

    [Fact]
    public async Task Portable_download_requires_a_published_sha256()
    {
        var update = new UpdateInfo(new Version(3, 1, 0), "v3.1.0", "https://github.com/a/x64.exe", "https://github.com/r", "")
        {
            PortableUrl = "https://github.com/a/p-x64.zip",
            PortableSize = 10,
            PortableSha256 = null,
        };
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => UpdateChecker.DownloadPortableAsync(update, Path.GetTempPath(), null));
        // Release sans archive portable pour cette architecture : lien vers la page, pas de téléchargement.
        await Assert.ThrowsAsync<UpdateIntegrityException>(() =>
            UpdateChecker.DownloadPortableAsync(update with { PortableUrl = null }, Path.GetTempPath(), null));
    }
}
