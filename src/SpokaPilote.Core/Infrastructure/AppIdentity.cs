namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Identité du produit qui utilise le moteur : nom affiché, dossier de données, variable d'environnement de
/// redirection, journal, dépôt GitHub des mises à jour et dossier des tâches planifiées.
/// <para>
/// SpokaPilote par défaut (rien à faire). Un autre produit construit sur ce moteur (ex. SpokaPrint) déclare la sienne
/// avec <see cref="Use"/> au tout début de son démarrage, avant tout accès aux emplacements, au journal ou au réseau :
/// ses données vont alors dans %ProgramData%\&lt;DataFolderName&gt; et ses mises à jour viennent de son propre dépôt.
/// </para>
/// </summary>
public sealed record AppIdentity
{
    /// <summary>Nom du produit affiché dans les messages (« SpokaPilote »).</summary>
    public required string ProductName { get; init; }
    /// <summary>Dossier des données : %ProgramData%\X (machine) et %LocalAppData%\X (préférences d'interface).</summary>
    public required string DataFolderName { get; init; }
    /// <summary>Variable d'environnement qui redirige toutes les données (tests, usages particuliers).</summary>
    public required string HomeVariable { get; init; }
    /// <summary>Fichier témoin du mode portable à côté de l'exécutable ; null = pas de mode portable.</summary>
    public string? PortableMarkerFileName { get; init; }
    /// <summary>Préfixe des journaux quotidiens (« spokapilote » → spokapilote-AAAAMMJJ.log).</summary>
    public required string LogFilePrefix { get; init; }
    /// <summary>Dépôt GitHub « propriétaire/nom » dont les Releases fournissent les mises à jour.</summary>
    public required string UpdateRepository { get; init; }
    /// <summary>Dossier du Planificateur de tâches (« \SpokaPilote »).</summary>
    public required string TaskFolder { get; init; }

    /// <summary>Identité de SpokaPilote (par défaut).</summary>
    public static AppIdentity SpokaPilote { get; } = new()
    {
        ProductName = "SpokaPilote",
        DataFolderName = "SpokaPilote",
        HomeVariable = AppPaths.HomeVariable,
        PortableMarkerFileName = AppPaths.PortableMarkerFileName,
        LogFilePrefix = "spokapilote",
        UpdateRepository = "Spokator/SpokaPilote",
        TaskFolder = @"\SpokaPilote",
    };

    /// <summary>Identité en vigueur (SpokaPilote tant que <see cref="Use"/> n'a pas été appelé).</summary>
    public static AppIdentity Current { get; private set; } = SpokaPilote;

    /// <summary>
    /// Déclare l'identité du produit. À appeler au démarrage, avant tout accès à <see cref="AppPaths"/>,
    /// <see cref="Log"/> ou <see cref="Http"/> : les emplacements sont recalculés, mais un client HTTP déjà créé
    /// garderait l'ancien User-Agent.
    /// </summary>
    public static void Use(AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.ProductName) || string.IsNullOrWhiteSpace(identity.DataFolderName) ||
            identity.DataFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Identité de produit invalide (nom ou dossier de données).", nameof(identity));
        if (!identity.TaskFolder.StartsWith('\\') || identity.TaskFolder.Length < 2 || identity.TaskFolder.EndsWith('\\'))
            throw new ArgumentException("Dossier de tâches invalide (format attendu : \\Produit).", nameof(identity));
        Current = identity;
        AppPaths.ResetLayout();
    }
}
