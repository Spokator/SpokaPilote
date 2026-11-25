using System.Management;
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

    /// <summary>Exporte tous les pilotes tiers installés (pnputil /export-driver *) vers un dossier horodaté.</summary>
    public static async Task<string?> BackupDriversAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.Backups, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var result = await PnpUtil.ExportAllAsync(dir, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            Log.Info($"Pilotes actuels sauvegardés dans {dir}");
            return dir;
        }
        Log.Warn($"Sauvegarde des pilotes incomplète (code {result.ExitCode}).");
        return Directory.EnumerateFileSystemEntries(dir).Any() ? dir : null;
    }
}
