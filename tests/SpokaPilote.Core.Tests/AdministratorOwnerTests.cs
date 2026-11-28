using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Tests;

/// <summary>Faux annuaire : membres directs, membres par un groupe imbriqué, administrateur élevé courant, domaine injoignable.</summary>
internal sealed class FakeMembership : IAdministratorsMembership
{
    public HashSet<SecurityIdentifier> Direct { get; } = new();
    public HashSet<SecurityIdentifier> Nested { get; } = new();
    public SecurityIdentifier? Elevated { get; set; }
    /// <summary>Domaine injoignable : l'appartenance imbriquée ne peut pas être tranchée.</summary>
    public bool Offline { get; set; }
    public int NestedQueries { get; private set; }

    public IReadOnlyCollection<SecurityIdentifier>? DirectMembers() => Direct.ToList();

    public bool? IsMemberIncludingNested(SecurityIdentifier sid)
    {
        NestedQueries++;
        return Offline ? null : Nested.Contains(sid) || Direct.Contains(sid);
    }

    public SecurityIdentifier? ElevatedProcessUser() => Elevated;
}

/// <summary>Comptes de test (SID d'un domaine de comptes fictif).</summary>
internal static class Accounts
{
    /// <summary>PC\Pierre : membre direct du groupe Administrateurs (UAC sans invite : propriétaire de ses fichiers).</summary>
    public static readonly SecurityIdentifier AdminUser = new("S-1-5-21-4444444444-5555555555-6666666666-1001");
    /// <summary>DOMAINE\technicien : membre de « Admins du domaine », lui-même membre des Administrateurs du poste.</summary>
    public static readonly SecurityIdentifier DomainTechnician = new("S-1-5-21-7777777777-8888888888-9999999999-1105");
    /// <summary>Administrateur qui exécute SpokaPilote maintenant (processus élevé).</summary>
    public static readonly SecurityIdentifier CurrentAdmin = new("S-1-5-21-4444444444-5555555555-6666666666-1002");
    /// <summary>Utilisateur standard (aucune appartenance).</summary>
    public static SecurityIdentifier StandardUser => FakeFileSecurity.Attacker;
}

[Collection(nameof(GlobalDataProtectionState))]
public sealed class AdministratorOwnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "admin-owner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileSecurity _security = new() { DefaultOwner = DataProtection.SystemSid };
    private readonly FakeMembership _membership = new();
    private readonly IFileSecurity _previousSecurity = DataProtection.Security;
    private readonly AppLayout _previousLayout = AppPaths.Layout;
    private readonly IDisposable _membershipOverride;
    private readonly IDisposable _enforced = DataProtection.OverrideEnforcedForTest(false); // pas d'ACL réelles (processus non élevé)
    private readonly List<LogEntry> _log = new();
    private static readonly List<Action<LogEntry>> Listeners = new();

    static AdministratorOwnerTests() => Log.Subscribe(e => { lock (Listeners) foreach (var l in Listeners) l(e); });

    public AdministratorOwnerTests()
    {
        Directory.CreateDirectory(_root);
        DataProtection.Security = _security;
        _membership.Direct.Add(Accounts.AdminUser);
        _membership.Nested.Add(Accounts.DomainTechnician);
        _membership.Elevated = Accounts.CurrentAdmin;
        _membershipOverride = DataProtection.OverrideMembershipForTest(_membership);
        AppPaths.Layout = new AppLayout(DataLocationMode.Override, _root, _root, null, Path.Combine(_root, "ui.json"));
        DataProtection.ResetLocationCache();
        lock (Listeners) Listeners.Add(Capture);
    }

    private void Capture(LogEntry e)
    {
        lock (_log) _log.Add(e);
    }

    public void Dispose()
    {
        lock (Listeners) Listeners.Remove(Capture);
        _enforced.Dispose();
        _membershipOverride.Dispose();
        DataProtection.Security = _previousSecurity;
        AppPaths.Layout = _previousLayout;
        DataProtection.ResetLocationCache();
        DataProtection.DeleteTree(_root);
    }

    private string Make(string relative, string content, SecurityIdentifier owner)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _security.Own(path, owner);
        return path;
    }

    private static JsonObject Raw(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    // ------------------------------------------------------------------ règle de confiance

    [Fact]
    public void Members_of_the_local_administrators_group_are_trusted_owners()
    {
        Assert.True(DataProtection.IsTrustedOwner(DataProtection.SystemSid));
        Assert.True(DataProtection.IsTrustedOwner(DataProtection.AdministratorsSid));
        Assert.True(DataProtection.IsTrustedOwner(DataProtection.TrustedInstallerSid));
        Assert.True(DataProtection.IsTrustedOwner(Accounts.AdminUser));          // membre direct
        Assert.True(DataProtection.IsTrustedOwner(Accounts.DomainTechnician));   // groupe de domaine imbriqué
        Assert.True(DataProtection.IsTrustedOwner(Accounts.CurrentAdmin));       // administrateur élevé courant
        Assert.False(DataProtection.IsTrustedOwner(Accounts.StandardUser));
        Assert.False(DataProtection.IsTrustedOwner(null));
        // Seuls SYSTEM, Administrateurs et TrustedInstaller sont des propriétaires « attendus » (rétablis sur nos écritures).
        Assert.False(DataProtection.IsBuiltInTrustedOwner(Accounts.AdminUser));
    }

    [Fact]
    public void Generic_groups_guests_and_domain_users_are_never_trusted_even_if_added_to_administrators()
    {
        SecurityIdentifier[] never =
        [
            DataProtection.UsersSid,
            new(WellKnownSidType.WorldSid, null),                  // Tout le monde
            new(WellKnownSidType.AuthenticatedUserSid, null),      // Utilisateurs authentifiés
            new(WellKnownSidType.InteractiveSid, null),            // INTERACTIF
            new(WellKnownSidType.CreatorOwnerSid, null),           // CREATOR OWNER
            new(WellKnownSidType.BuiltinGuestsSid, null),
            new(WellKnownSidType.NetworkServiceSid, null),
            new(WellKnownSidType.LocalServiceSid, null),
            new("S-1-5-21-4444444444-5555555555-6666666666-501"),  // Invité
            new("S-1-5-21-7777777777-8888888888-9999999999-513"),  // Utilisateurs du domaine
            new("S-1-5-21-7777777777-8888888888-9999999999-515"),  // Ordinateurs du domaine
            new("S-1-5-21-4444444444-5555555555-6666666666"),      // domaine sans RID
        ];
        foreach (var sid in never)
        {
            _membership.Direct.Add(sid); // ajouté par erreur au groupe Administrateurs
            _membership.Nested.Add(sid);
        }
        AdministratorOwners.ResetCache();
        foreach (var sid in never) Assert.False(DataProtection.IsTrustedOwner(sid), sid.Value);
        // Entra ID (S-1-12-1-…) : éligible, reconnu s'il est membre.
        var entra = new SecurityIdentifier("S-1-12-1-1111111111-2222222222-3333333333-4444444444");
        Assert.False(DataProtection.IsTrustedOwner(entra));
        _membership.Direct.Add(entra);
        AdministratorOwners.ResetCache();
        Assert.True(DataProtection.IsTrustedOwner(entra));
    }

    [Fact]
    public void Nested_membership_is_unknown_offline_and_answers_are_cached_per_process()
    {
        _membership.Offline = true;
        AdministratorOwners.ResetCache();
        Assert.False(DataProtection.IsTrustedOwner(Accounts.DomainTechnician)); // domaine injoignable : non reconnu
        Assert.True(DataProtection.IsTrustedOwner(Accounts.AdminUser));         // membre direct : sans le domaine
        var queries = _membership.NestedQueries;
        Assert.False(DataProtection.IsTrustedOwner(Accounts.DomainTechnician));
        Assert.Equal(queries, _membership.NestedQueries); // mémorisé

        _membership.Offline = false;
        AdministratorOwners.ResetCache(); // nouveau processus
        Assert.True(DataProtection.IsTrustedOwner(Accounts.DomainTechnician));
    }

    // ------------------------------------------------------------------ protection du dossier

    [Fact]
    public void Settings_logs_and_reports_written_by_an_administrator_account_are_kept()
    {
        // Poste 4.0.0 mis à jour, UAC sans invite : tout appartient à PC\Pierre (cas réel de la 4.0.1).
        var settings = Make("settings.json", """{ "serverUrl": "https://console.exemple.fr", "serverToken": "jeton-4-0-0" }""", Accounts.AdminUser);
        _security.Own(Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName, Accounts.AdminUser);
        var log = Make(@"Logs\spokapilote-20261127.log", "journal", Accounts.AdminUser);
        var report = Make(@"Reports\PC-PIERRE.html", "<html/>", Accounts.DomainTechnician);
        var planted = Make(@"History\planted.json", "{}", Accounts.StandardUser);

        var result = DataProtection.Protect(_root, forceFullPass: true)!;

        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(log));
        Assert.True(File.Exists(report));
        Assert.Empty(result.QuarantinedConfig);
        Assert.False(File.Exists(planted)); // fichier d'un utilisateur standard : toujours en quarantaine
        Assert.Contains(result.Quarantined, q => q.StartsWith(@"History\planted.json", StringComparison.Ordinal));
        // Propriétaire ramené au groupe Administrateurs : les tâches SYSTEM s'y fient sans interroger l'annuaire.
        Assert.Equal(DataProtection.AdministratorsSid, _security.OwnerOf(settings));
        Assert.Equal(DataProtection.AdministratorsSid, _security.OwnerOf(Path.Combine(_root, "Logs")));
        Assert.True(result.Adopted >= 4);
        Assert.Null(ConfigQuarantine.Pending().FirstOrDefault());
        Assert.Equal("jeton-4-0-0", Settings.Load().ServerToken);
    }

    [Fact]
    public void Settings_planted_by_a_standard_user_is_quarantined_flagged_and_restorable_by_an_administrator()
    {
        Make("settings.json", """{ "serverUrl": "https://console.exemple.fr", "serverToken": "jeton-4-0-0", "extraTrustedPublishers": ["CN=Pirate"] }""",
            Accounts.StandardUser);
        var now = new DateTime(2026, 11, 28, 10, 0, 0);
        var report = new DataRootProtector(_security).Ensure(_root, now, forceFullPass: true);
        var item = Assert.Single(report.QuarantinedConfig);
        Assert.Equal(Accounts.StandardUser.Value, item.OwnerSid);
        Assert.False(File.Exists(AppPaths.SettingsFile));

        ConfigQuarantine.AfterProtection(report, now);

        // Jamais perdu en silence : ERREUR dans le journal, marqueur (sans secret) affiché par la CLI et l'interface.
        Assert.Contains(_log, e => e.Level == LogLevel.Error && e.Message.Contains("compte non administrateur") && e.Message.Contains("spokapilote config restore"));
        Assert.True(File.Exists(ConfigQuarantine.MarkerFile));
        Assert.DoesNotContain("jeton-4-0-0", File.ReadAllText(ConfigQuarantine.MarkerFile));
        var entry = Assert.Single(ConfigQuarantine.Pending());
        Assert.False(entry.Legacy);
        Assert.Equal(Accounts.StandardUser.Value, entry.OwnerSid);
        Assert.StartsWith("Configuration (settings.json) mise en quarantaine le 28/11/2026", entry.Notice);
        Assert.Contains("fichier créé par un compte non administrateur", entry.Notice);
        Assert.Contains("vérifiez-la puis « spokapilote config restore »", entry.Notice);
        // Propriétaire toujours non reconnu : jamais restauré automatiquement.
        ConfigQuarantine.AfterProtection(new ProtectionReport(), now.AddHours(1));
        Assert.False(File.Exists(AppPaths.SettingsFile));
        Assert.Single(ConfigQuarantine.Pending());

        // « config restore » : contenu affiché, jeton masqué, réglages sensibles signalés.
        var candidate = ConfigQuarantine.Prepare();
        Assert.True(candidate.IsSettings);
        Assert.True(candidate.ContainsToken);
        Assert.DoesNotContain("jeton-4-0-0", candidate.Display!.ToJsonString());
        Assert.Equal("https://console.exemple.fr", candidate.Display![nameof(Settings.ServerUrl)]!.GetValue<string>());
        Assert.Contains(nameof(Settings.ExtraTrustedPublishers), candidate.TrustExtendingSettings);

        ConfigQuarantine.Restore(candidate);

        Assert.True(File.Exists(AppPaths.SettingsFile));
        Assert.False(Raw(AppPaths.SettingsFile).ContainsKey("serverToken")); // jeton migré
        Assert.True(File.Exists(SecretStore.TokenFile));
        var restored = Settings.Load();
        Assert.Equal("jeton-4-0-0", restored.ServerToken);
        Assert.Equal("https://console.exemple.fr", restored.ServerUrl);
        Assert.Empty(ConfigQuarantine.Pending());
        Assert.False(File.Exists(ConfigQuarantine.MarkerFile));
        Assert.False(File.Exists(candidate.FullPath));
        Assert.True(File.Exists(candidate.FullPath + ConfigQuarantine.RestoredSuffix)); // gardé jusqu'à la purge
        Assert.Throws<FileNotFoundException>(() => ConfigQuarantine.Prepare()); // plus rien à restaurer
    }

    [Fact]
    public void Discarded_configuration_no_longer_shows_and_is_never_restored()
    {
        Make("settings.json", """{ "serverUrl": "https://pirate.exemple" }""", Accounts.StandardUser);
        var now = DateTime.Now;
        ConfigQuarantine.AfterProtection(new DataRootProtector(_security).Ensure(_root, now, forceFullPass: true), now);
        Assert.Single(ConfigQuarantine.Pending());
        Assert.Equal(1, ConfigQuarantine.DiscardPending());
        Assert.Empty(ConfigQuarantine.Pending());
        ConfigQuarantine.AfterProtection(new ProtectionReport(), now);
        Assert.Empty(ConfigQuarantine.Pending());
        Assert.False(File.Exists(AppPaths.SettingsFile));
    }

    [Fact]
    public void Quarantined_settings_whose_owner_becomes_a_recognised_administrator_is_restored_automatically()
    {
        // Technicien du domaine, poste hors ligne au moment de la reprise : appartenance imbriquée non vérifiable.
        _membership.Offline = true;
        AdministratorOwners.ResetCache();
        Make("settings.json", """{ "serverUrl": "https://console.exemple.fr", "serverToken": "jeton-tech" }""", Accounts.DomainTechnician);
        var now = DateTime.Now;
        var report = new DataRootProtector(_security).Ensure(_root, now, forceFullPass: true);
        ConfigQuarantine.AfterProtection(report, now);
        Assert.False(File.Exists(AppPaths.SettingsFile));
        Assert.Single(ConfigQuarantine.Pending());

        // Démarrage élevé suivant, domaine joignable : propriétaire reconnu, configuration absente → restaurée.
        _membership.Offline = false;
        AdministratorOwners.ResetCache();
        ConfigQuarantine.AfterProtection(new ProtectionReport(), now.AddMinutes(5));

        Assert.True(File.Exists(AppPaths.SettingsFile));
        Assert.False(Raw(AppPaths.SettingsFile).ContainsKey("serverToken"));
        Assert.Equal("jeton-tech", Settings.Load().ServerToken);
        Assert.Empty(ConfigQuarantine.Pending());
        Assert.Contains(_log, e => e.Level == LogLevel.Warning && e.Message.Contains("restauré automatiquement"));
    }

    /// <summary>Quarantaine laissée par la 4.0.1 : propriétaire repris (Administrateurs) avant le déplacement, aucun marqueur.</summary>
    private string Legacy401(string stamp, string json)
    {
        var path = Make(Path.Combine("Quarantaine", stamp, "settings.json"), json, DataProtection.AdministratorsSid);
        Make(Path.Combine("Quarantaine", stamp, "Logs", "spokapilote-20261127.log"), "journal", DataProtection.AdministratorsSid);
        return path;
    }

    [Fact]
    public void Configuration_lost_by_a_401_upgrade_is_restored_automatically_when_it_extends_no_trust()
    {
        var legacy = Legacy401("20261127-183000", """{ "serverUrl": "https://console.exemple.fr", "serverToken": "jeton-4-0-0", "useOemCatalogs": false }""");

        ConfigQuarantine.AfterProtection(new ProtectionReport(), new DateTime(2026, 11, 28, 9, 0, 0));

        var settings = Settings.Load();
        Assert.Equal("https://console.exemple.fr", settings.ServerUrl);
        Assert.False(settings.UseOemCatalogs);
        Assert.Equal("jeton-4-0-0", settings.ServerToken);
        Assert.False(Raw(AppPaths.SettingsFile).ContainsKey("serverToken"));
        Assert.True(File.Exists(SecretStore.TokenFile));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ConfigQuarantine.RestoredSuffix));
        Assert.Empty(ConfigQuarantine.Pending());
        // Journaux de la quarantaine : laissés en place (purgés avec le dossier).
        Assert.True(File.Exists(Path.Combine(_root, "Quarantaine", "20261127-183000", "Logs", "spokapilote-20261127.log")));
        // Démarrage suivant : rien de plus.
        ConfigQuarantine.AfterProtection(new ProtectionReport(), new DateTime(2026, 11, 28, 10, 0, 0));
        Assert.Empty(ConfigQuarantine.Pending());
    }

    [Fact]
    public void Configuration_lost_by_a_401_upgrade_that_extends_trust_waits_for_an_administrator()
    {
        Legacy401("20261127-183000", """{ "serverUrl": "https://console.exemple.fr", "repositoryPaths": [ "\\\\srv\\pilotes" ], "extraTrustedHosts": [] }""");

        ConfigQuarantine.AfterProtection(new ProtectionReport(), DateTime.Now);

        Assert.False(File.Exists(AppPaths.SettingsFile));
        var entry = Assert.Single(ConfigQuarantine.Pending());
        Assert.True(entry.Legacy);
        Assert.Equal(new[] { nameof(Settings.RepositoryPaths) }, entry.TrustExtendingSettings); // liste vide : pas un réglage
        Assert.Contains("par la version 4.0.1 (propriétaire d'origine inconnu ; réglages qui étendent la confiance : RepositoryPaths)", entry.Notice);
        Assert.Contains(_log, e => e.Level == LogLevel.Error && e.Message == entry.Notice);
        var candidate = ConfigQuarantine.Prepare();
        ConfigQuarantine.Restore(candidate);
        Assert.Equal(@"\\srv\pilotes", Assert.Single(Settings.Load().RepositoryPaths));
        Assert.Empty(ConfigQuarantine.Pending());
    }

    [Fact]
    public void Configuration_already_redone_by_hand_after_401_is_left_alone()
    {
        Legacy401("20261127-183000", """{ "serverUrl": "https://ancienne.exemple.fr" }""");
        File.WriteAllText(AppPaths.SettingsFile, """{ "serverUrl": "https://console.exemple.fr" }""");
        ConfigQuarantine.AfterProtection(new ProtectionReport(), DateTime.Now);
        Assert.Empty(ConfigQuarantine.Pending());
        Assert.Equal("https://console.exemple.fr", Settings.Load().ServerUrl);
    }

    [Fact]
    public void Explicit_restore_over_an_existing_configuration_keeps_a_copy_and_the_current_token()
    {
        new Settings { ServerUrl = "https://actuelle.exemple.fr", ServerToken = "jeton-actuel" }.Save();
        var legacy = Legacy401("20261127-183000", """{ "serverUrl": "https://console.exemple.fr" }""");
        var candidate = ConfigQuarantine.Prepare(Path.GetDirectoryName(legacy));
        Assert.False(candidate.ContainsToken);
        Assert.Contains("jeton actuel est conservé", candidate.Display![nameof(Settings.ServerToken)]!.GetValue<string>());
        ConfigQuarantine.Restore(candidate);
        var settings = Settings.Load();
        Assert.Equal("https://console.exemple.fr", settings.ServerUrl);
        Assert.Equal("jeton-actuel", settings.ServerToken);
        Assert.Contains("actuelle.exemple.fr", File.ReadAllText(AppPaths.SettingsFile + ConfigQuarantine.PreviousSettingsSuffix));
    }

    [Fact]
    public void Quarantine_older_than_90_days_is_pruned_unless_still_pending()
    {
        var now = new DateTime(2026, 11, 28, 10, 0, 0);
        Make(@"Quarantaine\20260801-100000\Logs\a.log", "x", DataProtection.AdministratorsSid);
        Make(@"Quarantaine\20261101-100000\Logs\b.log", "x", DataProtection.AdministratorsSid);
        Make(@"Quarantaine\20260701-100000\settings.json", """{ "policySource": "\\\\srv\\p.json" }""", DataProtection.AdministratorsSid);
        ConfigQuarantine.AfterProtection(new ProtectionReport(), now); // ancien settings.json en attente (réglage sensible)
        Assert.Single(ConfigQuarantine.Pending());

        Assert.Equal(1, ConfigQuarantine.Prune(now));
        Assert.False(Directory.Exists(Path.Combine(_root, "Quarantaine", "20260801-100000")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Quarantaine", "20261101-100000")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Quarantaine", "20260701-100000"))); // en attente : gardé

        ConfigQuarantine.DiscardPending();
        Assert.Equal(1, ConfigQuarantine.Prune(now));
        Assert.False(Directory.Exists(Path.Combine(_root, "Quarantaine", "20260701-100000")));
    }

    [Fact]
    public void Marker_is_a_known_data_root_entry()
    {
        Assert.True(DataRootProtector.IsKnownRootEntry(ConfigQuarantine.MarkerFileName));
        Assert.True(DataRootProtector.IsKnownRootEntry("settings.json" + ConfigQuarantine.PreviousSettingsSuffix));
    }
}

/// <summary>API Windows réelles (netapi32) : lecture seule, sans élévation.</summary>
public sealed class WindowsMembershipTests
{
    [Fact]
    public void Real_membership_api_lists_the_administrators_group_and_resolves_direct_members_through_nested_lookup()
    {
        var api = WindowsAdministratorsMembership.Instance;
        var direct = api.DirectMembers();
        Assert.NotNull(direct);
        Assert.NotEmpty(direct!); // au moins le compte Administrateur intégré ou un groupe
        Assert.DoesNotContain(direct!, s => s == DataProtection.UsersSid);
        using var me = WindowsIdentity.GetCurrent();
        // Un membre direct est aussi reconnu par NetUserGetLocalGroups(LG_INCLUDE_INDIRECT) (même réponse, autre API).
        if (direct!.Contains(me.User!)) Assert.True(api.IsMemberIncludingNested(me.User!));
        Assert.Null(api.IsMemberIncludingNested(new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-4242"))); // compte inconnu
        Assert.Equal(new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator) ? me.User : null, api.ElevatedProcessUser());
    }
}

/// <summary>Contrôle réel des propriétaires et des ACL (processus élevé) dans un dossier de test, jamais %ProgramData%.</summary>
[Collection(nameof(GlobalDataProtectionState))]
public sealed class RealAdministratorOwnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "acl-admin-" + Guid.NewGuid().ToString("N"), "SpokaPilote");
    private readonly AppLayout _previousLayout = AppPaths.Layout;

    public RealAdministratorOwnerTests()
    {
        Assert.False(_root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(_root);
        AppPaths.Layout = new AppLayout(DataLocationMode.Override, _root, _root, null, Path.Combine(_root, "ui.json"));
        DataProtection.ResetLocationCache();
        AdministratorOwners.ResetCache();
    }

    public void Dispose()
    {
        AppPaths.Layout = _previousLayout;
        DataProtection.ResetLocationCache();
        DataProtection.DeleteTree(Path.GetDirectoryName(_root)!);
    }

    private static string Write(string path, string content, SecurityIdentifier owner)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        WindowsFileSecurity.Instance.SetOwner(path, false, owner); // SeRestorePrivilege : propriétaire quelconque
        return path;
    }

    [ElevatedFact]
    public void Elevated_start_keeps_admin_account_files_migrates_the_token_and_flags_a_standard_user_file()
    {
        var security = WindowsFileSecurity.Instance;
        security.EnableOwnershipPrivileges();
        using var identity = WindowsIdentity.GetCurrent();
        var me = identity.User!;
        Assert.True(DataProtection.IsTrustedOwner(me)); // administrateur élevé courant (et membre du groupe)
        var standard = FakeFileSecurity.Attacker;     // SID d'un compte qui n'est pas administrateur

        // Poste 4.0.0 : fichiers écrits par le compte de l'administrateur (UAC sans invite), plus un fichier planté.
        var settings = Write(Path.Combine(_root, "settings.json"), """{ "serverUrl": "https://console.exemple.fr", "serverToken": "jeton-reel" }""", me);
        var log = Write(Path.Combine(_root, "Logs", "spokapilote-20261127.log"), "journal", me);
        var reportFile = Write(Path.Combine(_root, "Reports", "PC.html"), "<html/>", me);
        var planted = Write(Path.Combine(_root, "History", "planted.json"), "{}", standard);
        Assert.Equal(me, security.GetOwner(settings, false));

        var report = DataProtection.OnStartup(forceFullPass: true);

        Assert.NotNull(report);
        Assert.Empty(report!.QuarantinedConfig);
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(log));
        Assert.True(File.Exists(reportFile));
        Assert.False(File.Exists(planted));
        Assert.Contains(report.Quarantined, q => q.StartsWith(@"History\planted.json", StringComparison.Ordinal));
        Assert.Equal(DataProtection.AdministratorsSid, security.GetOwner(settings, false)); // repris
        Assert.False(JsonNode.Parse(File.ReadAllText(settings))!.AsObject().ContainsKey("serverToken"));
        Assert.True(DataRootProtector.Matches(security.GetDacl(SecretStore.TokenFile, false), DataProtection.PrivateRules, false));
        Assert.Equal("jeton-reel", Settings.Load().ServerToken);
        Assert.Empty(ConfigQuarantine.Pending());

        // settings.json remplacé par un utilisateur standard : quarantaine, marqueur, restauration explicite.
        File.Delete(settings);
        Write(settings, """{ "serverUrl": "https://pirate.exemple" }""", standard);
        var second = DataProtection.OnStartup(forceFullPass: true)!;
        var item = Assert.Single(second.QuarantinedConfig);
        Assert.Equal(standard.Value, item.OwnerSid);
        Assert.False(File.Exists(settings));
        var entry = Assert.Single(ConfigQuarantine.Pending());
        Assert.Contains("compte non administrateur", entry.Notice);
        // Marqueur lisible par l'interface non élevée (héritage de la racine : Utilisateurs en lecture), sans secret.
        Assert.Contains(new FileInfo(ConfigQuarantine.MarkerFile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            r => r.IdentityReference.Value == DataProtection.UsersSid.Value && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
        var quarantined = Path.Combine(_root, entry.Path);
        Assert.DoesNotContain(new FileInfo(quarantined).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            r => r.IdentityReference.Value == DataProtection.UsersSid.Value);

        ConfigQuarantine.Restore(ConfigQuarantine.Prepare());
        Assert.Equal("https://pirate.exemple", Settings.Load().ServerUrl); // choix explicite de l'administrateur
        Assert.Equal("jeton-reel", Settings.Load().ServerToken);           // jeton actuel conservé
        Assert.Empty(ConfigQuarantine.Pending());
        Assert.False(new DataRootProtector(security).Ensure(_root, DateTime.Now).FullPass); // dossier conforme ensuite
    }
}
