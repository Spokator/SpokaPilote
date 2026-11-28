using System.Text;
using System.Text.Json.Nodes;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Tests;

/// <summary>Jeton de la console hors de settings.json, migration, casse des clés (fichier commun du dossier de test).</summary>
[Collection(nameof(GlobalDataProtectionState))]
public sealed class SettingsTokenTests : IDisposable
{
    public SettingsTokenTests() => Clean();

    public void Dispose() => Clean();

    private static void Clean()
    {
        if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
        if (File.Exists(SecretStore.TokenFile)) File.Delete(SecretStore.TokenFile);
    }

    private static JsonObject RawSettings() => JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();

    [Fact]
    public void Token_is_saved_apart_encrypted_and_never_in_settings_json()
    {
        Assert.True(SecretStore.UsesTokenFile); // SPOKAPILOTE_HOME des tests : comme la version installée
        var settings = new Settings { ServerToken = "jeton-secret-123", PolicySource = @"\\srv\politique.json" };
        Assert.True(settings.Save());

        Assert.False(RawSettings().ContainsKey("serverToken"));
        Assert.DoesNotContain("jeton-secret-123", File.ReadAllText(AppPaths.SettingsFile));
        Assert.True(File.Exists(SecretStore.TokenFile));
        Assert.DoesNotContain("jeton-secret-123", Encoding.UTF8.GetString(File.ReadAllBytes(SecretStore.TokenFile))); // DPAPI
        var loaded = Settings.Load();
        Assert.Equal("jeton-secret-123", loaded.ServerToken);
        Assert.Equal(@"\\srv\politique.json", loaded.PolicySource);

        // Enregistrement sans changement du jeton : fichier du jeton intact ; jeton effacé : fichier supprimé.
        var before = File.GetLastWriteTimeUtc(SecretStore.TokenFile);
        loaded.CatalogConcurrency = 2;
        Assert.True(loaded.Save());
        Assert.Equal(before, File.GetLastWriteTimeUtc(SecretStore.TokenFile));
        loaded.ServerToken = "";
        Assert.True(loaded.Save());
        Assert.False(File.Exists(SecretStore.TokenFile));
        Assert.Null(Settings.Load().ServerToken);
    }

    [Fact]
    public void Plaintext_token_written_by_an_older_version_or_the_deployment_kit_is_moved_on_every_elevated_start()
    {
        // 4.0.0, ou deploy\Install-SpokaPilote.ps1 qui écrit « serverToken » dans settings.json après l'installation.
        File.WriteAllText(AppPaths.SettingsFile, """{ "serverToken": "ancien-jeton", "catalogConcurrency": 3 }""");
        var legacy = Settings.Load();
        Assert.Equal("ancien-jeton", legacy.ServerToken); // utilisable tout de suite

        Settings.MigrateLegacyToken();
        Assert.False(RawSettings().ContainsKey("serverToken"));
        Assert.Equal(3, RawSettings()["catalogConcurrency"]!.GetValue<int>());
        Assert.Equal("ancien-jeton", Settings.Load().ServerToken);

        // Redéployé plus tard (GPO, kit) avec un nouveau jeton, clé en PascalCase : de nouveau déplacé, le nouveau l'emporte.
        File.WriteAllText(AppPaths.SettingsFile, """{ "ServerToken": "nouveau-jeton" }""");
        Settings.MigrateLegacyToken();
        Assert.DoesNotContain("nouveau-jeton", File.ReadAllText(AppPaths.SettingsFile));
        Assert.Equal("nouveau-jeton", Settings.Load().ServerToken);

        // Rien à migrer : fichier inchangé.
        var stamp = File.GetLastWriteTimeUtc(AppPaths.SettingsFile);
        Settings.MigrateLegacyToken();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(AppPaths.SettingsFile));
    }

    [Fact]
    public void Settings_keys_are_read_whatever_their_case_and_written_in_camel_case()
    {
        File.WriteAllText(AppPaths.SettingsFile, """
            {
              "ServerUrl": "https://console.exemple.fr",
              "USEWINDOWSUPDATE": false,
              "repositoryPaths": [ "\\\\srv\\pilotes" ],
              "CatalogConcurrency": 5
            }
            """);
        var settings = Settings.Load();
        Assert.Equal("https://console.exemple.fr", settings.ServerUrl);
        Assert.False(settings.UseWindowsUpdate);
        Assert.Equal(@"\\srv\pilotes", Assert.Single(settings.RepositoryPaths));
        Assert.Equal(5, settings.CatalogConcurrency);

        settings.ServerUrl = null; // pas de console réelle configurée pendant les tests
        Assert.True(settings.Save());
        var raw = RawSettings();
        Assert.True(raw.ContainsKey("useWindowsUpdate"));
        Assert.True(raw.ContainsKey("catalogConcurrency"));
        Assert.False(raw.ContainsKey("USEWINDOWSUPDATE"));
    }

    [Fact]
    public void Displayed_configuration_masks_the_token()
    {
        var shown = new Settings { ServerToken = "jeton-a-cacher" }.ToDisplayJson();
        Assert.DoesNotContain("jeton-a-cacher", shown.ToJsonString());
        Assert.StartsWith("(défini", shown["ServerToken"]!.GetValue<string>());
        Assert.Null(new Settings().ToDisplayJson()["ServerToken"]);
    }

    [Fact]
    public void Dpapi_round_trip_and_tampered_file_is_rejected()
    {
        var blob = SecretStore.Protect(Encoding.UTF8.GetBytes("abc"));
        Assert.Equal("abc", Encoding.UTF8.GetString(SecretStore.Unprotect(blob)));
        SecretStore.WriteToken("xyz");
        var bytes = File.ReadAllBytes(SecretStore.TokenFile);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(SecretStore.TokenFile, bytes);
        Assert.Null(SecretStore.ReadToken());
    }

    [Fact]
    public void Token_that_cannot_be_decrypted_on_this_machine_is_discarded_and_the_machine_carries_on_without_console()
    {
        // Poste cloné depuis une image de référence qui contenait le jeton (clé DPAPI machine différente), ou fichier
        // recopié d'un autre poste : blob indéchiffrable ici (simulé par un blob DPAPI d'une autre clé).
        var foreign = SecretStore.Protect(Encoding.UTF8.GetBytes("jeton-du-master"));
        foreign[foreign.Length / 2] ^= 0x5A; // une clé différente produit la même erreur de CryptUnprotectData
        File.WriteAllBytes(SecretStore.TokenFile, [.. "SPKT1"u8.ToArray(), .. foreign]);
        File.WriteAllText(AppPaths.SettingsFile, """{ "catalogConcurrency": 2 }""");

        var settings = Settings.Load();
        Assert.Null(settings.ServerToken);
        Assert.Equal(2, settings.CatalogConcurrency); // le reste de la configuration s'applique
        Assert.False(File.Exists(SecretStore.TokenFile)); // supprimé : redéployé ensuite par le kit / Intune
        Assert.Null(SecretStore.ReadToken());

        // Fichier d'un autre format (version future, contenu quelconque) : de même.
        File.WriteAllText(SecretStore.TokenFile, "pas un jeton");
        Assert.Null(SecretStore.ReadToken());
        Assert.False(File.Exists(SecretStore.TokenFile));
    }
}
