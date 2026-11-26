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
public sealed record AppLayout(DataLocationMode Mode, string Root, string MachineRoot, string? PortableHome, string UiPreferencesFile)
{
    /// <summary>Exécutables lancés depuis une copie portable (fichier témoin présent), quel que soit l'emplacement des données.</summary>
    public bool IsPortable => PortableHome is not null;
}

/// <summary>
/// Emplacements disque utilisés par SpokaPilote. Par ordre de priorité :
/// 1. SPOKAPILOTE_HOME (variable d'environnement) ;
/// 2. mode portable : fichier « SpokaPilote.portable » à côté de l'exécutable → &lt;dossier de l'exe&gt;\Data ;
/// 3. version installée : %ProgramData%\SpokaPilote.
/// </summary>
public static class AppPaths
{
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

    public static string Cache => Ensure(Path.Combine(Root, "Cache"));
    public static string Downloads => Ensure(Path.Combine(Root, "Downloads"));
    public static string Extracted => Ensure(Path.Combine(Root, "Extracted"));
    public static string Reports => Ensure(Path.Combine(Root, "Reports"));
    public static string Logs => Ensure(Path.Combine(MachineRoot, "Logs"));
    public static string Backups => Ensure(Path.Combine(MachineRoot, "Backups"));
    public static string History => Ensure(Path.Combine(MachineRoot, "History"));
    /// <summary>Archives portables téléchargées par la mise à jour (mode portable).</summary>
    public static string Updates => Ensure(Path.Combine(Root, "Mises-a-jour"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");
    /// <summary>Dernière politique de pilotes valide (fonctionnement hors ligne).</summary>
    public static string PolicyCache => Path.Combine(Ensure(Root), "policy-cache.json");
    public static string UiPreferencesFile => Layout.UiPreferencesFile;

    /// <summary>
    /// Points de montage DISM (images Windows). DISM exige un volume NTFS local : en mode portable (clé souvent en
    /// FAT32/exFAT), dossier temporaire du poste, vidé après chaque injection.
    /// </summary>
    public static string Mount => Ensure(Layout.Mode == DataLocationMode.Portable
        ? Path.Combine(Path.GetTempPath(), "SpokaPilote", "Mount")
        : Path.Combine(Root, "Mount"));

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
        DataLocationMode.Override => $"Données : {layout.Root} ({HomeVariable})" + (layout.IsPortable ? " — copie portable" : ""),
        _ => $"Données : {layout.Root}",
    };

    // ------------------------------------------------------------------ résolution (logique pure, testable)

    /// <summary>
    /// Résout les emplacements. <paramref name="homeOverride"/> (SPOKAPILOTE_HOME) est prioritaire ; sinon le fichier
    /// témoin <see cref="PortableMarkerFileName"/> dans <paramref name="executableDirectory"/> active le mode portable ;
    /// sinon %ProgramData%\SpokaPilote.
    /// </summary>
    public static AppLayout Resolve(string? homeOverride, string executableDirectory, string programData, string localAppData,
        string machineName, string? machineId, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var exeDir = Path.TrimEndingDirectorySeparator(executableDirectory);
        var portableHome = fileExists(Path.Combine(exeDir, PortableMarkerFileName)) ? exeDir : null;
        var installedUi = Path.Combine(localAppData, "SpokaPilote", "ui.json");

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
        var installed = Path.Combine(programData, "SpokaPilote");
        return new AppLayout(DataLocationMode.Installed, installed, installed, null, installedUi);
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
    public static bool IsPortableDirectory(string directory) => File.Exists(Path.Combine(directory, PortableMarkerFileName));

    private static AppLayout ResolveCurrent()
    {
        var exeDir = AppContext.BaseDirectory;
        return Resolve(
            Environment.GetEnvironmentVariable(HomeVariable),
            exeDir,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.MachineName,
            IsPortableDirectory(exeDir) ? ReadMachineGuidQuietly() : null);
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
