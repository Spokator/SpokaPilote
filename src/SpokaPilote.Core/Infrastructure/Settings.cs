using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>Configuration persistée dans %ProgramData%\SpokaPilote\settings.json (déployable par GPO / Intune).</summary>
public sealed class Settings
{
    /// <summary>Dépôts de pilotes locaux ou réseau (ex. \serveur\pilotes) indexés en priorité.</summary>
    public List<string> RepositoryPaths { get; set; } = new();

    public bool UseWindowsUpdate { get; set; } = true;
    /// <summary>Interroge directement Windows Update même si la machine est gérée par WSUS (WSUS n'approuve souvent pas les pilotes).</summary>
    public bool WindowsUpdateBypassWsus { get; set; } = true;
    public bool UseMicrosoftCatalog { get; set; } = true;
    public bool UseOemCatalogs { get; set; } = true;
    public bool IncludeBiosAndFirmware { get; set; } = false;
    public bool CreateRestorePoint { get; set; } = true;
    public bool BackupDriversBeforeInstall { get; set; } = true;

    /// <summary>Domaines supplémentaires autorisés au téléchargement (en plus de la liste officielle intégrée).</summary>
    public List<string> ExtraTrustedHosts { get; set; } = new();
    /// <summary>Éditeurs supplémentaires autorisés pour les installeurs (.exe/.msi) signés.</summary>
    public List<string> ExtraTrustedPublishers { get; set; } = new();

    /// <summary>URL d'un catalogue d'imprimantes complémentaire (JSON) hébergé par votre organisation.</summary>
    public string? PrinterCatalogUrl { get; set; }

    public int CatalogConcurrency { get; set; } = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Configuration illisible ({AppPaths.SettingsFile}), valeurs par défaut utilisées : {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer la configuration : {ex.Message}");
        }
    }
}
