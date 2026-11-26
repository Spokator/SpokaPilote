using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Configuration persistée dans %ProgramData%\SpokaPilote\settings.json (déployable par GPO / Intune) ; en mode
/// portable, dans Data\settings.json sur la clé (voir <see cref="AppPaths"/>).
/// </summary>
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

    /// <summary>Console centrale SpokaPilote Server (ex. https://spokapilote.mon-entreprise.fr). Null = désactivée.</summary>
    public string? ServerUrl { get; set; }
    /// <summary>Jeton d'enrôlement des postes auprès de la console centrale (en-tête Authorization: Bearer).</summary>
    public string? ServerToken { get; set; }
    /// <summary>
    /// Politique de pilotes : chemin local/UNC ou URL HTTPS d'un fichier driver-policy.json.
    /// Si null et qu'une console centrale est configurée : {ServerUrl}/api/policy.
    /// </summary>
    public string? PolicySource { get; set; }
    /// <summary>
    /// Cache de pilotes et de catalogues de la console centrale (consoles 4.0 et ultérieures) : « auto » (défaut) = utilisé
    /// quand une console est configurée avec un jeton et que son cache est actif ; « on » = idem, avec un avertissement
    /// dans le journal à chaque repli sur le téléchargement direct ; « off » = jamais. La console n'est qu'un transport :
    /// le poste refait toutes ses vérifications, et télécharge directement si la console est injoignable.
    /// </summary>
    public string UseConsoleCache { get; set; } = "auto";
    /// <summary>Demander aussi les catalogues (Microsoft Update Catalog, Dell, HP, Lenovo, NVIDIA) à la console.</summary>
    public bool ConsoleCacheCatalogs { get; set; } = true;
    public bool UseGpuVendorSources { get; set; } = true;
    /// <summary>
    /// Branche des pilotes NVIDIA GeForce : « studio » (défaut, publiée moins souvent et validée pour la stabilité,
    /// adaptée à un parc) ou « gameready » (la plus récente, orientée jeux). Les cartes professionnelles RTX/Quadro
    /// reçoivent toujours le pilote RTX Enterprise servi par NVIDIA.
    /// </summary>
    public string NvidiaBranch { get; set; } = "studio";

    /// <summary>
    /// Dépôts réellement indexés : en mode portable, le dépôt de la clé (&lt;dossier de l'exe&gt;\Depot, s'il existe) en
    /// premier, puis les dépôts configurés (un chemin relatif l'est par rapport au dossier de la copie portable).
    /// Calculé à chaque appel et jamais enregistré : la lettre de la clé change d'un poste à l'autre.
    /// </summary>
    public List<string> EffectiveRepositoryPaths() => EffectiveRepositoryPaths(AppPaths.DefaultRepository, AppPaths.PortableHome);

    internal List<string> EffectiveRepositoryPaths(string? defaultRepository, string? portableHome)
    {
        var list = new List<string>();
        void Add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !list.Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }
        if (defaultRepository is not null) Add(defaultRepository);
        foreach (var path in RepositoryPaths) Add(AppPaths.ResolveRepositoryPath(path, portableHome));
        return list;
    }

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
            Online.ConsoleCache.Configure(this);
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer la configuration : {ex.Message}");
        }
    }
}
