using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Matching;

namespace SpokaPilote.Core.Install;

public sealed class InterventionItem
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Types d'intervention (<see cref="Intervention.Kind"/>).</summary>
public static class InterventionKinds
{
    /// <summary>Installation de pilotes (valeur par défaut, historiques antérieurs compris).</summary>
    public const string Install = "install";
    /// <summary>Action de réparation sur un périphérique (redémarrage, réactivation, réinstallation).</summary>
    public const string DeviceRepair = "device-repair";
    /// <summary>Nettoyage du magasin de pilotes (suppression d'anciennes versions inutilisées).</summary>
    public const string DriverStoreClean = "driverstore-clean";
}

/// <summary>Origine de l'attribution d'un paquet à SpokaPilote (<see cref="AddedDriverPackage.Origin"/>).</summary>
public static class AddedPackageOrigins
{
    /// <summary>Nom publié renvoyé par « pnputil /add-driver » lancé par SpokaPilote : attribution certaine.</summary>
    public const string PnpUtil = "pnputil";
    /// <summary>
    /// Paquet apparu dans le magasin pendant l'étape d'un installeur constructeur ou de Windows Update (aucun nom renvoyé) :
    /// fenêtre réduite à cette seule étape.
    /// </summary>
    public const string Step = "etape";
}

/// <summary>
/// Paquet ajouté au magasin par une intervention, avec de quoi le reconnaître lors de l'annulation : un nom oemNN.inf peut
/// être réattribué à un autre paquet après une suppression, et un paquet ajouté en même temps par Windows Update ou
/// Intune ne doit jamais être retiré.
/// </summary>
public sealed class AddedDriverPackage
{
    public string PublishedName { get; set; } = "";
    /// <summary>Nom d'origine de l'INF (ex. nvhda.inf).</summary>
    public string? OriginalName { get; set; }
    /// <summary>Dossier du paquet dans le magasin (ex. nvhda.inf_amd64_0123abcd) : son suffixe dépend du contenu du paquet.</summary>
    public string? PackageId { get; set; }
    public string? Provider { get; set; }
    public string? ClassName { get; set; }
    public string? Version { get; set; }
    /// <summary><see cref="AddedPackageOrigins"/>.</summary>
    public string Origin { get; set; } = AddedPackageOrigins.PnpUtil;
    /// <summary>Proposition installée qui a ajouté ce paquet.</summary>
    public string? Candidate { get; set; }

    public override string ToString() =>
        $"{PublishedName}{(OriginalName is null ? "" : $" ({OriginalName})")}{(Version is null ? "" : $" {Version}")}{(Provider is null ? "" : $" — {Provider}")}";
}

/// <summary>
/// Trace d'une intervention : installation (paquets ajoutés au magasin de pilotes), réparation d'un périphérique
/// ou nettoyage du magasin (paquets retirés).
/// </summary>
public sealed class Intervention
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    /// <summary>Fin de l'intervention (absente des historiques antérieurs à 4.0.1).</summary>
    public DateTime? CompletedAt { get; set; }
    public string ComputerName { get; set; } = Environment.MachineName;
    /// <summary>MachineGuid du poste (absent des historiques antérieurs) : l'annulation n'est permise que sur ce poste.</summary>
    public string? MachineId { get; set; }
    /// <summary>Type d'intervention (<see cref="InterventionKinds"/>) ; absent dans les historiques antérieurs = installation.</summary>
    public string? Kind { get; set; }
    public List<InterventionItem> Items { get; set; } = new();
    /// <summary>Paquets publiés ajoutés au magasin pendant l'intervention (oemNN.inf).</summary>
    public List<string> AddedDriverPackages { get; set; } = new();
    /// <summary>
    /// Détail des paquets ajoutés par SpokaPilote lui-même (4.0.1 et suivantes), vérifié avant toute suppression. Vide pour
    /// un historique antérieur (<see cref="HasPackageDetails"/>) : l'annulation doit alors être demandée explicitement.
    /// </summary>
    public List<AddedDriverPackage> AddedPackages { get; set; } = new();
    /// <summary>Paquets publiés retirés du magasin (nettoyage) : non annulable, sauf sauvegarde (<see cref="BackupFolder"/>).</summary>
    public List<string> RemovedDriverPackages { get; set; } = new();
    public string? BackupFolder { get; set; }
    public bool RestorePointCreated { get; set; }
    public DateTime? RolledBackAt { get; set; }

    public bool CanRollback => RolledBackAt is null && AddedDriverPackages.Count > 0;
    [JsonIgnore] public bool HasPackageDetails => AddedPackages.Count > 0;
    public string Summary =>
        $"{Timestamp:dd/MM/yyyy HH:mm} — " + (Kind switch
        {
            InterventionKinds.DeviceRepair => $"réparation de périphérique : {string.Join(", ", Items.Select(i => $"{i.Title} ({i.Status})"))}",
            InterventionKinds.DriverStoreClean => $"nettoyage du magasin de pilotes : {RemovedDriverPackages.Count} paquet(s) retiré(s)",
            _ => $"{Items.Count(i => i.Status == nameof(InstallStatus.Installed))} pilote(s) installé(s)",
        }) +
        (RolledBackAt is not null ? $" — annulée le {RolledBackAt:dd/MM/yyyy HH:mm}" : "");
}

/// <summary>Options d'annulation.</summary>
public sealed class RollbackOptions
{
    /// <summary>
    /// Intervention désignée explicitement (identifiant, sélection dans l'interface) : sans limite d'âge, et permise pour
    /// un historique antérieur à 4.0.1 (vérifications réduites). Faux pour « rollback last » et la tâche distante.
    /// </summary>
    public bool Explicit { get; init; } = true;
    /// <summary>Ancienneté maximale d'une annulation non explicite (<see cref="Settings.RollbackLastMaxAgeDays"/>).</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    public static RollbackOptions Last(Settings settings) =>
        new() { Explicit = false, MaxAge = TimeSpan.FromDays(Math.Max(1, settings.RollbackLastMaxAgeDays)) };
}

/// <summary>Décision pour un paquet d'une intervention à annuler.</summary>
public sealed record RollbackStep(string PublishedName, bool Delete, string Reason);

/// <summary>Magasin de pilotes vu par l'annulation (remplaçable par un faux dans les tests : jamais de vrai pnputil).</summary>
public interface IDriverStore
{
    /// <summary>Description actuelle des paquets demandés présents dans le magasin (absents : non renvoyés).</summary>
    IReadOnlyDictionary<string, AddedDriverPackage> Describe(IEnumerable<string> publishedNames);
    /// <summary>Date de création de %windir%\INF\oemNN.inf (historiques antérieurs), null si absent.</summary>
    DateTime? PublishedInfCreated(string publishedName);
    Task<ProcessResult> DeleteAsync(string publishedName, CancellationToken ct);
    Task ScanDevicesAsync(CancellationToken ct);
}

/// <summary>
/// Historique des interventions et annulation : supprimer du magasin les paquets ajoutés par une intervention
/// (pnputil /delete-driver /uninstall) fait revenir chaque périphérique au pilote qu'il utilisait avant,
/// toujours présent dans le magasin. Seuls les paquets que SpokaPilote a lui-même ajoutés, et qui n'ont pas changé
/// depuis (nom d'origine, éditeur, version, dossier du magasin), sont retirés.
/// </summary>
public static class InterventionHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Historique antérieur à 4.0.1 : le paquet oemNN.inf doit avoir été créé dans cette fenêtre autour de l'intervention.</summary>
    internal static readonly TimeSpan LegacyCreationTolerance = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LegacyMaxDuration = TimeSpan.FromHours(12);

    public static string Folder => AppPaths.History;

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
            intervention.MachineId ??= SystemInfoProvider.ReadMachineId();
            DataProtection.WriteTextAtomically(Path.Combine(AppPaths.Ensure(Folder), intervention.Id + ".json"), JsonSerializer.Serialize(intervention, Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"Historique non enregistré : {ex.Message}");
        }
    }

    public static List<Intervention> List()
    {
        var list = new List<Intervention>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            // Exécution élevée : une entrée qui n'a pas été écrite par SYSTEM ou un administrateur (déposée par un
            // utilisateur pour faire retirer des pilotes choisis par lui) est ignorée.
            if (!DataProtection.IsTrustedForElevatedUse(file))
            {
                Log.Warn($"Entrée d'historique ignorée : {file} n'a pas été écrite par un administrateur.");
                continue;
            }
            try
            {
                var item = JsonSerializer.Deserialize<Intervention>(File.ReadAllText(file));
                if (item is not null) list.Add(item);
            }
            catch { /* entrée illisible ignorée */ }
        }
        return list.OrderByDescending(i => i.Timestamp).ToList();
    }

    public static Intervention? Find(string idOrLast) => Find(List(), idOrLast);

    internal static Intervention? Find(IReadOnlyList<Intervention> list, string idOrLast) =>
        IsLast(idOrLast) ? list.FirstOrDefault(i => i.CanRollback) : list.FirstOrDefault(i => i.Id == idOrLast);

    public static bool IsLast(string? idOrLast) =>
        idOrLast is null || idOrLast.Equals("last", StringComparison.OrdinalIgnoreCase) || idOrLast.Equals("derniere", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Annulation automatique (« rollback last », tâche distante) : refusée pour une intervention plus ancienne que
    /// <see cref="RollbackOptions.MaxAge"/> ou enregistrée par une version antérieure (paquets non vérifiables). Null si permise.
    /// </summary>
    public static string? CheckAutomaticRollback(Intervention intervention, RollbackOptions options, DateTime now)
    {
        if (options.Explicit) return null;
        if (!intervention.HasPackageDetails)
            return $"L'intervention {intervention.Id} a été enregistrée par une version antérieure à 4.0.1 : ses paquets ne peuvent pas être " +
                   $"vérifiés automatiquement. Contrôlez-les (« spokapilote history ») puis annulez-la explicitement : spokapilote rollback {intervention.Id}.";
        var age = now - intervention.Timestamp;
        if (age > options.MaxAge)
            return $"La dernière intervention annulable ({intervention.Id}, du {intervention.Timestamp:dd/MM/yyyy}) date de plus de " +
                   $"{options.MaxAge.TotalDays:0} jours : annulation automatique refusée (les pilotes ont pu être mis à jour depuis). " +
                   $"Après vérification : spokapilote rollback {intervention.Id} (limite réglable : RollbackLastMaxAgeDays).";
        return null;
    }

    public static Task<(bool Success, string Message)> RollbackAsync(Intervention intervention, IProgress<string>? progress = null, CancellationToken ct = default) =>
        RollbackAsync(intervention, new RollbackOptions(), progress, ct);

    public static Task<(bool Success, string Message)> RollbackAsync(Intervention intervention, RollbackOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator()) return Task.FromResult((false, "L'annulation nécessite les droits administrateur."));
        return RollbackAsync(intervention, options, WindowsDriverStore.Instance, DateTime.Now, Environment.MachineName, SystemInfoProvider.ReadMachineId(),
            sharedHistory: AppPaths.Layout.Mode == DataLocationMode.Portable, Save, progress, ct);
    }

    internal static async Task<(bool Success, string Message)> RollbackAsync(Intervention intervention, RollbackOptions options, IDriverStore store,
        DateTime now, string machineName, string? machineId, bool sharedHistory, Action<Intervention> save, IProgress<string>? progress, CancellationToken ct)
    {
        if (!intervention.CanRollback) return (false, "Cette intervention n'a rien à annuler (aucun paquet ajouté, ou déjà annulée).");
        if (CheckSameMachine(intervention, machineName, machineId, sharedHistory) is { } otherMachine) return (false, otherMachine);
        if (CheckAutomaticRollback(intervention, options, now) is { } refusal) return (false, refusal);

        progress?.Report("Vérification des paquets dans le magasin de pilotes...");
        var plan = Plan(intervention, store);
        var deleted = new List<string>();
        var errors = new List<string>();
        var reboot = false;
        foreach (var step in plan.Where(s => s.Delete))
        {
            progress?.Report($"Suppression de {step.PublishedName}...");
            var r = await store.DeleteAsync(step.PublishedName, ct).ConfigureAwait(false);
            if (r.ExitCode is 0 or PnpUtil.ErrorSuccessRebootRequired)
            {
                deleted.Add(step.PublishedName);
                reboot |= r.ExitCode == PnpUtil.ErrorSuccessRebootRequired;
            }
            else
            {
                errors.Add($"{step.PublishedName} (code {r.ExitCode} : encore utilisé par un périphérique ou refusé par Windows, laissé en place)");
            }
        }
        if (deleted.Count > 0)
        {
            progress?.Report("Réévaluation des périphériques...");
            await store.ScanDevicesAsync(ct).ConfigureAwait(false);
        }

        var skipped = plan.Where(s => !s.Delete).ToList();
        // Une annulation incomplète (paquet encore utilisé) reste possible plus tard : seuls les paquets restants seront retentés.
        if (errors.Count == 0)
        {
            intervention.RolledBackAt = now;
            save(intervention);
        }
        var parts = new List<string>();
        parts.Add(errors.Count == 0
            ? $"Intervention du {intervention.Timestamp:dd/MM/yyyy HH:mm} annulée : {deleted.Count} paquet(s) retiré(s), les périphériques reviennent à leur pilote précédent" +
              (reboot ? " (redémarrage nécessaire)" : "") + "."
            : $"Annulation partielle : {deleted.Count} paquet(s) retiré(s) ; échec pour {string.Join(", ", errors)}.");
        if (skipped.Count > 0)
            parts.Add($"Non retiré(s) : {string.Join(" ; ", skipped.Select(s => $"{s.PublishedName} ({s.Reason})"))}.");
        var message = string.Join(" ", parts);
        Log.Info(message);
        return (errors.Count == 0, message);
    }

    /// <summary>
    /// Paquets à retirer, revérifiés dans le magasin au moment de l'annulation :
    /// <list type="bullet">
    /// <item>4.0.1 et suivantes : seulement les paquets ajoutés par SpokaPilote (<see cref="Intervention.AddedPackages"/>)
    /// dont le nom publié existe encore ET dont le nom d'origine, l'éditeur, la version et le dossier du magasin n'ont pas
    /// changé (sinon le nom oemNN.inf a été réattribué, ou le pilote mis à jour par un autre outil) ;</item>
    /// <item>historique antérieur (noms seuls) : le paquet doit exister et son oemNN.inf avoir été créé pendant l'intervention.</item>
    /// </list>
    /// </summary>
    internal static List<RollbackStep> Plan(Intervention intervention, IDriverStore store)
    {
        var steps = new List<RollbackStep>();
        if (intervention.HasPackageDetails)
        {
            var current = store.Describe(intervention.AddedPackages.Select(p => p.PublishedName));
            foreach (var recorded in intervention.AddedPackages)
            {
                if (!PnpUtil.IsPublishedOemName(recorded.PublishedName))
                {
                    steps.Add(new RollbackStep(recorded.PublishedName, false, "nom de paquet inattendu"));
                    continue;
                }
                current.TryGetValue(recorded.PublishedName, out var now);
                var mismatch = Mismatch(recorded, now);
                steps.Add(new RollbackStep(recorded.PublishedName, mismatch is null, mismatch ?? "identique à l'installation"));
            }
            return steps;
        }

        var present = store.Describe(intervention.AddedDriverPackages);
        var from = intervention.Timestamp - LegacyCreationTolerance;
        var to = (intervention.CompletedAt ?? intervention.Timestamp + LegacyMaxDuration) + LegacyCreationTolerance;
        foreach (var name in intervention.AddedDriverPackages)
        {
            if (!PnpUtil.IsPublishedOemName(name)) { steps.Add(new RollbackStep(name, false, "nom de paquet inattendu")); continue; }
            if (!present.ContainsKey(name)) { steps.Add(new RollbackStep(name, false, "absent du magasin : déjà retiré")); continue; }
            var created = store.PublishedInfCreated(name);
            steps.Add(created is { } c && c >= from && c <= to
                ? new RollbackStep(name, true, "créé pendant l'intervention")
                : new RollbackStep(name, false, created is null
                    ? "date d'ajout illisible"
                    : $"ajouté le {created:dd/MM/yyyy HH:mm}, hors de l'intervention : nom réattribué à un autre paquet"));
        }
        return steps;
    }

    /// <summary>Raison de ne pas retirer ce paquet (null = il est toujours celui que SpokaPilote a installé).</summary>
    internal static string? Mismatch(AddedDriverPackage recorded, AddedDriverPackage? current)
    {
        if (current is null) return "absent du magasin : déjà retiré";
        if (recorded.OriginalName is null && recorded.PackageId is null)
            return "identité du paquet non enregistrée : vérifiez-le puis retirez-le à la main si nécessaire (pnputil /delete-driver)";
        var differences = new List<string>();
        void Compare(string label, string? before, string? after)
        {
            if (before is null) return;
            if (!string.Equals(before.Trim(), after?.Trim(), StringComparison.OrdinalIgnoreCase)) differences.Add($"{label} {before} → {after ?? "?"}");
        }
        Compare("INF", recorded.OriginalName, current.OriginalName);
        Compare("éditeur", recorded.Provider, current.Provider);
        Compare("classe", recorded.ClassName, current.ClassName);
        Compare("version", recorded.Version, current.Version);
        Compare("paquet", recorded.PackageId, current.PackageId);
        return differences.Count == 0 ? null : $"ne correspond plus au paquet installé ({string.Join(", ", differences)}) : laissé en place";
    }

    /// <summary>
    /// null si l'intervention a été faite sur ce poste, sinon la raison du refus. Les paquets à retirer (oemNN.inf) sont
    /// numérotés par chaque poste : annuler l'intervention d'un autre poste supprimerait d'autres pilotes. Garde-fou
    /// indispensable en mode portable, où une même clé conserve l'historique de nombreux postes.
    /// Sans MachineGuid enregistré (historiques antérieurs), le nom du poste fait foi pour un historique partagé
    /// (<paramref name="sharedHistory"/> : clé portable) ; un historique local (%ProgramData%) est par nature celui du poste.
    /// </summary>
    public static string? CheckSameMachine(Intervention intervention, string machineName, string? machineId, bool sharedHistory)
    {
        if (intervention.MachineId is { } recorded && machineId is not null)
            return string.Equals(recorded, machineId, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Cette intervention a été réalisée sur un autre poste ({intervention.ComputerName}) : elle ne peut être annulée que sur ce poste.";
        return !sharedHistory || string.Equals(intervention.ComputerName, machineName, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Cette intervention a été réalisée sur le poste {intervention.ComputerName} (poste actuel : {machineName}) : elle ne peut être annulée que sur ce poste.";
    }

    /// <summary>Réinstalle tous les pilotes d'une sauvegarde (dossier produit avant chaque intervention).</summary>
    public static async Task<(bool Success, string Message)> RestoreBackupAsync(string backupFolder, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator()) return (false, "La restauration nécessite les droits administrateur.");
        if (!Directory.Exists(backupFolder)) return (false, $"Sauvegarde introuvable : {backupFolder}");
        if (!DataProtection.IsTrustedForElevatedUse(backupFolder))
            return (false, $"Sauvegarde refusée : {backupFolder} n'a pas été créée par un administrateur.");
        var r = await PnpUtil.AddDriversRecursiveAsync(backupFolder, install: true, ct).ConfigureAwait(false);
        return PnpUtil.IsSuccess(r.ExitCode)
            ? (true, $"Pilotes de la sauvegarde {Path.GetFileName(backupFolder)} réinstallés.")
            : (false, $"Restauration incomplète (pnputil code {r.ExitCode}).");
    }

    public static List<string> Backups() =>
        Directory.Exists(AppPaths.Backups) ? Directory.GetDirectories(AppPaths.Backups).OrderByDescending(d => d).ToList() : new List<string>();

    /// <summary>Description des paquets du magasin pour l'enregistrement d'une intervention (vide si illisible).</summary>
    internal static IReadOnlyDictionary<string, AddedDriverPackage> DescribeStore(IEnumerable<string> publishedNames) =>
        WindowsDriverStore.Instance.Describe(publishedNames);
}

/// <summary>Magasin de pilotes de ce poste : base DriverDatabase du registre, INF du magasin, pnputil.</summary>
internal sealed class WindowsDriverStore : IDriverStore
{
    public static readonly WindowsDriverStore Instance = new();

    private static string InfFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    public IReadOnlyDictionary<string, AddedDriverPackage> Describe(IEnumerable<string> publishedNames)
    {
        var result = new Dictionary<string, AddedDriverPackage>(StringComparer.OrdinalIgnoreCase);
        var names = publishedNames.Where(PnpUtil.IsPublishedOemName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return result;
        var packageIds = DriverStoreCleaner.ReadPackageIdsForHistory();
        foreach (var name in names)
        {
            var oemPath = Path.Combine(InfFolder, name);
            if (!File.Exists(oemPath)) continue;
            packageIds.TryGetValue(name, out var packageId);
            var original = DriverStoreCleaner.OriginalNameFromPackageId(packageId);
            var folder = packageId is null ? null : Path.Combine(DriverStoreCleaner.FileRepositoryFolder, packageId);
            var infPath = folder is not null && original is not null && File.Exists(Path.Combine(folder, original)) ? Path.Combine(folder, original) : oemPath;
            InfInfo? info = null;
            try { info = InfParser.Parse(infPath); }
            catch (Exception ex) { Log.Debug($"INF illisible {infPath} : {ex.Message}"); }
            result[name] = new AddedDriverPackage
            {
                PublishedName = name.ToLowerInvariant(),
                OriginalName = original,
                PackageId = packageId,
                Provider = info?.Provider,
                ClassName = info?.Class,
                Version = info?.DriverVersion,
            };
        }
        return result;
    }

    public DateTime? PublishedInfCreated(string publishedName)
    {
        var path = Path.Combine(InfFolder, publishedName);
        return File.Exists(path) ? File.GetCreationTime(path) : null;
    }

    public Task<ProcessResult> DeleteAsync(string publishedName, CancellationToken ct) => PnpUtil.DeleteDriverAsync(publishedName, ct);

    public Task ScanDevicesAsync(CancellationToken ct) => PnpUtil.ScanDevicesAsync(ct);
}
