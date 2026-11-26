using System.IO;
using SpokaPilote.App.Localization;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.App.ViewModels;

/// <summary>Paramètres : langue, sources de pilotes, sécurité, connexion à la console, à propos et mise à jour.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(MainViewModel shell)
    {
        Shell = shell;
        SaveCommand = new RelayCommand(Save);
        TestServerCommand = new RelayCommand(TestServerAsync, () => !Shell.IsBusy);
        CheckUpdateCommand = new RelayCommand(CheckUpdateAsync, () => !Shell.IsBusy);
        OpenSettingsFolderCommand = new RelayCommand(() => MainViewModel.OpenPath(Path.GetDirectoryName(AppPaths.SettingsFile)!));
    }

    public MainViewModel Shell { get; }
    public Core.Infrastructure.Settings Settings => Shell.Settings;

    // ------------------------------------------------------------------ langue de l'interface

    public string[] LanguageChoices { get; } = { Loc.T("Automatique (langue de Windows)"), "Français", "English" };
    private int _languageIndex = UiPreferences.Load().Language switch { "fr" => 1, "en" => 2, _ => 0 };
    private string _languageNote = "";
    public string LanguageNote { get => _languageNote; private set => Set(ref _languageNote, value); }
    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (!Set(ref _languageIndex, value)) return;
            var prefs = UiPreferences.Load();
            prefs.Language = value switch { 1 => "fr", 2 => "en", _ => null };
            prefs.Save();
            LanguageNote = Loc.T("La nouvelle langue s'appliquera au prochain démarrage de SpokaPilote.");
        }
    }

    // ------------------------------------------------------------------ enregistrement

    public string SettingsFileNote => Shell.IsPortable
        ? Loc.F("Mode portable : enregistrés sur la clé ({0}), communs à tous les postes où elle est branchée.", AppPaths.SettingsFile)
        : Loc.F("Enregistrés dans {0} : le même fichier peut être déployé sur tout le parc (GPO, Intune, script).", AppPaths.SettingsFile);

    private string _saveResult = "";
    public string SaveResult { get => _saveResult; private set => Set(ref _saveResult, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenSettingsFolderCommand { get; }

    private void Save()
    {
        Shell.SaveSettings();
        Shell.Tools.Maintenance.RefreshConsole();
        SaveResult = Loc.F("✓ Enregistré à {0:HH:mm:ss}.", DateTime.Now);
    }

    // ------------------------------------------------------------------ console centrale

    private string _serverStatus = "";
    public string ServerStatusText { get => _serverStatus; private set => Set(ref _serverStatus, value); }
    public RelayCommand TestServerCommand { get; }

    private async Task TestServerAsync()
    {
        if (!Core.Online.ServerClient.IsConfigured(Settings)) { ServerStatusText = Loc.T("Aucune adresse de console renseignée."); return; }
        try
        {
            ServerStatusText = Loc.T("Test en cours...");
            var status = await new Core.Online.ServerClient(Settings).TestAsync();
            ServerStatusText = (status.Reachable && status.Authorized ? "✓ " : "✗ ") + status.Message;
        }
        catch (Exception ex)
        {
            ServerStatusText = "✗ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ à propos et mise à jour

    public string VersionText => Shell.AppVersionLabel;
    private string _updateStatus = "";
    public string UpdateStatusText { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public RelayCommand CheckUpdateCommand { get; }

    private async Task CheckUpdateAsync()
    {
        UpdateStatusText = Loc.T("Recherche d'une nouvelle version...");
        var ok = await Shell.CheckForUpdateAsync();
        UpdateStatusText = !ok ? Loc.T("Vérification impossible (pas d'accès à GitHub). Réessayez plus tard.")
            : Shell.HasUpdate ? Shell.UpdateLabel
            : Loc.T("✓ Vous avez la dernière version.");
    }
}
