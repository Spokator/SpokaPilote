using System.Text.Json;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

public sealed class InterventionItem
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Trace d'une installation : ce qui a été installé et quels paquets ont été ajoutés au magasin de pilotes.</summary>
public sealed class Intervention
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ComputerName { get; set; } = Environment.MachineName;
    public List<InterventionItem> Items { get; set; } = new();
    /// <summary>Paquets publiés ajoutés au magasin pendant l'intervention (oemNN.inf).</summary>
    public List<string> AddedDriverPackages { get; set; } = new();
    public string? BackupFolder { get; set; }
    public bool RestorePointCreated { get; set; }
    public DateTime? RolledBackAt { get; set; }

    public bool CanRollback => RolledBackAt is null && AddedDriverPackages.Count > 0;
    public string Summary =>
        $"{Timestamp:dd/MM/yyyy HH:mm} — {Items.Count(i => i.Status == nameof(InstallStatus.Installed))} pilote(s) installé(s)" +
        (RolledBackAt is not null ? $" — annulée le {RolledBackAt:dd/MM/yyyy HH:mm}" : "");
}

/// <summary>
/// Historique des interventions et annulation : supprimer du magasin les paquets ajoutés par une intervention
/// (pnputil /delete-driver /uninstall) fait revenir chaque périphérique au pilote qu'il utilisait avant,
/// toujours présent dans le magasin.
/// </summary>
public static class InterventionHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => AppPaths.Ensure(Path.Combine(AppPaths.Root, "History"));

    /// <summary>Noms publiés des pilotes tiers présents dans le magasin (%windir%\INF\oem*.inf).</summary>
    public static HashSet<string> SnapshotDriverStore()
    {
        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        try
        {
            return Directory.GetFiles(inf, "oem*.inf").Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture du magasin de pilotes impossible : {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(Intervention intervention)
    {
        try
        {
            File.WriteAllText(Path.Combine(Folder, intervention.Id + ".json"), JsonSerializer.Serialize(intervention, Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"Historique non enregistré : {ex.Message}");
        }
    }

    public static List<Intervention> List()
    {
        var list = new List<Intervention>();
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<Intervention>(File.ReadAllText(file));
                if (item is not null) list.Add(item);
            }
            catch { /* entrée illisible ignorée */ }
        }
        return list.OrderByDescending(i => i.Timestamp).ToList();
    }

    public static Intervention? Find(string idOrLast) =>
        idOrLast.Equals("last", StringComparison.OrdinalIgnoreCase) || idOrLast.Equals("derniere", StringComparison.OrdinalIgnoreCase)
            ? List().FirstOrDefault(i => i.CanRollback)
            : List().FirstOrDefault(i => i.Id == idOrLast);

    public static async Task<(bool Success, string Message)> RollbackAsync(Intervention intervention, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator()) return (false, "L'annulation nécessite les droits administrateur.");
        if (!intervention.CanRollback) return (false, "Cette intervention n'a rien à annuler (aucun paquet ajouté, ou déjà annulée).");

        var errors = new List<string>();
        foreach (var package in intervention.AddedDriverPackages)
        {
            progress?.Report($"Suppression de {package}...");
            var r = await PnpUtil.DeleteDriverAsync(package, ct).ConfigureAwait(false);
            if (r.ExitCode != 0 && r.ExitCode != PnpUtil.ErrorSuccessRebootRequired)
                errors.Add($"{package} (code {r.ExitCode})");
        }
        progress?.Report("Réévaluation des périphériques...");
        await PnpUtil.ScanDevicesAsync(ct).ConfigureAwait(false);

        intervention.RolledBackAt = DateTime.Now;
        Save(intervention);
        var message = errors.Count == 0
            ? $"Intervention du {intervention.Timestamp:dd/MM/yyyy HH:mm} annulée : {intervention.AddedDriverPackages.Count} paquet(s) retiré(s), les périphériques reviennent à leur pilote précédent."
            : $"Annulation partielle : échec pour {string.Join(", ", errors)}.";
        Log.Info(message);
        return (errors.Count == 0, message);
    }

    /// <summary>Réinstalle tous les pilotes d'une sauvegarde (dossier produit avant chaque intervention).</summary>
    public static async Task<(bool Success, string Message)> RestoreBackupAsync(string backupFolder, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator()) return (false, "La restauration nécessite les droits administrateur.");
        if (!Directory.Exists(backupFolder)) return (false, $"Sauvegarde introuvable : {backupFolder}");
        var r = await PnpUtil.AddDriversRecursiveAsync(backupFolder, install: true, ct).ConfigureAwait(false);
        return PnpUtil.IsSuccess(r.ExitCode)
            ? (true, $"Pilotes de la sauvegarde {Path.GetFileName(backupFolder)} réinstallés.")
            : (false, $"Restauration incomplète (pnputil code {r.ExitCode}).");
    }

    public static List<string> Backups() =>
        Directory.GetDirectories(AppPaths.Backups).OrderByDescending(d => d).ToList();
}
