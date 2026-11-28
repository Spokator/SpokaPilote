using Microsoft.Win32;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>Origine de l'emplacement des données (<see cref="AppLayout.Mode"/>).</summary>
public enum DataLocationMode
{
    /// <summary>Version installée : %ProgramData%\SpokaPilote.</summary>
    Installed,
    /// <summary>Mode portable (clé USB) : dossier « Data » à côté de l'exécutable, un sous-dossier par poste.</summary>
    Portable,
    /// <summary>Variable d'environnement SPOKAPILOTE_HOME (tests, usages particuliers) : prioritaire sur tout le reste.</summary>
    Override,
}

/// <summary>
/// Emplacements résolus une fois pour toutes au démarrage (voir <see cref="AppPaths.Resolve"/>).
/// </summary>
/// <param name="Root">Données partagées : configuration, cache, téléchargements, rapports.</param>
/// <param name="MachineRoot">
/// Données propres au poste : journaux, historique des interventions, sauvegardes de pilotes. Égal à
/// <paramref name="Root"/> hors mode portable ; en mode portable, Root\Postes\&lt;POSTE&gt;_&lt;id&gt; : une même clé
/// sert des centaines de postes sans mélanger leurs historiques (l'annulation retire des paquets oemNN.inf propres à
/// chaque poste).
/// </param>
/// <param name="PortableHome">Dossier de l'exécutable portable (contenant SpokaPilote.portable), sinon null.</param>
/// <param name="UiPreferencesFile">Préférences d'interface (langue, premier lancement, dernier onglet).</param>
/// <param name="UserRoot">
/// Version installée lancée sans élévation : %LocalAppData%\SpokaPilote, où vont toutes ses écritures (journaux, cache,
/// rapports, dossiers de travail, caches de politique). Le dossier commun (%ProgramData%\SpokaPilote) n'est modifiable
/// que par SYSTEM et les Administrateurs (<see cref="DataProtection"/>) : il n'y est que lu. Null = écritures dans
/// <paramref name="Root"/> / <paramref name="MachineRoot"/>.
/// </param>
public sealed record AppLayout(DataLocationMode Mode, string Root, string MachineRoot, string? PortableHome, string UiPreferencesFile, string? UserRoot = null)
{
    /// <summary>Exécutables lancés depuis une copie portable (fichier témoin présent), quel que soit l'emplacement des données.</summary>
    public bool IsPortable => PortableHome is not null;
}

/// <summary>
/// Emplacements disque utilisés par SpokaPilote. Par ordre de priorité :
/// 1. SPOKAPILOTE_HOME (variable d'environnement) ;
/// 2. mode portable : fichier « SpokaPilote.portable » à côté de l'exécutable → &lt;dossier de l'exe&gt;\Data ;
/// 3. version installée : %ProgramData%\SpokaPilote.
/// Un autre produit construit sur ce moteur déclare son identité (<see cref="AppIdentity.Use"/>) : sa variable, son
/// fichier témoin et son dossier de données remplacent alors ceux de SpokaPilote.
/// </summary>
public static class AppPaths
{
    /// <summary>Variable de redirection de SpokaPilote (celle du produit en vigueur : <see cref="AppIdentity.HomeVariable"/>).</summary>
    public const string HomeVariable = "SPOKAPILOTE_HOME";
    /// <summary>Fichier témoin du mode portable, à côté de SpokaPilote.App.exe et spokapilote.exe.</summary>
    public const string PortableMarkerFileName = "SpokaPilote.portable";
    /// <summary>Données de la copie portable, à côté de l'exécutable.</summary>
    public const string PortableDataFolderName = "Data";
    /// <summary>Dépôt de pilotes de la clé, à côté de l'exécutable : utilisé automatiquement s'il existe.</summary>
    public const string PortableRepositoryFolderName = "Depot";
    /// <summary>Sous-dossier des données propres à chaque poste (mode portable).</summary>
    public const string MachinesFolderName = "Postes";

    private static AppLayout? _layout;

    /// <summary>Emplacements en vigueur (résolus au premier accès ; modifiables par les tests).</summary>
    public static AppLayout Layout
    {
        get => _layout ??= ResolveCurrent();
        set => _layout = value;
    }

    /// <summary>Oublie les emplacements calculés (changement d'identité du produit) : recalculés au prochain accès.</summary>
    internal static void ResetLayout() => _layout = null;

    /// <summary>Données partagées (configuration, cache, téléchargements, rapports).</summary>
    public static string Root
    {
        get => Layout.Root;
        set => _layout = Layout with { Root = value, MachineRoot = value };
    }

    /// <summary>Données propres à ce poste (journaux, historique, sauvegardes) : voir <see cref="AppLayout.MachineRoot"/>.</summary>
    public static string MachineRoot => Layout.MachineRoot;
    public static bool IsPortable => Layout.IsPortable;
    public static string? PortableHome => Layout.PortableHome;

    /// <summary>Racine des écritures de ce processus : <see cref="AppLayout.UserRoot"/> (non élevé, version installée) ou <see cref="Root"/>.</summary>
    public static string WritableRoot => Layout.UserRoot ?? Layout.Root;
    /// <summary>Racine des écritures propres au poste : <see cref="AppLayout.UserRoot"/> ou <see cref="MachineRoot"/>.</summary>
    public static string WritableMachineRoot => Layout.UserRoot ?? Layout.MachineRoot;

    /// <summary>Catalogues et index en cache (dossier privé quand le processus est élevé).</summary>
    public static string Cache => DataProtection.EnsurePrivateDirectory(Path.Combine(WritableRoot, "Cache"));
    /// <summary>
    /// Sous-dossier du cache (« catalog », « repo-index ») : processus élevé, jamais un lien ni un dossier d'un autre
    /// propriétaire (retiré sans être suivi puis recréé), voir <see cref="DataProtection.EnsureTrustedSubdirectory"/>.
    /// </summary>
    public static string CacheSubdirectory(string name) => DataProtection.EnsureTrustedSubdirectory(Cache, name);
    /// <summary>Dossiers de travail propres à chaque exécution (<see cref="WorkDirectory"/>) : dossier privé quand le processus est élevé.</summary>
    public static string Work => DataProtection.EnsurePrivateDirectory(Path.Combine(WritableRoot, WorkDirectory.FolderName));
    public static string Reports => Ensure(Path.Combine(WritableRoot, "Reports"));
    public static string Logs => Ensure(Path.Combine(WritableMachineRoot, "Logs"));
    /// <summary>Journaux des exécutions élevées du poste (tâches planifiées, agent) : lus aussi par un processus non élevé.</summary>
    public static string MachineLogs => EnsureIfWritable(Path.Combine(MachineRoot, "Logs"));
    public static string Backups => EnsureIfWritable(Path.Combine(MachineRoot, "Backups"));
    public static string History => EnsureIfWritable(Path.Combine(MachineRoot, "History"));
    /// <summary>Archives portables téléchargées par la mise à jour (mode portable).</summary>
    public static string Updates => Ensure(Path.Combine(WritableRoot, "Mises-a-jour"));
    /// <summary>Configuration : modifiable par SYSTEM et les Administrateurs seulement (version installée), lisible par tous.</summary>
    public static string SettingsFile => Path.Combine(EnsureIfWritable(Root), "settings.json");
    internal const string PolicyCacheFileName = "policy-cache.json";
    internal const string RolloutDecisionsCacheFileName = "rollout-decisions.json";
    /// <summary>
    /// Dernière politique de pilotes valide (fonctionnement hors ligne). Propre au poste : en mode portable, chaque poste a
    /// la sienne (un poste ne réutilise jamais la politique ou l'anneau reçus par un autre poste servi par la même clé).
    /// </summary>
    public static string PolicyCache => Path.Combine(Ensure(WritableMachineRoot), PolicyCacheFileName);
    /// <summary>Dernières décisions de diffusion par anneaux reçues de la console (poste hors ligne) : propres au poste, comme <see cref="PolicyCache"/>.</summary>
    public static string RolloutDecisionsCache => Path.Combine(Ensure(WritableMachineRoot), RolloutDecisionsCacheFileName);
    /// <summary>
    /// Version installée lancée sans élévation : cache de politique écrit par les exécutions SYSTEM du poste
    /// (%ProgramData%\SpokaPilote), lu seulement, quand la console n'est pas accessible sans le jeton. Null sinon.
    /// </summary>
    public static string? MachinePolicyCache => Layout.UserRoot is null ? null : Path.Combine(MachineRoot, PolicyCacheFileName);
    /// <summary>Décisions de diffusion écrites par les exécutions SYSTEM du poste (voir <see cref="MachinePolicyCache"/>).</summary>
    public static string? MachineRolloutDecisionsCache => Layout.UserRoot is null ? null : Path.Combine(MachineRoot, RolloutDecisionsCacheFileName);
    public static string UiPreferencesFile => Layout.UiPreferencesFile;

    /// <summary>
    /// Points de montage DISM (images Windows). DISM exige un volume NTFS local : en mode portable (clé souvent en
    /// FAT32/exFAT), dossier temporaire du poste, vidé après chaque injection.
    /// </summary>
    public static string Mount => Layout.Mode == DataLocationMode.Portable
        ? Ensure(Path.Combine(Path.GetTempPath(), AppIdentity.Current.DataFolderName, "Mount"))
        : DataProtection.EnsurePrivateDirectory(Path.Combine(Root, "Mount"));

    /// <summary>Dépôt de pilotes de la clé (&lt;dossier de l'exe&gt;\Depot) s'il existe, sinon null.</summary>
    public static string? DefaultRepository => DefaultRepositoryFor(Layout, Directory.Exists);

    internal static string? DefaultRepositoryFor(AppLayout layout, Func<string, bool> directoryExists)
    {
        if (layout.PortableHome is null) return null;
        var depot = Path.Combine(layout.PortableHome, PortableRepositoryFolderName);
        return directoryExists(depot) ? depot : null;
    }

    /// <summary>
    /// Chemin de dépôt configuré : un chemin relatif l'est par rapport au dossier de la copie portable (ex. « Depot\Dell »
    /// reste valable quelle que soit la lettre de la clé) ; hors mode portable, il est rendu tel quel.
    /// </summary>
    public static string ResolveRepositoryPath(string path) => ResolveRepositoryPath(path, PortableHome);

    internal static string ResolveRepositoryPath(string path, string? portableHome)
    {
        if (portableHome is null || string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(portableHome, path));
    }

    /// <summary>Résumé d'une ligne : « Mode portable — données sur E:\SpokaPilote\Data » ou emplacement installé.</summary>
    public static string Describe() => Describe(Layout);

    public static string Describe(AppLayout layout) => layout.Mode switch
    {
        DataLocationMode.Portable => $"Mode portable — données sur {layout.Root}",
        DataLocationMode.Override => $"Données : {layout.Root} ({AppIdentity.Current.HomeVariable})" + (layout.IsPortable ? " — copie portable" : ""),
        _ => $"Données : {layout.Root}",
    };

    // ------------------------------------------------------------------ résolution (logique pure, testable)

    /// <summary>
    /// Résout les emplacements. <paramref name="homeOverride"/> (SPOKAPILOTE_HOME) est prioritaire ; sinon le fichier
    /// témoin <see cref="PortableMarkerFileName"/> dans <paramref name="executableDirectory"/> active le mode portable ;
    /// sinon %ProgramData%\SpokaPilote. <paramref name="elevated"/> = false : la version installée écrit dans
    /// %LocalAppData% (<see cref="AppLayout.UserRoot"/>).
    /// </summary>
    public static AppLayout Resolve(string? homeOverride, string executableDirectory, string programData, string localAppData,
        string machineName, string? machineId, Func<string, bool>? fileExists = null, bool elevated = true) =>
        Resolve(AppIdentity.Current, homeOverride, executableDirectory, programData, localAppData, machineName, machineId, fileExists, elevated);

    /// <summary>
    /// Résolution pour un produit donné : dossier %ProgramData%\&lt;<see cref="AppIdentity.DataFolderName"/>&gt; et
    /// fichier témoin <see cref="AppIdentity.PortableMarkerFileName"/> (aucun mode portable s'il est null).
    /// </summary>
    public static AppLayout Resolve(AppIdentity identity, string? homeOverride, string executableDirectory, string programData,
        string localAppData, string machineName, string? machineId, Func<string, bool>? fileExists = null, bool elevated = true)
    {
        fileExists ??= File.Exists;
        var exeDir = Path.TrimEndingDirectorySeparator(executableDirectory);
        var portableHome = identity.PortableMarkerFileName is { } marker && fileExists(Path.Combine(exeDir, marker)) ? exeDir : null;
        var installedUi = Path.Combine(localAppData, identity.DataFolderName, "ui.json");

        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            var root = homeOverride.Trim();
            return new AppLayout(DataLocationMode.Override, root, root, portableHome,
                portableHome is null ? installedUi : Path.Combine(root, "ui.json"));
        }
        if (portableHome is not null)
        {
            var root = Path.Combine(portableHome, PortableDataFolderName);
            return new AppLayout(DataLocationMode.Portable, root,
                Path.Combine(root, MachinesFolderName, MachineFolderName(machineName, machineId)),
                portableHome, Path.Combine(root, "ui.json"));
        }
        var installed = Path.Combine(programData, identity.DataFolderName);
        return new AppLayout(DataLocationMode.Installed, installed, installed, null, installedUi,
            elevated ? null : Path.Combine(localAppData, identity.DataFolderName));
    }

    /// <summary>
    /// Nom du dossier d'un poste : nom de l'ordinateur + 8 premiers caractères du MachineGuid (« PC-COMPTA_3f2a9c1b ») :
    /// deux postes clonés portant le même nom ne partagent jamais leur historique.
    /// </summary>
    public static string MachineFolderName(string machineName, string? machineId)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(machineName) ? "POSTE" : machineName.Trim().ToUpperInvariant(), 40);
        var id = machineId?.Replace("-", "").Trim().ToLowerInvariant();
        return id is { Length: >= 8 } && id.All(Uri.IsHexDigit) ? $"{name}_{id[..8]}" : name;
    }

    /// <summary>Le dossier contient-il une copie portable (fichier témoin) ?</summary>
    public static bool IsPortableDirectory(string directory) =>
        AppIdentity.Current.PortableMarkerFileName is { } marker && File.Exists(Path.Combine(directory, marker));

    private static AppLayout ResolveCurrent()
    {
        var exeDir = AppContext.BaseDirectory;
        var identity = AppIdentity.Current;
        return Resolve(
            identity,
            Environment.GetEnvironmentVariable(identity.HomeVariable),
            exeDir,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.MachineName,
            IsPortableDirectory(exeDir) ? ReadMachineGuidQuietly() : null,
            elevated: DataProtection.IsElevated);
    }

    /// <summary>MachineGuid de Windows, sans journalisation (le journal dépend lui-même de ces emplacements).</summary>
    private static string? ReadMachineGuidQuietly()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return Guid.TryParse((key?.GetValue("MachineGuid") as string)?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ utilitaires

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Dossier commun lu par un processus non élevé (version installée) : jamais créé par lui (il n'en a pas le droit) ;
    /// les exécutions élevées et l'installeur le créent. Les lecteurs tolèrent son absence.
    /// </summary>
    private static string EnsureIfWritable(string dir) => Layout.UserRoot is null ? Ensure(dir) : dir;

    /// <summary>Retourne un nom de fichier/dossier sûr à partir d'un texte quelconque.</summary>
    public static string SafeName(string value, int maxLength = 80)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var s = new string(chars).Trim('_', '.');
        if (s.Length > maxLength) s = s[..maxLength];
        return string.IsNullOrEmpty(s) ? "item" : s;
    }
}
