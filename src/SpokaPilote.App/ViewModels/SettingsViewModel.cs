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
        if (TokenInput.Trim().Length > 0) Settings.ServerToken = TokenInput.Trim();
        var ok = Shell.SaveSettings();
        TokenInput = "";
        TokenSaved?.Invoke();
        OnPropertyChanged(nameof(TokenStatus));
        Shell.Tools.Maintenance.RefreshConsole();
        SaveResult = ok
            ? Loc.F("✓ Enregistré à {0:HH:mm:ss}.", DateTime.Now)
            : Loc.T("⚠ Non enregistré : la configuration commune du poste ne se modifie qu'en administrateur.");
    }

    // ------------------------------------------------------------------ jeton de la console (jamais affiché)

    /// <summary>Nouveau jeton saisi (champ masqué) ; vide = jeton actuel conservé.</summary>
    public string TokenInput { get; set; } = "";

    /// <summary>Le champ masqué est vidé après chaque enregistrement.</summary>
    public event Action? TokenSaved;

    /// <summary>Un jeton ne se règle qu'en administrateur (version installée) : il est lisible des seuls administrateurs.</summary>
    public bool CanEditToken => Shell.IsAdmin || Shell.IsPortable;

    public string TokenStatus => !CanEditToken
        ? Loc.T("Jeton enregistré à part, lisible des seuls administrateurs : relancez SpokaPilote en administrateur pour le modifier.")
        : string.IsNullOrWhiteSpace(Settings.ServerToken)
            ? Loc.T("Aucun jeton enregistré.")
            : Loc.T("Jeton enregistré (masqué). Saisissez un nouveau jeton pour le remplacer.");

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
            if (status.Authorized && await CacheStatusTextAsync() is { } cache) ServerStatusText += Environment.NewLine + cache;
        }
        catch (Exception ex)
        {
            ServerStatusText = "✗ " + ex.Message;
        }
    }

    /// <summary>Ligne « Cache de pilotes » (consoles 4.0) ; null si ce poste ne l'utilise pas.</summary>
    private async Task<string?> CacheStatusTextAsync()
    {
        if (Core.Online.ConsoleCache.From(Settings) is not { } cache) return null;
        var s = await cache.GetStatusAsync();
        if (s is null) return Loc.T("Cache de pilotes : indisponible (console antérieure à 4.0 ou injoignable) — téléchargements directs.");
        if (!s.Enabled) return Loc.T("Cache de pilotes : désactivé sur la console — téléchargements directs.");
        var size = (s.SizeBytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.CurrentUICulture);
        return s.HitRatio is { } r
            ? Loc.F("✓ Cache de pilotes : actif, {0} Go, {1} % de réussite.", size, Math.Round(r * 100))
            : Loc.F("✓ Cache de pilotes : actif, {0} Go (aucune demande pour l'instant).", size);
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
