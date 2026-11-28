using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Tests qui modifient l'état global des contrôles (<see cref="DataProtection.OverrideEnforcedForTest"/>, faux
/// <see cref="IFileSecurity"/>, emplacements) : jamais en parallèle avec les autres.
/// </summary>
[CollectionDefinition(nameof(GlobalDataProtectionState), DisableParallelization = true)]
public sealed class GlobalDataProtectionState;

/// <summary>Faux contrôle d'accès : propriétaires et DACL en mémoire, système de fichiers réel (dossier temporaire).</summary>
internal sealed class FakeFileSecurity : IFileSecurity
{
    public static readonly SecurityIdentifier Attacker = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private readonly Dictionary<string, (SecurityIdentifier? Owner, DaclState Dacl)> _state = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Propriétaire d'un élément jamais vu (créé « par un autre compte »).</summary>
    public SecurityIdentifier DefaultOwner { get; set; } = Attacker;
    public int ProtectedDaclWrites { get; private set; }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private (SecurityIdentifier? Owner, DaclState Dacl) Get(string path) =>
        _state.TryGetValue(Key(path), out var s) ? s : (DefaultOwner, new DaclState(false, [], false));

    public void Own(string path, SecurityIdentifier owner) => _state[Key(path)] = (owner, new DaclState(false, [], false));
    public SecurityIdentifier? OwnerOf(string path) => Get(path).Owner;
    public DaclState DaclOf(string path) => Get(path).Dacl;

    public SecurityIdentifier? GetOwner(string path, bool isDirectory) => Get(path).Owner;
    public DaclState? GetDacl(string path, bool isDirectory) => Get(path).Dacl;
    public void SetOwner(string path, bool isDirectory, SecurityIdentifier owner) => _state[Key(path)] = (owner, Get(path).Dacl);
    public void SetProtectedDacl(string path, bool isDirectory, IReadOnlyList<AclRule> rules)
    {
        ProtectedDaclWrites++;
        _state[Key(path)] = (Get(path).Owner, new DaclState(true, rules.ToList(), false));
    }
    public void ResetToInherited(string path, bool isDirectory) => _state[Key(path)] = (Get(path).Owner, new DaclState(false, [], false));
    public void CreateProtectedDirectory(string path, IReadOnlyList<AclRule> rules)
    {
        Directory.CreateDirectory(path);
        _state[Key(path)] = (DataProtection.AdministratorsSid, new DaclState(true, rules.ToList(), false));
    }
    public void EnableOwnershipPrivileges() { }
    /// <summary>Chemins dont la DACL a été posée sans propagation (dossier Mount occupé).</summary>
    public List<string> OwnDaclOnlyWrites { get; } = new();
    public void SetOwnDaclOnly(string path, bool isDirectory, IReadOnlyList<AclRule> rules)
    {
        OwnDaclOnlyWrites.Add(Key(path));
        _state[Key(path)] = (Get(path).Owner, new DaclState(true, rules.ToList(), false));
    }
    /// <summary>Faux type de volume (FAT32 / partage réseau quand false).</summary>
    public bool AclVolume { get; set; } = true;
    public bool SupportsOwnersAndAcls(string path) => AclVolume;
    /// <summary>Chemins touchés (propriétaire ou DACL) : rien sous Mount ne doit y figurer.</summary>
    public IEnumerable<string> KnownPaths => _state.Keys;
}

public sealed class DataRootProtectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "protect-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "outside-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileSecurity _security = new();

    public void Dispose()
    {
        DataProtection.DeleteTree(_root);
        DataProtection.DeleteTree(_outside);
    }

    private string Make(string relative, string content = "x", SecurityIdentifier? owner = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (owner is not null) _security.Own(path, owner);
        return path;
    }

    private void OwnDir(string relative, SecurityIdentifier owner)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        _security.Own(path, owner);
    }

    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "jonction non créée");
    }

    [Fact]
    public void Full_pass_locks_the_root_and_quarantines_or_deletes_what_users_planted()
    {
        var admins = DataProtection.AdministratorsSid;
        Directory.CreateDirectory(_root);
        _security.Own(_root, FakeFileSecurity.Attacker); // dossier racine créé d'avance par un utilisateur
        var settings = Make("settings.json", """{ "extraTrustedPublishers": ["CN=Pirate"] }""", FakeFileSecurity.Attacker);
        var cache = Make(@"Cache\dell-CatalogPC.xml", "<faux/>", FakeFileSecurity.Attacker);
        var history = Make(@"History\20261101-101010.json", "{}", admins);
        OwnDir("Logs", FakeFileSecurity.Attacker); // créé par l'interface non élevée d'une version antérieure
        var log = Make(@"Logs\spokapilote-20261101.log", "journal", admins);
        var dll = Make(@"Extracted\pkg\version.dll", "MZ", FakeFileSecurity.Attacker);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "cible.txt"), "ne pas toucher");
        Junction(Path.Combine(_root, "Downloads", "lien"), _outside);

        var report = new DataRootProtector(_security).Ensure(_root, new DateTime(2026, 11, 28, 10, 0, 0));

        Assert.True(report.FullPass);
        Assert.Equal(admins, _security.OwnerOf(_root));
        Assert.True(DataRootProtector.Matches(_security.DaclOf(_root), DataProtection.RootRules, isDirectory: true));
        // settings.json planté : mis en quarantaine (jamais lu), pas supprimé (un administrateur peut l'examiner).
        Assert.False(File.Exists(settings));
        Assert.Contains(report.Quarantined, q => q.StartsWith("settings.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, "Quarantaine", "20261128-100000", "settings.json")));
        // Données régénérables plantées : supprimées.
        Assert.False(File.Exists(cache));
        Assert.False(File.Exists(dll));
        // Fichiers légitimes conservés, y compris dans un dossier créé par un utilisateur (repris).
        Assert.True(File.Exists(history));
        Assert.True(File.Exists(log));
        Assert.Equal(admins, _security.OwnerOf(Path.Combine(_root, "Logs")));
        // Jonction retirée, cible intacte.
        Assert.False(Directory.Exists(Path.Combine(_root, "Downloads", "lien")));
        Assert.True(File.Exists(Path.Combine(_outside, "cible.txt")));
        Assert.Contains("Downloads", report.Renewed); // dossier régénérable renouvelé : le lien est parti avec l'ancien
        // Dossiers privés : sans accès des utilisateurs.
        foreach (var name in new[] { "Cache", "Work", "Quarantaine" })
            Assert.True(DataRootProtector.Matches(_security.DaclOf(Path.Combine(_root, name)), DataProtection.PrivateRules, isDirectory: true), name);
        Assert.True(File.Exists(Path.Combine(_root, DataRootProtector.MarkerFileName)));
    }

    [Fact]
    public void Second_start_is_a_cheap_check_and_tampering_triggers_a_new_full_pass()
    {
        var protector = new DataRootProtector(_security);
        Assert.True(protector.Ensure(_root, DateTime.Now).FullPass);
        _security.DefaultOwner = DataProtection.SystemSid; // tout ce qui suit est écrit par le service

        var writes = _security.ProtectedDaclWrites;
        var again = protector.Ensure(_root, DateTime.Now);
        Assert.False(again.FullPass);
        Assert.Equal(writes, _security.ProtectedDaclWrites);

        // Droits du dossier modifiés depuis (ACE ajoutée à la main) : reprise complète.
        _security.ResetToInherited(_root, true);
        Assert.True(protector.Ensure(_root, DateTime.Now).FullPass);
        Assert.False(protector.Ensure(_root, DateTime.Now).FullPass);

        // Élément de premier niveau d'un autre propriétaire : reprise complète.
        Make("intrus.txt", "x", FakeFileSecurity.Attacker);
        var third = protector.Ensure(_root, DateTime.Now);
        Assert.True(third.FullPass);
        Assert.Contains(third.Quarantined, q => q.StartsWith("intrus.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Token_file_keeps_its_admin_only_acl()
    {
        Make(SecretStore.TokenFileName, "secret", DataProtection.AdministratorsSid);
        new DataRootProtector(_security).Ensure(_root, DateTime.Now);
        Assert.True(DataRootProtector.Matches(_security.DaclOf(Path.Combine(_root, SecretStore.TokenFileName)), DataProtection.PrivateRules, isDirectory: false));
        Assert.True(DataRootProtector.Matches(_security.DaclOf(_root), DataProtection.RootRules, isDirectory: true));
    }

    [Fact]
    public void Expected_dacl_comparison_is_exact()
    {
        var root = DataProtection.RootRules;
        Assert.True(DataRootProtector.Matches(new DaclState(true, root.ToList(), false), root, true));
        // Synchronize ajouté par Windows : ignoré.
        Assert.True(DataRootProtector.Matches(new DaclState(true, root.Select(r => r with { Rights = r.Rights | FileSystemRights.Synchronize }).ToList(), false), root, true));
        Assert.False(DataRootProtector.Matches(new DaclState(false, root.ToList(), false), root, true)); // héritage actif
        Assert.False(DataRootProtector.Matches(new DaclState(true, root.ToList(), true), root, true)); // règle « deny »
        Assert.False(DataRootProtector.Matches(new DaclState(true, [.. root, new AclRule(DataProtection.UsersSid, FileSystemRights.Write, true)], false), root, true));
        Assert.False(DataRootProtector.Matches(new DaclState(true, [new AclRule(DataProtection.UsersSid, FileSystemRights.Modify, true), .. root.Skip(0).Take(2)], false), root, true));
        Assert.False(DataRootProtector.Matches(null, root, true));
    }

    [Fact]
    public void Misconfigured_home_is_never_locked_down()
    {
        Assert.False(DataProtection.LooksLikeDataRoot(@"C:\", DataLocationMode.Override));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "rapport.docx"), "x");
        Assert.False(DataProtection.LooksLikeDataRoot(_outside, DataLocationMode.Override));
        Assert.True(DataProtection.LooksLikeDataRoot(_root, DataLocationMode.Override)); // n'existe pas encore
        Make("settings.json");
        Assert.True(DataProtection.LooksLikeDataRoot(_root, DataLocationMode.Override));
    }

    [Fact]
    public void User_folder_that_merely_contains_logs_or_reports_is_not_a_data_root()
    {
        // Dossier de l'utilisateur avec des noms courants : jamais repris ni mis en quarantaine.
        Directory.CreateDirectory(Path.Combine(_outside, "Logs"));
        Directory.CreateDirectory(Path.Combine(_outside, "Reports"));
        Assert.False(DataProtection.LooksLikeDataRoot(_outside, DataLocationMode.Override)); // pas de settings.json
        File.WriteAllText(Path.Combine(_outside, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_outside, "compta.xlsx"), "x");
        Assert.False(DataProtection.LooksLikeDataRoot(_outside, DataLocationMode.Override)); // élément inconnu
        File.Delete(Path.Combine(_outside, "compta.xlsx"));
        Assert.True(DataProtection.LooksLikeDataRoot(_outside, DataLocationMode.Override)); // uniquement des éléments SpokaPilote
        // Dossier déjà protégé (témoin) : reconnu même avec un élément ajouté depuis (mis en quarantaine).
        File.WriteAllText(Path.Combine(_outside, "intrus.exe"), "MZ");
        File.WriteAllText(Path.Combine(_outside, DataRootProtector.MarkerFileName), DataRootProtector.MarkerContent);
        Assert.True(DataProtection.LooksLikeDataRoot(_outside, DataLocationMode.Override));
    }

    [Fact]
    public void Full_pass_renews_cache_and_work_so_handles_opened_before_point_to_a_deleted_folder()
    {
        Make(@"Cache\dell-CatalogPC.xml", "<ok/>", DataProtection.SystemSid);
        Make(@"Work\install-1a2b\setup.exe", "MZ", DataProtection.SystemSid);
        var cacheBefore = new DirectoryInfo(Path.Combine(_root, "Cache"));

        var report = new DataRootProtector(_security).Ensure(_root, DateTime.Now);

        Assert.True(report.FullPass);
        Assert.False(report.Incomplete);
        Assert.Contains("Cache", report.Renewed);
        Assert.Contains("Work", report.Renewed);
        // Dossiers neufs, vides, privés ; anciens supprimés (aucun « Cache.ancien-* » restant).
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Work")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_root), d => DataRootProtector.IsRenewedLeftover(Path.GetFileName(d)));
        Assert.True(DataRootProtector.Matches(_security.DaclOf(Path.Combine(_root, "Cache")), DataProtection.PrivateRules, isDirectory: true));
        Assert.True(File.Exists(Path.Combine(_root, DataRootProtector.MarkerFileName)));
        cacheBefore.Refresh();
        Assert.True(cacheBefore.Exists); // même chemin, nouveau dossier
    }

    [Fact]
    public void Folder_with_an_open_file_is_not_renewed_and_the_full_pass_is_retried_at_next_start()
    {
        var held = Make(@"Cache\verrou.bin", "x", DataProtection.SystemSid);
        var protector = new DataRootProtector(_security);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var first = protector.Ensure(_root, DateTime.Now);
            Assert.True(first.FullPass);
            Assert.True(first.Incomplete);
            Assert.DoesNotContain("Cache", first.Renewed);
            Assert.Contains(first.Errors, e => e.StartsWith("Cache", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(_root, DataRootProtector.MarkerFileName))); // pas de témoin : on recommencera
            Assert.True(protector.Ensure(_root, DateTime.Now).FullPass);
        }
        var later = protector.Ensure(_root, DateTime.Now);
        Assert.True(later.FullPass);
        Assert.False(later.Incomplete);
        Assert.Contains("Cache", later.Renewed);
        Assert.False(File.Exists(held));
        _security.DefaultOwner = DataProtection.SystemSid;
        Assert.False(protector.Ensure(_root, DateTime.Now).FullPass);
    }

    [Fact]
    public void Planted_link_inside_cache_triggers_a_full_pass_even_when_the_first_level_is_compliant()
    {
        var protector = new DataRootProtector(_security);
        protector.Ensure(_root, DateTime.Now);
        _security.DefaultOwner = DataProtection.SystemSid;
        Assert.False(protector.Ensure(_root, DateTime.Now).FullPass);

        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "cible.txt"), "ne pas toucher");
        Junction(Path.Combine(_root, "Cache", "catalog"), _outside); // descripteur sur Cache ouvert avant la protection
        var report = protector.Ensure(_root, DateTime.Now);
        Assert.True(report.FullPass);
        Assert.False(Directory.Exists(Path.Combine(_root, "Cache", "catalog")));
        Assert.True(File.Exists(Path.Combine(_outside, "cible.txt")));
    }

    [Fact]
    public void Mounted_image_is_never_touched_only_the_mount_folder_acl()
    {
        // Image montée par DISM pendant une reprise complète : fichiers d'autres propriétaires (TrustedInstaller, comptes
        // de l'image), dossier de montage en lien : rien n'est supprimé, repris ni réinitialisé sous Mount.
        var image = Path.Combine(_root, "Mount", "20261128-100000-1");
        var system32 = Make(@"Mount\20261128-100000-1\Windows\System32\ntoskrnl.exe", "MZ", FakeFileSecurity.Attacker);
        _security.Own(image, FakeFileSecurity.Attacker);
        Directory.CreateDirectory(_outside);
        Junction(Path.Combine(_root, "Mount", "20261128-100000-2"), _outside);
        OwnDir("Mount", FakeFileSecurity.Attacker);

        var report = new DataRootProtector(_security).Ensure(_root, DateTime.Now);

        Assert.True(report.MountInUse);
        Assert.True(File.Exists(system32));
        Assert.True(Directory.Exists(Path.Combine(_root, "Mount", "20261128-100000-2")));
        Assert.Equal(FakeFileSecurity.Attacker, _security.OwnerOf(system32));
        Assert.Equal(FakeFileSecurity.Attacker, _security.OwnerOf(image));
        Assert.False(_security.DaclOf(image).IsProtected);
        Assert.DoesNotContain(report.LinksRemoved, l => l.StartsWith("Mount", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Deleted, d => d.StartsWith("Mount", StringComparison.OrdinalIgnoreCase));
        // Le dossier Mount lui-même : repris, DACL privée posée sans propagation.
        var mount = Path.GetFullPath(Path.Combine(_root, "Mount"));
        Assert.Equal(DataProtection.AdministratorsSid, _security.OwnerOf(mount));
        Assert.Contains(mount, _security.OwnDaclOnlyWrites);
        Assert.True(DataRootProtector.Matches(_security.DaclOf(mount), DataProtection.PrivateRules, isDirectory: true));
        Assert.DoesNotContain(_security.KnownPaths, p => p.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && p != Path.GetFullPath(image) && p != Path.GetFullPath(system32));
    }

    [Fact]
    public void Empty_mount_folder_is_renewed_like_the_other_regenerable_folders()
    {
        OwnDir("Mount", FakeFileSecurity.Attacker);
        var report = new DataRootProtector(_security).Ensure(_root, DateTime.Now);
        Assert.False(report.MountInUse);
        Assert.Contains("Mount", report.Renewed);
        Assert.False(Directory.Exists(Path.Combine(_root, "Mount"))); // recréé à la demande (AppPaths.Mount), privé
        Assert.Empty(_security.OwnDaclOnlyWrites);
    }
}

[Collection(nameof(GlobalDataProtectionState))]
public sealed class ElevatedUseChecksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "trust-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileSecurity _security = new();
    private readonly IFileSecurity _previous = DataProtection.Security;

    public ElevatedUseChecksTests()
    {
        Directory.CreateDirectory(_dir);
        DataProtection.Security = _security;
    }

    public void Dispose()
    {
        DataProtection.Security = _previous;
        DataProtection.DeleteTree(_dir);
    }

    private string File(string name, SecurityIdentifier owner)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, "<catalogue/>");
        _security.Own(path, owner);
        return path;
    }

    [Fact]
    public void Elevated_runs_only_trust_files_written_by_system_or_administrators()
    {
        var mine = File("a.xml", DataProtection.SystemSid);
        var planted = File("b.xml", FakeFileSecurity.Attacker);
        using (DataProtection.OverrideEnforcedForTest(true))
        {
            Assert.True(DataProtection.IsTrustedForElevatedUse(mine));
            Assert.False(DataProtection.IsTrustedForElevatedUse(planted));
        }
        using (DataProtection.OverrideEnforcedForTest(false))
            Assert.True(DataProtection.IsTrustedForElevatedUse(planted)); // non élevé / portable : rien à protéger
    }

    [Fact]
    public void Cached_catalog_dated_in_the_future_or_planted_is_stale()
    {
        var now = DateTime.UtcNow;
        var fresh = File("fresh.xml", DataProtection.AdministratorsSid);
        System.IO.File.SetLastWriteTimeUtc(fresh, now.AddHours(-1));
        var future = File("future.xml", DataProtection.AdministratorsSid);
        System.IO.File.SetLastWriteTimeUtc(future, now.AddYears(10)); // « frais » pour toujours sans ce contrôle
        var old = File("old.xml", DataProtection.AdministratorsSid);
        System.IO.File.SetLastWriteTimeUtc(old, now.AddDays(-3));
        var planted = File("planted.xml", FakeFileSecurity.Attacker);
        System.IO.File.SetLastWriteTimeUtc(planted, now.AddHours(-1));

        using (DataProtection.OverrideEnforcedForTest(true))
        {
            Assert.True(Http.IsFresh(fresh, TimeSpan.FromHours(24), now));
            Assert.False(Http.IsFresh(future, TimeSpan.FromHours(24), now));
            Assert.False(Http.IsFresh(old, TimeSpan.FromHours(24), now));
            Assert.False(Http.IsFresh(planted, TimeSpan.FromHours(24), now));
        }
        using (DataProtection.OverrideEnforcedForTest(false))
        {
            Assert.True(Http.IsFresh(planted, TimeSpan.FromHours(24), now));
            Assert.False(Http.IsFresh(future, TimeSpan.FromHours(24), now)); // date future : périmé dans tous les cas
        }
    }

    [Fact]
    public void Planted_history_entry_is_ignored_by_elevated_runs()
    {
        var entry = Path.Combine(AppPaths.History, $"planted-{Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(entry, System.Text.Json.JsonSerializer.Serialize(new Intervention { Id = Path.GetFileNameWithoutExtension(entry), AddedDriverPackages = { "oem1.inf" } }));
        _security.Own(entry, FakeFileSecurity.Attacker);
        try
        {
            using (DataProtection.OverrideEnforcedForTest(true))
                Assert.DoesNotContain(InterventionHistory.List(), i => i.Id == Path.GetFileNameWithoutExtension(entry));
            using (DataProtection.OverrideEnforcedForTest(false))
                Assert.Contains(InterventionHistory.List(), i => i.Id == Path.GetFileNameWithoutExtension(entry));
        }
        finally
        {
            System.IO.File.Delete(entry);
        }
    }

    /// <summary>Dossier de données de test = _dir (SPOKAPILOTE_HOME), le temps d'un test.</summary>
    private IDisposable UseDataRoot(string root)
    {
        var previous = AppPaths.Layout;
        AppPaths.Layout = new AppLayout(DataLocationMode.Override, root, root, null, Path.Combine(root, "ui.json"));
        DataProtection.ResetLocationCache();
        return new Restore(() => { AppPaths.Layout = previous; DataProtection.ResetLocationCache(); });
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private void OwnTree(string path, SecurityIdentifier owner)
    {
        for (var p = Path.GetFullPath(path); p.Length >= _dir.Length; p = Path.GetDirectoryName(p)!) _security.Own(p, owner);
    }

    [Fact]
    public void Every_folder_on_the_path_from_the_data_root_must_be_trusted_and_not_a_link()
    {
        using var layout = UseDataRoot(_dir);
        var catalog = Directory.CreateDirectory(Path.Combine(_dir, "Cache", "catalog")).FullName;
        var file = Path.Combine(catalog, "q.json");
        System.IO.File.WriteAllText(file, "[]");
        OwnTree(file, DataProtection.SystemSid);
        using (DataProtection.OverrideEnforcedForTest(true))
        {
            Assert.True(DataProtection.IsTrustedForElevatedUse(file));
            // Dossier intermédiaire créé par un utilisateur (descripteur ouvert avant la protection) : fichier refusé, même
            // s'il appartient lui-même à SYSTEM.
            _security.Own(catalog, FakeFileSecurity.Attacker);
            Assert.False(DataProtection.IsTrustedForElevatedUse(file));
            _security.Own(catalog, DataProtection.SystemSid);
            _security.Own(_dir, FakeFileSecurity.Attacker); // racine elle-même
            Assert.False(DataProtection.IsTrustedForElevatedUse(file));
        }
    }

    [Fact]
    public void Junction_anywhere_on_the_path_is_refused_and_replaced_without_being_followed()
    {
        using var layout = UseDataRoot(_dir);
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "cible-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var target = Path.Combine(outside, "q.json");
            System.IO.File.WriteAllText(target, "[]");
            _security.Own(target, DataProtection.SystemSid);
            var cache = Directory.CreateDirectory(Path.Combine(_dir, "Cache")).FullName;
            OwnTree(cache, DataProtection.AdministratorsSid);
            var link = Path.Combine(cache, "catalog");
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                p.WaitForExit();
            _security.Own(link, DataProtection.AdministratorsSid); // un lien ne se juge pas à son propriétaire

            using (DataProtection.OverrideEnforcedForTest(true))
            {
                Assert.False(DataProtection.IsTrustedForElevatedUse(Path.Combine(link, "q.json")));
                _security.DefaultOwner = DataProtection.SystemSid; // dossiers recréés par le processus élevé
                var safe = DataProtection.EnsureTrustedSubdirectory(cache, "catalog");
                Assert.Equal(link, safe);
                Assert.Equal(0, (int)(new DirectoryInfo(safe).Attributes & FileAttributes.ReparsePoint));
                Assert.Empty(Directory.EnumerateFileSystemEntries(safe));
                Assert.True(System.IO.File.Exists(target)); // cible du lien intacte
            }
        }
        finally
        {
            DataProtection.DeleteTree(outside);
        }
    }

    [Fact]
    public void Home_on_a_volume_without_acls_is_an_unprotected_location_like_a_portable_key()
    {
        using var layout = UseDataRoot(_dir);
        Assert.False(DataProtection.UnprotectedLocation);
        Assert.True(SecretStore.UsesTokenFile);

        _security.AclVolume = false; // FAT32 / exFAT / partage réseau
        DataProtection.ResetLocationCache();
        Assert.True(DataProtection.UnprotectedLocation);
        Assert.False(SecretStore.UsesTokenFile); // jeton dans settings.json, comme sur une clé portable
        // Aucun droit modifié, rien de mis en quarantaine, même sur un dossier qui contient des données d'un utilisateur.
        System.IO.File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, "Reports"));
        var userFile = Path.Combine(_dir, "Reports", "rapport.html");
        System.IO.File.WriteAllText(userFile, "x");
        _security.Own(userFile, FakeFileSecurity.Attacker);
        Assert.Null(DataProtection.Protect(_dir, forceFullPass: true));
        Assert.True(System.IO.File.Exists(userFile));
        Assert.Equal(0, _security.ProtectedDaclWrites);
    }

    [Fact]
    public void Portable_layout_keeps_policy_and_rollout_caches_per_machine()
    {
        var previous = AppPaths.Layout;
        var key = Path.Combine(_dir, "Data");
        var machine = Path.Combine(key, AppPaths.MachinesFolderName, "PC-01_aaaaaaaa");
        try
        {
            AppPaths.Layout = new AppLayout(DataLocationMode.Portable, key, machine, _dir, Path.Combine(key, "ui.json"));
            Assert.Equal(Path.Combine(machine, "policy-cache.json"), AppPaths.PolicyCache);
            Assert.Equal(Path.Combine(machine, "rollout-decisions.json"), AppPaths.RolloutDecisionsCache);

            // Anciens caches partagés de la clé (4.0.0) : supprimés, jamais réutilisés par un autre poste.
            System.IO.File.WriteAllText(Path.Combine(key, "policy-cache.json"), "{}");
            System.IO.File.WriteAllText(Path.Combine(key, "rollout-decisions.json"), """{"origin":"https://console","decisions":{}}""");
            DataProtection.RemoveLegacySharedCaches(AppPaths.Layout);
            Assert.False(System.IO.File.Exists(Path.Combine(key, "policy-cache.json")));
            Assert.False(System.IO.File.Exists(Path.Combine(key, "rollout-decisions.json")));
        }
        finally
        {
            AppPaths.Layout = previous;
        }
    }
}

public sealed class NonElevatedLayoutTests
{
    [Fact]
    public void Installed_version_run_without_elevation_writes_under_local_app_data()
    {
        var elevated = AppPaths.Resolve(null, @"C:\Program Files\SpokaPilote", @"C:\ProgramData", @"C:\Users\u\AppData\Local", "PC", null, _ => false, elevated: true);
        var user = AppPaths.Resolve(null, @"C:\Program Files\SpokaPilote", @"C:\ProgramData", @"C:\Users\u\AppData\Local", "PC", null, _ => false, elevated: false);
        Assert.Null(elevated.UserRoot);
        Assert.Equal(@"C:\Users\u\AppData\Local\SpokaPilote", user.UserRoot);
        Assert.Equal(@"C:\ProgramData\SpokaPilote", user.Root); // configuration commune : lue seulement
        // Portable et SPOKAPILOTE_HOME : pas de redirection.
        Assert.Null(AppPaths.Resolve(@"D:\essai", @"C:\x", @"C:\ProgramData", @"C:\L", "PC", null, _ => false, elevated: false).UserRoot);
        Assert.Null(AppPaths.Resolve(null, @"E:\SpokaPilote", @"C:\ProgramData", @"C:\L", "PC", null, _ => true, elevated: false).UserRoot);
    }
}

public sealed class WorkDirectoryTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "work-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => DataProtection.DeleteTree(_parent);

    [Fact]
    public void Each_run_gets_a_fresh_directory_deleted_at_the_end()
    {
        string path;
        using (var a = WorkDirectory.Create(_parent, "install", protect: false))
        using (var b = WorkDirectory.Create(_parent, "install", protect: false))
        {
            Assert.NotEqual(a.Path, b.Path);
            path = a.Path;
            File.WriteAllText(Path.Combine(a.NewSubdirectory("x-pkg"), "setup.exe"), "MZ");
            Assert.True(File.Exists(Path.Combine(path, WorkDirectory.LockFileName)));
        }
        Assert.False(Directory.Exists(path));
        Assert.Empty(Directory.GetDirectories(_parent));
    }

    [Fact]
    public void Leftover_cleanup_removes_old_or_oversized_directories_but_never_one_in_use()
    {
        var now = DateTime.Now;
        using var running = WorkDirectory.Create(_parent, "install", protect: false);
        Directory.SetCreationTimeUtc(running.Path, now.ToUniversalTime().AddDays(-10)); // vieux mais en cours
        var old = Directory.CreateDirectory(Path.Combine(_parent, "install-old")).FullName;
        File.WriteAllText(Path.Combine(old, "a.cab"), "x");
        Directory.SetCreationTimeUtc(old, now.ToUniversalTime().AddDays(-3));
        var recent = Directory.CreateDirectory(Path.Combine(_parent, "install-recent")).FullName;
        File.WriteAllBytes(Path.Combine(recent, "big.cab"), new byte[4096]);

        Assert.Equal(1, WorkDirectory.CleanupLeftovers(_parent, now, TimeSpan.FromDays(2), maxTotalBytes: 1L << 30));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(running.Path));

        Assert.Equal(1, WorkDirectory.CleanupLeftovers(_parent, now, TimeSpan.FromDays(2), maxTotalBytes: 1024)); // place dépassée
        Assert.False(Directory.Exists(recent));
        Assert.True(Directory.Exists(running.Path));
    }

    [Fact]
    public void Tree_deletion_never_follows_a_junction()
    {
        var target = Directory.CreateDirectory(Path.Combine(_parent, "cible")).FullName;
        File.WriteAllText(Path.Combine(target, "garder.txt"), "x");
        var dir = Directory.CreateDirectory(Path.Combine(_parent, "install-x")).FullName;
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{Path.Combine(dir, "lien")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(DataProtection.DeleteTree(dir));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(target, "garder.txt")));
    }

    private sealed class Direct(byte[] content) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = request });
        }
    }

    [Fact]
    public async Task Downloads_never_reuse_a_file_prepared_in_a_shared_folder()
    {
        var official = Encoding.UTF8.GetBytes("installeur officiel " + Guid.NewGuid());
        var sha256 = Convert.ToHexString(SHA256.HashData(official));
        var name = $"setup-{Guid.NewGuid():N}.exe";
        // Ancien emplacement partagé (4.0.0) : fichier « déjà en cache » et « .part » préparés d'avance.
        var legacy = Directory.CreateDirectory(Path.Combine(AppPaths.WritableRoot, "Downloads")).FullName;
        File.WriteAllBytes(Path.Combine(legacy, name), official);
        File.WriteAllText(Path.Combine(legacy, name + ".part"), "piégé");

        var direct = new Direct(official);
        using var work = WorkDirectory.Create(_parent, "install", protect: false);
        using var downloader = new Downloader(new TrustPolicy(new Settings()), null, new HttpClient(direct));
        var path = await downloader.DownloadAsync($"https://downloads.dell.com/FOLDER1/{name}", sha256: sha256, directory: work.Path);

        Assert.Equal(1, direct.Requests);
        Assert.StartsWith(work.Path, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(official, await File.ReadAllBytesAsync(path));

        // Même fichier demandé à nouveau pendant la même exécution : réutilisé (vérifié par son empreinte).
        Assert.Equal(path, await downloader.DownloadAsync($"https://downloads.dell.com/FOLDER1/{name}", sha256: sha256, directory: work.Path));
        Assert.Equal(1, direct.Requests);
    }
}

/// <summary>Contrôle réel des ACL dans un dossier de test : seulement dans un processus élevé (ignoré sinon).</summary>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        if (!Elevation.IsAdministrator()) Skip = "Nécessite un processus élevé (contrôle réel des ACL).";
    }
}

public sealed class RealAclTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "acl-" + Guid.NewGuid().ToString("N"), "SpokaPilote");

    public void Dispose() => DataProtection.DeleteTree(Path.GetDirectoryName(_root)!);

    [Fact]
    public void Mount_acl_is_set_without_rewriting_the_acls_of_the_mounted_image()
    {
        var security = WindowsFileSecurity.Instance;
        Assert.True(security.SupportsOwnersAndAcls(Path.GetTempPath())); // NTFS local
        Assert.False(security.SupportsOwnersAndAcls(@"\\serveur\partage\SpokaPilote"));
        var mount = Directory.CreateDirectory(Path.Combine(_root, "Mount")).FullName;
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(mount, "img", "Windows")).FullName, "fichier.sys");
        File.WriteAllText(file, "x");
        string Sddl() => new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var before = Sddl();
        using var me = WindowsIdentity.GetCurrent();
        IReadOnlyList<AclRule> rules = [new(DataProtection.SystemSid, FileSystemRights.FullControl, true), new(me.User!, FileSystemRights.FullControl, true)];

        security.SetOwnDaclOnly(mount, true, rules);
        Assert.True(DataRootProtector.Matches(security.GetDacl(mount, true), rules, isDirectory: true));
        Assert.Equal(before, Sddl()); // aucune propagation sous Mount

        security.SetProtectedDacl(mount, true, rules); // SetAccessControl : propagation (ce que Mount évite)
        Assert.NotEqual(before, Sddl());
    }

    [ElevatedFact]
    public void Real_acls_match_the_documented_layout()
    {
        var security = WindowsFileSecurity.Instance;
        security.EnableOwnershipPrivileges();
        Directory.CreateDirectory(Path.Combine(_root, "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "History"));
        var planted = Path.Combine(_root, "settings.json");
        File.WriteAllText(planted, "{}");
        security.SetOwner(planted, false, DataProtection.UsersSid); // comme un fichier déposé par un utilisateur
        var cache = Path.Combine(_root, "Cache", "catalog.xml");
        File.WriteAllText(cache, "<x/>");
        security.SetOwner(cache, false, DataProtection.UsersSid);
        var history = Path.Combine(_root, "History", "a.json");
        File.WriteAllText(history, "{}");
        SecretStore.WriteToken(Path.Combine(_root, SecretStore.TokenFileName), "jeton-de-test");

        var report = new DataRootProtector(security).Ensure(_root, DateTime.Now);

        Assert.True(report.FullPass);
        Assert.Empty(report.Errors);
        Assert.True(DataProtection.IsTrustedOwner(security.GetOwner(_root, true)));
        Assert.True(DataRootProtector.Matches(security.GetDacl(_root, true), DataProtection.RootRules, true));
        Assert.True(DataRootProtector.Matches(security.GetDacl(Path.Combine(_root, "Cache"), true), DataProtection.PrivateRules, true));
        Assert.True(DataRootProtector.Matches(security.GetDacl(Path.Combine(_root, SecretStore.TokenFileName), false), DataProtection.PrivateRules, false));
        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(history));
        // Héritage effectif : aucune règle explicite sous la racine, Utilisateurs en lecture seule.
        var inherited = new FileInfo(history).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();
        Assert.All(inherited, r => Assert.True(r.IsInherited));
        Assert.DoesNotContain(inherited, r => r.IdentityReference.Value == DataProtection.UsersSid.Value &&
                                              (r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions)) != 0);
        Assert.DoesNotContain(inherited, r => r.IdentityReference.Value == "S-1-3-0"); // CREATOR OWNER
        Assert.Equal("jeton-de-test", SecretStore.ReadToken(Path.Combine(_root, SecretStore.TokenFileName)));
        // Fichier mis en quarantaine : plus aucun accès des utilisateurs (il pourrait contenir un jeton).
        var quarantined = Directory.GetFiles(Path.Combine(_root, DataRootProtector.QuarantineFolderName), "settings.json", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain(new FileInfo(quarantined).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            r => r.IdentityReference.Value == DataProtection.UsersSid.Value);
        Assert.False(new DataRootProtector(security).Ensure(_root, DateTime.Now).FullPass);
    }
}
