using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Repair;

/// <summary>Actions logicielles de réparation (toutes via pnputil, l'outil Microsoft officiel).</summary>
public enum RepairActionKind
{
    /// <summary>Redémarre le périphérique (pnputil /restart-device).</summary>
    Restart,
    /// <summary>Réactive le périphérique (pnputil /enable-device).</summary>
    Enable,
    /// <summary>Retire le périphérique puis le fait redétecter : Windows réinstalle le meilleur pilote du magasin (pnputil /remove-device + /scan-devices).</summary>
    Reinstall,
    /// <summary>Recherche les modifications matérielles (pnputil /scan-devices).</summary>
    Rescan,
}

public sealed record RepairAction(RepairActionKind Kind, string Label, string Explanation)
{
    /// <summary>Nom utilisé par la ligne de commande (--action restart|enable|reinstall|rescan).</summary>
    public string CliName => Kind.ToString().ToLowerInvariant();
}

/// <summary>Diagnostic et actions proposées pour un périphérique.</summary>
public sealed class RepairPlan
{
    public required DeviceInfo Device { get; init; }
    /// <summary>Explication du code d'erreur, en français.</summary>
    public string Diagnosis { get; init; } = "";
    /// <summary>Conseils à appliquer à la main (physiques, BIOS, redémarrage du poste...).</summary>
    public List<string> Advice { get; init; } = new();
    /// <summary>Actions logicielles possibles, la première étant la recommandée. Vide pour une panne matérielle.</summary>
    public List<RepairAction> Actions { get; init; } = new();
    public bool IsHardwareFault { get; init; }
    /// <summary>Motif de prudence (périphérique critique) : une confirmation explicite est alors obligatoire.</summary>
    public string? CriticalReason { get; init; }
    /// <summary>Commande SpokaPilote utile en complément (ex. recherche de pilote).</summary>
    public string? SuggestedCommand { get; init; }

    public bool IsCritical => CriticalReason is not null;
    public RepairAction? Recommended => Actions.FirstOrDefault();
    public bool Offers(RepairActionKind kind) => Actions.Any(a => a.Kind == kind);
}

public sealed class RepairOptions
{
    /// <summary>Simulation : affiche ce qui serait fait, n'exécute rien.</summary>
    public bool DryRun { get; init; }
    /// <summary>Confirmation explicite pour un périphérique critique (jamais déduite de « --yes »).</summary>
    public bool ConfirmCritical { get; init; }
    /// <summary>Délai maximal d'attente du nouvel état du périphérique après l'action.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public bool RecordHistory { get; init; } = true;
}

public enum RepairStatus
{
    /// <summary>Action exécutée, le périphérique fonctionne.</summary>
    Resolved,
    /// <summary>Action exécutée, le problème persiste (ou un redémarrage du poste est nécessaire).</summary>
    NotResolved,
    /// <summary>pnputil a échoué.</summary>
    Failed,
    /// <summary>Action refusée (non proposée, périphérique critique non confirmé, droits, Windows trop ancien).</summary>
    Refused,
    /// <summary>Simulation.</summary>
    Simulated,
}

public sealed class RepairResult
{
    public RepairStatus Status { get; init; }
    public RepairActionKind? Action { get; init; }
    public int ErrorCodeBefore { get; init; }
    public int? ErrorCodeAfter { get; init; }
    public bool? PresentAfter { get; init; }
    public bool RebootRequired { get; init; }
    public string Message { get; init; } = "";
    /// <summary>Commandes pnputil exécutées (ou qui le seraient en simulation).</summary>
    public List<string> Commands { get; init; } = new();
    public bool Executed => Status is RepairStatus.Resolved or RepairStatus.NotResolved or RepairStatus.Failed;
}

/// <summary>
/// Réparation guidée d'un périphérique en erreur : diagnostic du code d'erreur du Gestionnaire de périphériques,
/// actions adaptées (pnputil), contrôle de l'état après l'action (relecture WMI) et trace dans l'historique.
/// <para>
/// Périphériques critiques (confirmation explicite obligatoire, voir <see cref="CriticalClasses"/>) : contrôleurs de
/// disque et de stockage, volumes, processeurs, périphériques système (bus PCI, ACPI...), carte graphique, carte
/// réseau (coupe une session à distance), contrôleurs et concentrateurs USB racine, et le clavier ou la souris quand
/// ce sont les seuls en état de marche.
/// </para>
/// </summary>
public static class DeviceRepair
{
    /// <summary>Classes de périphériques critiques → motif affiché au technicien.</summary>
    public static readonly IReadOnlyDictionary<string, string> CriticalClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DiskDrive"] = "disque : une erreur peut rendre le système ou des données inaccessibles",
        ["HDC"] = "contrôleur de disque (SATA/IDE) : le disque système peut en dépendre",
        ["SCSIAdapter"] = "contrôleur de stockage (NVMe, RAID, SCSI) : le disque système peut en dépendre",
        ["Volume"] = "volume de disque",
        ["VolumeSnapshot"] = "clichés instantanés de volume",
        ["Processor"] = "processeur",
        ["Computer"] = "ordinateur (couche matérielle de Windows)",
        ["System"] = "périphérique système (bus, chipset, ACPI) : peut bloquer le poste",
        ["Display"] = "carte graphique : l'écran peut s'éteindre ou clignoter pendant l'opération",
        ["Net"] = "carte réseau : coupe le réseau, et une session à distance (RDP, prise de main) en cours",
        ["SecurityDevices"] = "puce de sécurité (TPM) : BitLocker et Windows Hello en dépendent",
        ["Firmware"] = "firmware du poste",
    };

    /// <summary>Plans de réparation de tous les périphériques présents en erreur ou désactivés.</summary>
    public static List<RepairPlan> PlanAll(IReadOnlyList<DeviceInfo> devices) =>
        devices.Where(d => d.IsPresent && (d.HasProblem || d.Health == DeviceHealth.Disabled)).Select(d => Plan(d, devices)).ToList();

    /// <summary>Diagnostic et actions adaptées au code d'erreur. <paramref name="allDevices"/> sert à repérer le seul clavier / la seule souris.</summary>
    public static RepairPlan Plan(DeviceInfo device, IReadOnlyList<DeviceInfo>? allDevices = null)
    {
        var critical = CriticalReason(device, allDevices);
        var restart = new RepairAction(RepairActionKind.Restart, "Redémarrer le périphérique",
            "Arrête puis relance le périphérique et son pilote, comme un débranchement / rebranchement logiciel. Sans risque pour les données.");
        var enable = new RepairAction(RepairActionKind.Enable, "Réactiver le périphérique",
            "Le périphérique a été désactivé (Gestionnaire de périphériques, script ou stratégie) : le réactiver le remet en service.");
        var reinstall = new RepairAction(RepairActionKind.Reinstall, "Réinstaller le pilote",
            "Retire le périphérique puis le fait redétecter : Windows réinstalle le meilleur pilote présent dans le magasin. Le pilote n'est pas supprimé du poste.");
        var rescan = new RepairAction(RepairActionKind.Rescan, "Rechercher les modifications matérielles",
            "Demande à Windows de redétecter le matériel et d'associer un pilote déjà présent sur le poste.");

        if (device.IsHardwareFault)
        {
            return new RepairPlan
            {
                Device = device,
                IsHardwareFault = true,
                CriticalReason = critical,
                Diagnosis = $"{device.StatusText} (code {device.ErrorCode}) : le périphérique ne répond pas correctement au port USB " +
                            "(échec de la lecture de son descripteur). C'est une panne matérielle ou électrique : aucun pilote ni aucune action logicielle ne peut la corriger.",
                Advice =
                {
                    "Débranchez le périphérique, attendez 10 secondes et rebranchez-le sur un autre port USB (de préférence directement sur le PC, sans concentrateur ni rallonge).",
                    "Essayez un autre câble ; pour un périphérique alimenté (disque, station d'accueil), vérifiez son alimentation.",
                    "Testez le périphérique sur un autre poste : s'il y échoue aussi, il est défectueux.",
                    "Si plusieurs ports échouent : arrêtez complètement le PC (pas seulement redémarrer), débranchez-le une minute, puis mettez à jour le BIOS et le pilote du chipset.",
                },
            };
        }

        var advice = new List<string>();
        var actions = new List<RepairAction>();
        string diagnosis;
        string? command = null;
        switch (device.ErrorCode)
        {
            case 0:
                diagnosis = "Le périphérique fonctionne correctement : aucune réparation nécessaire.";
                actions.Add(restart);
                break;
            case 10:
                diagnosis = "Le périphérique ne peut pas démarrer (code 10) : le pilote a échoué à l'initialiser, souvent après une mise en veille ou un changement de pilote.";
                actions.AddRange(new[] { restart, reinstall });
                advice.Add("Si le problème persiste, installez le pilote du constructeur : spokapilote install --only \"" + device.Name + "\".");
                break;
            case 43:
                diagnosis = "Windows a arrêté le périphérique car il a signalé un problème (code 43). Un redémarrage du périphérique suffit souvent ; sinon, le pilote ou le matériel est en cause.";
                actions.AddRange(new[] { restart, reinstall });
                advice.Add("Pour un périphérique USB : rebranchez-le sur un autre port. Pour une carte graphique : mettez à jour son pilote.");
                break;
            case 22:
                diagnosis = "Le périphérique est désactivé (code 22).";
                actions.Add(enable);
                advice.Add("Vérifiez qu'il n'a pas été désactivé volontairement (stratégie de l'entreprise, poste en libre-service...).");
                break;
            case 28:
                diagnosis = "Aucun pilote n'est installé (code 28).";
                actions.Add(rescan);
                command = "spokapilote install --missing";
                advice.Add("Cherchez et installez le pilote depuis les sources officielles : spokapilote install --missing (ou l'onglet Pilotes de l'application).");
                break;
            case 1 or 18 or 19 or 24:
                diagnosis = device.ErrorCode switch
                {
                    1 => "Le périphérique est mal configuré (code 1) : pilote absent ou incorrect.",
                    18 => "Le pilote doit être réinstallé (code 18).",
                    19 => "La configuration du périphérique dans le registre est incomplète ou endommagée (code 19).",
                    _ => "Le périphérique est absent, mal installé ou ne fonctionne pas correctement (code 24).",
                };
                actions.Add(device.ErrorCode == 24 ? rescan : reinstall);
                if (device.ErrorCode == 24) actions.Add(reinstall);
                command = "spokapilote install --missing";
                advice.Add("Si la réinstallation ne suffit pas : spokapilote install --missing.");
                break;
            case 31 or 39 or 52 or 3 or 37 or 32:
                diagnosis = device.ErrorCode switch
                {
                    31 => "Le pilote n'a pas pu être chargé (code 31).",
                    39 => "Le pilote est corrompu ou un de ses fichiers manque (code 39).",
                    52 => "La signature numérique du pilote est invalide (code 52) : Windows refuse de le charger.",
                    3 => "Le pilote est endommagé ou la mémoire est insuffisante (code 3).",
                    37 => "Le pilote a échoué à s'initialiser (code 37).",
                    _ => "Le service du pilote est désactivé (code 32).",
                };
                actions.Add(reinstall);
                if (device.ErrorCode is 3 or 37) actions.Add(restart);
                command = device.ErrorCode == 52 ? "spokapilote install --only \"" + device.Name + "\"" : null;
                advice.Add(device.ErrorCode == 52
                    ? "Une réinstallation remet le même pilote : remplacez-le par un pilote signé d'une source officielle (spokapilote install)."
                    : "Si le problème persiste, installez une autre version du pilote : spokapilote install --only \"" + device.Name + "\".");
                break;
            case 12:
                diagnosis = "Ressources insuffisantes (code 12) : conflit de ressources matérielles.";
                advice.Add("Mettez à jour le BIOS et le pilote du chipset ; retirez les cartes d'extension inutiles.");
                break;
            case 14 or 21:
                diagnosis = device.ErrorCode == 14
                    ? "Le périphérique ne fonctionnera qu'après un redémarrage de l'ordinateur (code 14)."
                    : "Windows est en train de retirer le périphérique (code 21).";
                advice.Add("Redémarrez l'ordinateur.");
                break;
            case 29:
                diagnosis = "Le périphérique est désactivé par le firmware (code 29).";
                advice.Add("Activez-le dans le BIOS / UEFI du poste.");
                break;
            case 45:
                diagnosis = "Le périphérique n'est pas connecté (code 45).";
                advice.Add("Rebranchez-le ; s'il n'existe plus, cette entrée peut être ignorée.");
                break;
            case 48:
                diagnosis = "Le pilote est bloqué par Windows car il pose des problèmes connus (code 48).";
                command = "spokapilote install --only \"" + device.Name + "\"";
                advice.Add("Installez une version plus récente et compatible du pilote : spokapilote install --only \"" + device.Name + "\".");
                break;
            default:
                diagnosis = $"{device.StatusText} (code {device.ErrorCode}).";
                actions.AddRange(new[] { restart, reinstall });
                break;
        }

        // Réinstallation : seulement pour un matériel redétectable par son bus (pas un périphérique logiciel ou racine).
        if (!device.IsHardware) actions.RemoveAll(a => a.Kind == RepairActionKind.Reinstall);
        if (device.Hint is { } hint && !advice.Contains(hint)) advice.Add(hint);
        if (critical is not null) advice.Insert(0, $"Périphérique critique ({critical}) : une confirmation explicite est demandée avant toute action.");

        return new RepairPlan
        {
            Device = device,
            Diagnosis = diagnosis,
            Advice = advice,
            Actions = actions,
            CriticalReason = critical,
            SuggestedCommand = command,
        };
    }

    /// <summary>Motif de prudence si le périphérique est critique, sinon null.</summary>
    public static string? CriticalReason(DeviceInfo device, IReadOnlyList<DeviceInfo>? allDevices = null)
    {
        if (device.ClassName is { } cls && CriticalClasses.TryGetValue(cls, out var reason)) return reason;

        var ids = device.HardwareIds.Concat(device.CompatibleIds).ToList();
        if (string.Equals(device.ClassName, "USB", StringComparison.OrdinalIgnoreCase) &&
            (device.Enumerator == "PCI" || ids.Any(i => i.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))))
            return "contrôleur ou concentrateur USB racine : tous les périphériques USB (clavier, souris compris) seraient déconnectés";

        if (device.ClassName is "Keyboard" or "Mouse" && allDevices is not null)
        {
            var others = allDevices.Count(d => d.IsPresent && d.ErrorCode == 0 && !string.Equals(d.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                                               string.Equals(d.ClassName, device.ClassName, StringComparison.OrdinalIgnoreCase));
            if (others == 0)
                return device.ClassName == "Keyboard"
                    ? "seul clavier en état de marche : vous pourriez perdre la main sur le poste"
                    : "seule souris en état de marche : vous pourriez perdre la main sur le poste";
        }
        return null;
    }

    /// <summary>Commandes pnputil correspondant à une action.</summary>
    public static List<string> CommandsFor(RepairActionKind kind, string instanceId) => kind switch
    {
        RepairActionKind.Restart => new() { $"pnputil /restart-device \"{instanceId}\"" },
        RepairActionKind.Enable => new() { $"pnputil /enable-device \"{instanceId}\"" },
        RepairActionKind.Reinstall => new() { $"pnputil /remove-device \"{instanceId}\"", "pnputil /scan-devices" },
        _ => new() { "pnputil /scan-devices" },
    };

    /// <summary>
    /// Exécute une action du plan. Refuse (sans rien exécuter) : une action non proposée (panne matérielle comprise),
    /// un périphérique critique sans <see cref="RepairOptions.ConfirmCritical"/>, une console non administrateur ou
    /// un Windows trop ancien. Après l'action, l'état du périphérique est relu (WMI) jusqu'à
    /// <see cref="RepairOptions.VerifyTimeout"/>.
    /// </summary>
    public static async Task<RepairResult> ExecuteAsync(RepairPlan plan, RepairActionKind? action = null, RepairOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new RepairOptions();
        var device = plan.Device;
        var kind = action ?? plan.Recommended?.Kind;
        RepairResult Refuse(string message) => new() { Status = RepairStatus.Refused, Action = kind, ErrorCodeBefore = device.ErrorCode, Message = message };

        if (kind is null)
            return Refuse(plan.IsHardwareFault
                ? "Panne matérielle : aucune action logicielle n'est proposée. Suivez les conseils physiques (câble, port, alimentation)."
                : "Aucune action logicielle n'est proposée pour ce problème : suivez les conseils affichés.");
        if (!plan.Offers(kind.Value))
            return Refuse(plan.IsHardwareFault
                ? "Panne matérielle : aucune action logicielle n'est proposée. Suivez les conseils physiques (câble, port, alimentation)."
                : $"L'action « {kind.Value.ToString().ToLowerInvariant()} » n'est pas adaptée à ce problème (actions proposées : {(plan.Actions.Count == 0 ? "aucune" : string.Join(", ", plan.Actions.Select(a => a.CliName)))}).");
        if (plan.IsCritical && !options.ConfirmCritical)
            return Refuse($"Périphérique critique ({plan.CriticalReason}) : action refusée sans confirmation explicite.");

        var commands = CommandsFor(kind.Value, device.InstanceId);
        if (options.DryRun)
            return new RepairResult
            {
                Status = RepairStatus.Simulated,
                Action = kind,
                ErrorCodeBefore = device.ErrorCode,
                Commands = commands,
                Message = $"Simulation : {string.Join(" puis ", commands)} — rien n'a été exécuté.",
            };
        if (!PnpUtil.SupportsDeviceCommands)
            return Refuse("Cette version de Windows ne propose pas les actions de périphérique de pnputil (Windows 10 2004 ou plus récent requis) : utilisez le Gestionnaire de périphériques.");
        if (!Elevation.IsAdministrator())
            return Refuse("Cette action nécessite une console administrateur (clic droit > Exécuter en tant qu'administrateur).");

        ProcessResult r;
        var reboot = false;
        switch (kind.Value)
        {
            case RepairActionKind.Restart:
                progress?.Report("Redémarrage du périphérique...");
                r = await PnpUtil.RestartDeviceAsync(device.InstanceId, ct).ConfigureAwait(false);
                break;
            case RepairActionKind.Enable:
                progress?.Report("Réactivation du périphérique...");
                r = await PnpUtil.EnableDeviceAsync(device.InstanceId, ct).ConfigureAwait(false);
                break;
            case RepairActionKind.Reinstall:
                progress?.Report("Retrait du périphérique...");
                r = await PnpUtil.RemoveDeviceAsync(device.InstanceId, ct).ConfigureAwait(false);
                if (r.ExitCode is 0 or PnpUtil.ErrorSuccessRebootRequired)
                {
                    reboot = r.ExitCode == PnpUtil.ErrorSuccessRebootRequired;
                    progress?.Report("Redétection du matériel et réinstallation du pilote...");
                    r = await PnpUtil.ScanDevicesAsync(ct).ConfigureAwait(false);
                }
                break;
            default:
                progress?.Report("Recherche des modifications matérielles...");
                r = await PnpUtil.ScanDevicesAsync(ct).ConfigureAwait(false);
                break;
        }
        Log.Debug($"pnputil ({kind}) : code {r.ExitCode} — {r.Combined.Trim()}");
        reboot |= r.ExitCode == PnpUtil.ErrorSuccessRebootRequired;

        RepairResult result;
        if (r.ExitCode is not (0 or PnpUtil.ErrorSuccessRebootRequired))
        {
            result = new RepairResult
            {
                Status = RepairStatus.Failed,
                Action = kind,
                ErrorCodeBefore = device.ErrorCode,
                Commands = commands,
                Message = r.TimedOut ? "pnputil ne répond pas (délai dépassé)." : $"pnputil a échoué (code {r.ExitCode}). Le périphérique est peut-être protégé par Windows ou déjà en cours de modification.",
            };
        }
        else
        {
            progress?.Report("Contrôle de l'état du périphérique...");
            var after = await WaitForStateAsync(device.InstanceId, options.VerifyTimeout, ct).ConfigureAwait(false);
            var resolved = after is { ErrorCode: 0, IsPresent: true };
            result = new RepairResult
            {
                Status = resolved ? RepairStatus.Resolved : RepairStatus.NotResolved,
                Action = kind,
                ErrorCodeBefore = device.ErrorCode,
                ErrorCodeAfter = after?.ErrorCode,
                PresentAfter = after?.IsPresent,
                RebootRequired = reboot,
                Commands = commands,
                Message = resolved
                    ? $"Le périphérique fonctionne correctement{(reboot ? " (redémarrage du poste conseillé pour finaliser)" : "")}."
                    : after is null
                        ? "Le périphérique n'a pas été redétecté : rebranchez-le ou redémarrez le poste."
                        : reboot
                            ? $"Redémarrage du poste nécessaire pour finaliser (état actuel : {after.StatusText})."
                            : $"Le problème persiste : {after.StatusText} (code {after.ErrorCode}). Essayez l'action suivante proposée ou les conseils affichés.",
            };
        }

        if (options.RecordHistory) Record(plan, result);
        Log.Info($"Réparation de « {device.Name} » ({kind}) : {result.Message}");
        return result;
    }

    /// <summary>Relit le périphérique jusqu'à ce qu'il fonctionne ou que le délai expire ; renvoie le dernier état lu.</summary>
    private static async Task<DeviceInfo?> WaitForStateAsync(string instanceId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        DeviceInfo? last = null;
        while (true)
        {
            try { last = DeviceInventory.Find(instanceId); }
            catch (Exception ex) { Log.Debug($"Relecture de {instanceId} impossible : {ex.Message}"); }
            if (last is { ErrorCode: 0 } || DateTime.UtcNow >= deadline) return last;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    private static void Record(RepairPlan plan, RepairResult result)
    {
        InterventionHistory.Save(new Intervention
        {
            Kind = InterventionKinds.DeviceRepair,
            Items =
            {
                new InterventionItem
                {
                    Title = plan.Device.Name,
                    Version = plan.Device.DriverVersion,
                    Source = $"pnputil ({result.Action?.ToString().ToLowerInvariant()}) — {plan.Device.InstanceId}",
                    Status = result.Status.ToString(),
                    Message = result.Message,
                },
            },
        });
    }
}
