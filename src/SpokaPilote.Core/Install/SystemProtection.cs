using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

/// <summary>Filets de sécurité avant toute installation : point de restauration et sauvegarde des pilotes tiers.</summary>
public static class SystemProtection
{
    /// <summary>Crée un point de restauration système (type 10 = DEVICE_DRIVER_INSTALL).</summary>
    public static async Task<bool> CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var mc = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
                using var inParams = mc.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 10;
                inParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE
                using var outParams = mc.InvokeMethod("CreateRestorePoint", inParams, null);
                var code = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
                if (code == 0)
                {
                    Log.Info($"Point de restauration créé : {description}");
                    return true;
                }
                Log.Warn($"Point de restauration non créé (code {code}). La protection du système est peut-être désactivée, " +
                         "ou un point a déjà été créé dans les dernières 24 h.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"Point de restauration impossible : {ex.Message}");
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exporte tous les pilotes tiers installés (pnputil /export-driver *, 1 à 4 Go) vers un dossier horodaté, sauf si le
    /// magasin n'a pas changé depuis la dernière sauvegarde complète (même liste de oemNN.inf, mêmes tailles et dates) :
    /// celle-ci est alors réutilisée. Applique ensuite la rétention (<see cref="BackupRetention"/>).
    /// </summary>
    public static async Task<string?> BackupDriversAsync(BackupRetention retention, CancellationToken ct = default)
    {
        var root = AppPaths.Backups;
        var fingerprint = DriverStoreFingerprint();
        if (fingerprint is not null && FindReusableBackup(root, fingerprint) is { } reused)
        {
            Log.Info($"Magasin de pilotes inchangé depuis la sauvegarde {Path.GetFileName(reused)} : réutilisée (aucune nouvelle copie).");
            PruneBackups(root, retention, ProtectedBackups(reused));
            return reused;
        }

        var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{n}");
        Directory.CreateDirectory(dir);
        var result = await PnpUtil.ExportAllAsync(dir, ct).ConfigureAwait(false);
        var complete = result.ExitCode == 0;
        string? kept = complete || Directory.EnumerateFileSystemEntries(dir).Any() ? dir : null;
        if (complete) Log.Info($"Pilotes actuels sauvegardés dans {dir}");
        else Log.Warn($"Sauvegarde des pilotes incomplète (code {result.ExitCode}).");
        if (kept is not null)
            WriteManifest(dir, new BackupManifest { CreatedAt = DateTime.Now, StoreFingerprint = complete ? fingerprint : null, Complete = complete, SizeBytes = DataProtection.TreeSize(dir) });
        else
            DataProtection.DeleteTree(dir);
        PruneBackups(root, retention, ProtectedBackups(kept));
        return kept;
    }

    public static Task<string?> BackupDriversAsync(CancellationToken ct = default) => BackupDriversAsync(BackupRetention.From(Settings.Load()), ct);

    // ------------------------------------------------------------------ rétention des sauvegardes

    /// <summary>Fichier décrivant une sauvegarde complète (dans son dossier ; ignoré par la restauration, qui ne lit que les INF).</summary>
    internal const string ManifestFileName = "sauvegarde.spokapilote.json";

    internal sealed class BackupManifest
    {
        public DateTime CreatedAt { get; set; }
        /// <summary>Empreinte du magasin au moment de la sauvegarde (null si sauvegarde incomplète : jamais réutilisée).</summary>
        public string? StoreFingerprint { get; set; }
        public bool Complete { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>Sauvegarde complète faite avant une installation (« 20261128-101500 », « 20261128-101500-2 »).</summary>
    internal static bool IsFullBackupName(string name) => Regex.IsMatch(name, @"^\d{8}-\d{6}(-\d+)?$");

    /// <summary>
    /// Empreinte du magasin de pilotes tiers : liste triée des %windir%\INF\oemNN.inf avec taille et date. Tout ajout,
    /// retrait ou remplacement d'un paquet la change. Null si le magasin est illisible.
    /// </summary>
    internal static string? DriverStoreFingerprint() =>
        DriverStoreFingerprint(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"));

    internal static string? DriverStoreFingerprint(string infFolder)
    {
        try
        {
            var lines = new DirectoryInfo(infFolder).GetFiles("oem*.inf")
                .Select(f => $"{f.Name.ToLowerInvariant()}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
                .OrderBy(l => l, StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Empreinte du magasin de pilotes impossible : {ex.Message}");
            return null;
        }
    }

    /// <summary>Sauvegarde complète la plus récente faite avec ce même magasin (dossier présent, écrit par un administrateur).</summary>
    internal static string? FindReusableBackup(string root, string fingerprint)
    {
        if (!Directory.Exists(root)) return null;
        var latest = Directory.GetDirectories(root).Where(d => IsFullBackupName(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
        if (latest is null || ReadManifest(latest) is not { Complete: true } manifest) return null;
        return manifest.StoreFingerprint == fingerprint && DataProtection.IsTrustedForElevatedUse(latest) ? latest : null;
    }

    internal static BackupManifest? ReadManifest(string backup)
    {
        var path = Path.Combine(backup, ManifestFileName);
        try
        {
            if (!File.Exists(path) || !DataProtection.IsTrustedForElevatedUse(path)) return null;
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteManifest(string backup, BackupManifest manifest)
    {
        try { DataProtection.WriteTextAtomically(Path.Combine(backup, ManifestFileName), JsonSerializer.Serialize(manifest)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"Description de la sauvegarde non écrite : {ex.Message}"); }
    }

    /// <summary>
    /// Sauvegardes jamais supprimées par la rétention : celle qui vient d'être faite (ou réutilisée) et celle de la dernière
    /// intervention encore annulable (restauration possible après une annulation).
    /// </summary>
    private static HashSet<string> ProtectedBackups(string? current)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current is not null) set.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)));
        try
        {
            if (InterventionHistory.List().FirstOrDefault(i => i.CanRollback && i.BackupFolder is not null) is { BackupFolder: { } referenced })
                set.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(referenced)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"Historique illisible pour la rétention des sauvegardes : {ex.Message}");
        }
        return set;
    }

    /// <summary>
    /// Rétention des sauvegardes complètes : les <see cref="BackupRetention.KeepCount"/> plus récentes, puis les plus anciennes
    /// d'entre elles retirées tant que l'ensemble dépasse <see cref="BackupRetention.MaxTotalBytes"/>. La plus récente et les
    /// sauvegardes protégées (<paramref name="protectedFolders"/>) ne sont jamais supprimées. Les sauvegardes du nettoyage du
    /// magasin (« driverstore-… », seulement les paquets retirés, peu volumineuses) ne sont pas concernées. Renvoie les dossiers supprimés.
    /// </summary>
    internal static List<string> PruneBackups(string root, BackupRetention retention, IReadOnlySet<string> protectedFolders)
    {
        var removed = new List<string>();
        if (!Directory.Exists(root)) return removed;
        var backups = Directory.GetDirectories(root)
            .Where(d => IsFullBackupName(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .ToList();
        if (backups.Count == 0) return removed;
        bool IsProtected(string d) => d == backups[0] || protectedFolders.Contains(d);

        var keepCount = Math.Max(1, retention.KeepCount);
        var kept = new List<(string Dir, long Size)>();
        foreach (var (dir, index) in backups.Select((d, i) => (d, i)))
        {
            if (index < keepCount || IsProtected(dir)) kept.Add((dir, SizeOf(dir)));
            else if (Delete(dir)) removed.Add(dir);
        }
        if (retention.MaxTotalBytes > 0)
        {
            var total = kept.Sum(k => k.Size);
            foreach (var (dir, size) in kept.AsEnumerable().Reverse())
            {
                if (total <= retention.MaxTotalBytes) break;
                if (IsProtected(dir)) continue;
                if (Delete(dir)) { removed.Add(dir); total -= size; }
            }
        }
        return removed;

        static bool Delete(string dir)
        {
            var size = SizeOf(dir);
            if (!DataProtection.DeleteTree(dir)) return false;
            Log.Info($"Ancienne sauvegarde de pilotes supprimée (rétention) : {Path.GetFileName(dir)} ({size / 1048576.0:0} Mo libérés).");
            return true;
        }
    }

    private static long SizeOf(string backup) => ReadManifest(backup) is { SizeBytes: > 0 } m ? m.SizeBytes : DataProtection.TreeSize(backup);

    /// <summary>Démarrage élevé : rétention appliquée si les sauvegardes complètes dépassent le nombre gardé (anciens postes).</summary>
    internal static void PruneBackupsAtStartup()
    {
        try
        {
            var root = Path.Combine(AppPaths.MachineRoot, "Backups");
            if (!Directory.Exists(root)) return;
            var retention = BackupRetention.From(Settings.Load());
            if (Directory.GetDirectories(root).Count(d => IsFullBackupName(Path.GetFileName(d))) <= retention.KeepCount) return;
            PruneBackups(root, retention, ProtectedBackups(null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Rétention des sauvegardes impossible : {ex.Message}");
        }
    }
}

/// <summary>Rétention des sauvegardes complètes de pilotes (<see cref="Settings.BackupRetentionCount"/>, <see cref="Settings.BackupMaxTotalSizeMB"/>).</summary>
public sealed record BackupRetention(int KeepCount, long MaxTotalBytes)
{
    public static BackupRetention From(Settings settings) =>
        new(Math.Max(1, settings.BackupRetentionCount), Math.Max(0, (long)settings.BackupMaxTotalSizeMB) * 1024 * 1024);
}
