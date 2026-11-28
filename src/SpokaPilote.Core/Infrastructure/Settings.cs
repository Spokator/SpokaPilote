using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Configuration persistée dans %ProgramData%\SpokaPilote\settings.json (déployable par GPO / Intune) ; en mode
/// portable, dans Data\settings.json sur la clé (voir <see cref="AppPaths"/>). Le fichier est lisible par tous les
/// utilisateurs et modifiable par les seuls administrateurs ; le jeton de la console n'y figure pas (voir
/// <see cref="SecretStore"/>), sauf en mode portable.
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
    /// <summary>Sauvegardes complètes des pilotes (une par installation, 1 à 4 Go) conservées au plus (la plus récente toujours).</summary>
    public int BackupRetentionCount { get; set; } = 3;
    /// <summary>Place totale maximale des sauvegardes complètes, en Mo (0 = sans limite) ; la plus récente est toujours gardée.</summary>
    public int BackupMaxTotalSizeMB { get; set; } = 10240;
    /// <summary>
    /// Ancienneté maximale, en jours, de l'intervention annulée par « rollback last » et la tâche distante « rollback-last » ;
    /// au-delà, l'annulation se demande explicitement (spokapilote rollback &lt;id&gt;).
    /// </summary>
    public int RollbackLastMaxAgeDays { get; set; } = 30;

    /// <summary>Domaines supplémentaires autorisés au téléchargement (en plus de la liste officielle intégrée).</summary>
    public List<string> ExtraTrustedHosts { get; set; } = new();
    /// <summary>Éditeurs supplémentaires autorisés pour les installeurs (.exe/.msi) signés.</summary>
    public List<string> ExtraTrustedPublishers { get; set; } = new();

    /// <summary>URL d'un catalogue d'imprimantes complémentaire (JSON) hébergé par votre organisation.</summary>
    public string? PrinterCatalogUrl { get; set; }

    public int CatalogConcurrency { get; set; } = 4;

    /// <summary>Console centrale SpokaPilote Server (ex. https://spokapilote.mon-entreprise.fr). Null = désactivée.</summary>
    public string? ServerUrl { get; set; }
    /// <summary>
    /// Jeton d'enrôlement des postes auprès de la console centrale (en-tête Authorization: Bearer). Enregistré à part, dans
    /// un fichier lisible des seuls administrateurs (<see cref="SecretStore"/>), sauf en mode portable (settings.json de la
    /// clé). Un ancien settings.json qui le contient encore est migré au premier démarrage élevé.
    /// </summary>
    public string? ServerToken { get; set; }

    /// <summary>Jeton tel qu'il a été chargé (rien n'est récrit s'il n'a pas changé).</summary>
    private string? _loadedToken;
    /// <summary>Jeton lu en clair dans settings.json : à déplacer vers <see cref="SecretStore"/> au prochain enregistrement.</summary>
    private bool _tokenInSettingsFile;
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
        // Un fichier écrit à la main ou par un script (« ServerUrl », « SERVERURL ») est lu comme le fichier canonique
        // (camelCase, seule forme écrite) : une clé n'est jamais ignorée en silence pour une question de casse.
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load() => WithToken(LoadFile(AppPaths.SettingsFile) ?? new Settings());

    /// <summary>
    /// Configuration lue dans ce texte (settings.json mis en quarantaine, restauré par <see cref="ConfigQuarantine"/>) : un
    /// jeton en clair est déplacé vers <see cref="SecretStore"/> au prochain <see cref="Save"/> ; sans jeton, celui déjà
    /// enregistré est conservé. JsonException si le texte n'est pas une configuration.
    /// </summary>
    internal static Settings FromJson(string json) =>
        WithToken(JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? throw new JsonException("Configuration vide."));

    private static Settings WithToken(Settings settings)
    {
        if (SecretStore.UsesTokenFile)
        {
            if (string.IsNullOrWhiteSpace(settings.ServerToken)) settings.ServerToken = SecretStore.ReadToken();
            else settings._tokenInSettingsFile = true;
        }
        settings._loadedToken = settings.ServerToken;
        return settings;
    }

    private static Settings? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Exécution élevée : un settings.json qui n'a pas été écrit par un administrateur (créé d'avance par un
            // utilisateur avec ses propres éditeurs approuvés ou sa console) n'est jamais pris en compte.
            if (!DataProtection.IsTrustedForElevatedUse(path))
            {
                Log.Warn($"Configuration ignorée : {path} n'a été écrit ni par SYSTEM ni par un administrateur. Valeurs par défaut utilisées ; " +
                         "redéployez le fichier depuis un compte administrateur.");
                return null;
            }
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"Configuration illisible ({path}), valeurs par défaut utilisées : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Enregistre la configuration. Version installée : réservé aux administrateurs (le fichier commun pilote les tâches
    /// SYSTEM) ; le jeton va dans <see cref="SecretStore"/>, jamais dans settings.json. Renvoie false en cas d'échec.
    /// </summary>
    public bool Save()
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
            if (SecretStore.UsesTokenFile)
            {
                // Jeton d'abord : settings.json n'est purgé de l'ancien jeton qu'une fois celui-ci enregistré à part.
                if (_tokenInSettingsFile || !string.Equals(ServerToken, _loadedToken, StringComparison.Ordinal))
                    SecretStore.WriteToken(ServerToken);
                node.Remove("serverToken");
            }
            DataProtection.WriteTextAtomically(AppPaths.SettingsFile, node.ToJsonString(JsonOptions));
            _loadedToken = ServerToken;
            _tokenInSettingsFile = false;
            Online.ConsoleCache.Configure(this);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer la configuration{(ex is UnauthorizedAccessException && !DataProtection.IsElevated ? " (droits administrateur nécessaires)" : "")} : {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// À chaque démarrage élevé (version installée), pas seulement une fois : un jeton en clair dans settings.json
    /// (versions 4.0.0 et antérieures, kit de déploiement deploy\Install-SpokaPilote.ps1, fichier redéployé par GPO /
    /// Intune) est déplacé vers <see cref="SecretStore"/> et retiré du fichier. Une clé « ServerToken » de n'importe quelle
    /// casse est reconnue. Tout enregistrement élevé (« config set », interface) fait de même.
    /// </summary>
    internal static void MigrateLegacyToken()
    {
        if (!SecretStore.UsesTokenFile || !File.Exists(AppPaths.SettingsFile)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    is not JsonObject raw ||
                !raw.Any(kv => kv.Key.Equals("serverToken", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value?.ToString())))
                return;
        }
        catch (JsonException)
        {
            return;
        }
        var settings = Load();
        if (!settings._tokenInSettingsFile) return;
        if (settings.Save())
            Log.Info($"Jeton de la console déplacé de settings.json vers {SecretStore.TokenFile} (lisible des seuls administrateurs).");
    }

    /// <summary>Copie d'affichage (« spokapilote config ») : jeton masqué.</summary>
    public JsonObject ToDisplayJson()
    {
        var node = JsonSerializer.SerializeToNode(this, new JsonSerializerOptions { WriteIndented = true })!.AsObject();
        if (!string.IsNullOrWhiteSpace(ServerToken))
            node[nameof(ServerToken)] = $"(défini, {(SecretStore.UsesTokenFile ? $"enregistré dans {SecretStore.TokenFileName}" : "enregistré dans settings.json")})";
        return node;
    }
}
