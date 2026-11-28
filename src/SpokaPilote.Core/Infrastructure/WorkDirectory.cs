namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Dossier de travail propre à une exécution (téléchargements, extractions, installeurs constructeurs) : nom aléatoire,
/// créé neuf sous <see cref="AppPaths.Work"/> avec une DACL protégée (SYSTEM + Administrateurs) dès sa création quand
/// le processus est élevé. Rien n'y est jamais réutilisé d'une exécution à l'autre : un fichier vérifié (empreinte,
/// signature) est exécuté depuis ce dossier, hors de portée des utilisateurs, puis tout est supprimé à la fin
/// (<see cref="Dispose"/>), que l'installation réussisse ou non. Les dossiers abandonnés (processus interrompu) sont
/// supprimés par <see cref="CleanupLeftovers()"/> selon leur âge et la place occupée.
/// </summary>
public sealed class WorkDirectory : IDisposable
{
    public const string FolderName = "Work";
    /// <summary>Fichier tenu ouvert pendant l'exécution (supprimé à la fermeture) : dossier « en cours », jamais nettoyé.</summary>
    internal const string LockFileName = ".en-cours";
    internal static readonly TimeSpan LeftoverMaxAge = TimeSpan.FromDays(2);
    internal const long LeftoverMaxTotalBytes = 10L * 1024 * 1024 * 1024;
    /// <summary>Anciens dossiers partagés (versions 4.0.0 et antérieures), réutilisés d'une exécution à l'autre : supprimés.</summary>
    internal static readonly string[] LegacyFolders = ["Downloads", "Extracted"];

    private FileStream? _lock;
    private bool _disposed;

    public string Path { get; }

    private WorkDirectory(string path, FileStream? lockFile)
    {
        Path = path;
        _lock = lockFile;
    }

    /// <summary>Nouveau dossier de travail (<paramref name="purpose"/> : préfixe court et lisible, ex. « install »).</summary>
    public static WorkDirectory Create(string purpose)
    {
        var parent = AppPaths.Work;
        CleanupLeftovers(parent, DateTime.Now, LeftoverMaxAge, LeftoverMaxTotalBytes);
        return Create(parent, purpose, DataProtection.Enforced);
    }

    internal static WorkDirectory Create(string parent, string purpose, bool protect)
    {
        var path = System.IO.Path.Combine(parent, $"{AppPaths.SafeName(purpose, 16)}-{DataProtection.RandomName(6)}");
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException($"Dossier de travail déjà présent : {path}");
        if (protect)
        {
            DataProtection.Security.CreateProtectedDirectory(path, DataProtection.PrivateRules);
            // Contrôle après coup : le dossier doit être le nôtre (ni lien, ni propriétaire inattendu).
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !DataProtection.IsTrustedOwner(DataProtection.Security.GetOwner(path, true)))
                throw new IOException($"Dossier de travail {path} non sûr : abandon.");
        }
        else
        {
            Directory.CreateDirectory(path);
        }
        var lockFile = new FileStream(System.IO.Path.Combine(path, LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new WorkDirectory(path, lockFile);
    }

    /// <summary>Sous-dossier neuf (supprimé puis recréé s'il existe déjà dans ce dossier de travail).</summary>
    public string NewSubdirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, AppPaths.SafeName(name, 60));
        if (Directory.Exists(path)) DataProtection.DeleteTree(path);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Chemin d'un sous-dossier qui n'existe pas encore (un extracteur exige parfois de le créer lui-même).</summary>
    public string SubdirectoryPath(string name)
    {
        var path = System.IO.Path.Combine(Path, AppPaths.SafeName(name, 60));
        if (Directory.Exists(path)) DataProtection.DeleteTree(path);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock?.Dispose();
        _lock = null;
        if (!DataProtection.DeleteTree(Path))
            Log.Debug($"Dossier de travail {Path} non entièrement supprimé (fichier encore ouvert ?) : il le sera au prochain nettoyage.");
    }

    // ------------------------------------------------------------------ nettoyage

    /// <summary>Nettoyage des dossiers de travail abandonnés et des anciens dossiers partagés (démarrage élevé).</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            CleanupLeftovers(AppPaths.Work, DateTime.Now, LeftoverMaxAge, LeftoverMaxTotalBytes);
            foreach (var legacy in LegacyFolders)
            {
                var path = System.IO.Path.Combine(AppPaths.WritableRoot, legacy);
                if (!Directory.Exists(path)) continue;
                var size = DataProtection.TreeSize(path);
                if (DataProtection.DeleteTree(path))
                    Log.Info($"Ancien dossier {legacy} supprimé ({size / 1048576.0:0} Mo) : les téléchargements se font désormais dans un dossier propre à chaque exécution.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Nettoyage des dossiers de travail impossible : {ex.Message}");
        }
    }

    /// <summary>
    /// Supprime les dossiers de travail abandonnés : plus vieux que <paramref name="maxAge"/>, puis les plus anciens tant que
    /// l'ensemble dépasse <paramref name="maxTotalBytes"/>. Un dossier en cours d'utilisation (fichier témoin ouvert par
    /// un autre processus) n'est jamais touché. Renvoie le nombre de dossiers supprimés.
    /// </summary>
    internal static int CleanupLeftovers(string parent, DateTime now, TimeSpan maxAge, long maxTotalBytes)
    {
        if (!Directory.Exists(parent)) return 0;
        var removed = 0;
        var idle = new List<(DirectoryInfo Dir, long Size)>();
        foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories().OrderBy(d => d.CreationTimeUtc))
        {
            if (InUse(dir.FullName)) continue;
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || now.ToUniversalTime() - dir.CreationTimeUtc > maxAge)
            {
                if (DataProtection.DeleteTree(dir.FullName)) removed++;
                continue;
            }
            idle.Add((dir, DataProtection.TreeSize(dir.FullName)));
        }
        var total = idle.Sum(d => d.Size);
        foreach (var (dir, size) in idle)
        {
            if (total <= maxTotalBytes) break;
            if (DataProtection.DeleteTree(dir.FullName)) { removed++; total -= size; }
        }
        if (removed > 0) Log.Info($"{removed} dossier(s) de travail abandonné(s) supprimé(s) dans {parent}.");
        return removed;
    }

    /// <summary>Le fichier témoin est tenu ouvert sans partage par le processus propriétaire : la suppression échoue.</summary>
    private static bool InUse(string dir)
    {
        var lockPath = System.IO.Path.Combine(dir, LockFileName);
        if (!File.Exists(lockPath)) return false;
        try
        {
            File.Delete(lockPath);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
