using SpokaPilote.Core.Health;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;
using SpokaPilote.Core.Repair;

namespace SpokaPilote.Cli;

/// <summary>Commandes « spokapilote device ... » : liste, fiche et réparation guidée d'un périphérique.</summary>
internal static partial class Program
{
    private static async Task<int> DeviceAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0)?.ToLowerInvariant() ?? "list";
        switch (sub)
        {
            case "list" or "liste": return DeviceList(a);
            case "info" or "fiche": return await DeviceInfoAsync(a, ct);
            case "repair" or "reparer" or "réparer": return await DeviceRepairAsync(a, ct);
            default: return DeviceUsage();
        }
    }

    private static int DeviceUsage()
    {
        WriteLine("Usage : spokapilote device list [--problems] [--all]", ConsoleColor.Yellow);
        WriteLine("        spokapilote device info <n°|id>", ConsoleColor.Yellow);
        WriteLine("        spokapilote device repair <n°|id> [--action restart|enable|reinstall|rescan] [--dry-run] [--yes] [--confirm-critical]", ConsoleColor.Yellow);
        WriteLine("Le n° est celui affiché par « device list » (les périphériques en erreur sont numérotés en premier).", ConsoleColor.Gray);
        return ExitUsage;
    }

    /// <summary>Périphériques présents, numérotés dans un ordre stable (en erreur d'abord, puis par classe et nom).</summary>
    private static List<DeviceInfo> PresentDevices() => DeviceInventory.Collect().Where(d => d.IsPresent).ToList();

    private static (DeviceInfo? Device, int Number) ResolveDevice(List<DeviceInfo> devices, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, 0);
        if (int.TryParse(text, out var n))
            return n >= 1 && n <= devices.Count ? (devices[n - 1], n) : (null, 0);
        var exact = devices.FindIndex(d => d.InstanceId.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return (devices[exact], exact + 1);
        var matches = devices.Select((d, i) => (d, i))
            .Where(x => x.d.InstanceId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                        x.d.HardwareIds.Any(h => h.Equals(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 1) return (matches[0].d, matches[0].i + 1);
        if (matches.Count > 1)
        {
            WriteLine($"« {text} » désigne {matches.Count} périphériques : précisez le n° ou l'identifiant complet.", ConsoleColor.Yellow);
            foreach (var (d, i) in matches.Take(10)) WriteLine($"  {i + 1,4}. {d.Name}  [{d.InstanceId}]", ConsoleColor.Gray);
        }
        else WriteLine($"Périphérique introuvable : {text} (voir « spokapilote device list »).", ConsoleColor.Red);
        return (null, 0);
    }

    private static string StateSymbol(DeviceInfo d) => d.IsHardwareFault ? "⚡" : d.HasProblem ? "✗" : d.Health == DeviceHealth.Disabled ? "○" : "✓";

    private static ConsoleColor StateColor(DeviceInfo d) =>
        d.HasProblem ? ConsoleColor.Yellow : d.Health == DeviceHealth.Disabled ? ConsoleColor.DarkGray : ConsoleColor.Gray;

    // ------------------------------------------------------------------ list

    private static int DeviceList(Args a)
    {
        var devices = PresentDevices();
        var problemsOnly = a.Flag("problems") || a.Flag("problemes");
        var shown = devices.Select((d, i) => (d, n: i + 1))
            .Where(x => problemsOnly ? x.d.HasProblem || x.d.Health == DeviceHealth.Disabled
                                     : a.Flag("all") || x.d.IsHardware || x.d.HasProblem || x.d.Health == DeviceHealth.Disabled)
            .ToList();

        WriteLine($"{devices.Count} périphérique(s) présent(s), {devices.Count(d => d.HasProblem)} en erreur, {devices.Count(d => d.Health == DeviceHealth.Disabled)} désactivé(s).", ConsoleColor.White);
        if (shown.Count == 0)
        {
            WriteLine(problemsOnly ? "✓ Aucun périphérique en erreur ou désactivé." : "Aucun périphérique.", ConsoleColor.Green);
            return ExitOk;
        }
        foreach (var (d, n) in shown)
        {
            WriteLine($"{n,4}. {StateSymbol(d)} {d.Name}  [{d.ClassName ?? "?"}]{(d.DriverVersion is null ? "" : $"  pilote {d.DriverVersion}")}", StateColor(d));
            if (d.HasProblem || d.Health == DeviceHealth.Disabled)
            {
                WriteLine($"        {d.StatusText} (code {d.ErrorCode}) — {d.InstanceId}", ConsoleColor.DarkGray);
                var plan = DeviceRepair.Plan(d, devices);
                WriteLine(plan.IsHardwareFault
                        ? "        → panne matérielle : conseils physiques seulement (spokapilote device info " + n + ")"
                        : plan.Recommended is { } r
                            ? $"        → {r.Label} : spokapilote device repair {n}{(plan.IsCritical ? "   (périphérique critique : confirmation demandée)" : "")}"
                            : "        → aucune action logicielle : voir spokapilote device info " + n,
                    ConsoleColor.DarkCyan);
            }
        }
        if (!problemsOnly && !a.Flag("all")) WriteLine("\n(périphériques logiciels masqués : --all pour tout afficher, --problems pour les seuls problèmes)", ConsoleColor.DarkGray);
        return ExitOk;
    }

    // ------------------------------------------------------------------ info

    private static async Task<int> DeviceInfoAsync(Args a, CancellationToken ct)
    {
        var devices = PresentDevices();
        var (device, number) = ResolveDevice(devices, a.Positional(1));
        if (device is null)
        {
            return a.Positional(1) is null ? DeviceUsage() : ExitError;
        }

        var originals = DriverStoreCleaner.ReadOriginalInfNames();
        var original = device.InfName is not null && originals.TryGetValue(device.InfName, out var o) ? o : null;
        WriteLine($"n°{number} — {device.Name}", ConsoleColor.Cyan);
        WriteLine($"  État          : {StateSymbol(device)} {device.StatusText} (code {device.ErrorCode})", device.HasProblem ? ConsoleColor.Yellow : ConsoleColor.Green);
        WriteLine($"  Classe        : {device.ClassName ?? "?"} {device.ClassGuid}", ConsoleColor.Gray);
        WriteLine($"  Fabricant     : {device.Manufacturer ?? "?"}", ConsoleColor.Gray);
        WriteLine($"  Identifiant   : {device.InstanceId}", ConsoleColor.Gray);
        foreach (var (label, ids) in new[] { ("ID matériels  ", device.HardwareIds), ("ID compatibles", device.CompatibleIds) })
            for (var i = 0; i < ids.Count; i++)
                WriteLine($"  {(i == 0 ? label : new string(' ', label.Length))}: {ids[i]}", ConsoleColor.DarkGray);
        if (device.DriverVersion is not null)
        {
            WriteLine($"  Pilote        : {device.DriverVersion} du {device.DriverDate:dd/MM/yyyy} — {device.DriverProvider ?? "?"}", ConsoleColor.Gray);
            WriteLine($"  INF           : {device.InfName ?? "?"}{(original is null ? "" : $" (d'origine : {original})")} · " +
                      $"{(DriverHealth.IsThirdPartyDriver(device) ? "pilote tiers" : "pilote intégré à Windows")} · " +
                      $"{(device.IsSigned switch { true => "signé", false => "NON SIGNÉ", _ => "signature inconnue" })}", ConsoleColor.Gray);
        }
        else WriteLine("  Pilote        : aucun", ConsoleColor.Yellow);

        // Constats de santé propres à ce périphérique (dont pilote dangereux installé).
        try
        {
            var policy = await new PolicyLoader(LoadSettings(a)) { RefreshReferenceList = false }.LoadAsync(ct);
            var health = DriverHealth.Compute(new[] { device }, Array.Empty<DriverCandidate>(), policy.Effective,
                new HealthOptions { OriginalInfNames = originals, Interventions = InterventionHistory.List() });
            foreach (var f in health.Findings.Where(f => f.Code != HealthCodes.UpdateAvailable))
            {
                WriteLine($"  Santé         : [{f.SeverityLabel}] {f.Title}", f.Severity == HealthSeverity.Critical ? ConsoleColor.Red : ConsoleColor.Yellow);
                WriteLine($"                  {f.Detail}", ConsoleColor.DarkGray);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"Santé du périphérique non évaluée : {ex.Message}");
        }

        PrintRepairPlan(DeviceRepair.Plan(device, devices), number);
        return ExitOk;
    }

    private static void PrintRepairPlan(RepairPlan plan, int number)
    {
        WriteLine("\nDiagnostic", ConsoleColor.White);
        WriteLine($"  {plan.Diagnosis}", ConsoleColor.Gray);
        if (plan.IsCritical) WriteLine($"  ⚠ Périphérique critique : {plan.CriticalReason}.", ConsoleColor.Yellow);
        if (plan.Advice.Count > 0)
        {
            WriteLine(plan.IsHardwareFault ? "Conseils (panne matérielle : aucune action logicielle)" : "Conseils", ConsoleColor.White);
            foreach (var adv in plan.Advice) WriteLine($"  • {adv}", ConsoleColor.Gray);
        }
        if (plan.Actions.Count > 0)
        {
            WriteLine("Actions possibles", ConsoleColor.White);
            foreach (var act in plan.Actions)
            {
                WriteLine($"  {(act == plan.Recommended ? "→" : " ")} {act.Label} (--action {act.CliName}){(act == plan.Recommended ? " — recommandée" : "")}", act == plan.Recommended ? ConsoleColor.Cyan : ConsoleColor.Gray);
                WriteLine($"      {act.Explanation}", ConsoleColor.DarkGray);
            }
            WriteLine($"  spokapilote device repair {number} [--action ...] [--dry-run]", ConsoleColor.DarkCyan);
        }
        else if (!plan.IsHardwareFault)
        {
            WriteLine("Aucune action logicielle proposée pour ce problème.", ConsoleColor.Gray);
        }
        if (plan.SuggestedCommand is not null) WriteLine($"  Voir aussi : {plan.SuggestedCommand}", ConsoleColor.DarkCyan);
    }

    // ------------------------------------------------------------------ repair

    private static async Task<int> DeviceRepairAsync(Args a, CancellationToken ct)
    {
        Banner();
        var devices = PresentDevices();
        var (device, number) = ResolveDevice(devices, a.Positional(1));
        if (device is null)
        {
            return a.Positional(1) is null ? DeviceUsage() : ExitError;
        }

        RepairActionKind? action = null;
        if (a.Value("action") is { } name)
        {
            if (!Enum.TryParse<RepairActionKind>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                WriteLine($"Action inconnue : {name} (valeurs : restart, enable, reinstall, rescan).", ConsoleColor.Red);
                return ExitUsage;
            }
            action = parsed;
        }

        var plan = DeviceRepair.Plan(device, devices);
        WriteLine($"n°{number} — {device.Name}  [{device.InstanceId}]", ConsoleColor.Cyan);
        WriteLine($"État : {device.StatusText} (code {device.ErrorCode})", device.HasProblem ? ConsoleColor.Yellow : ConsoleColor.Green);
        PrintRepairPlan(plan, number);

        var dryRun = a.Flag("dry-run");
        var chosen = action ?? plan.Recommended?.Kind;
        if (chosen is null && action is null)
        {
            WriteLine(plan.IsHardwareFault
                ? "\nPanne matérielle : rien à exécuter côté logiciel. Appliquez les conseils ci-dessus."
                : "\nRien à exécuter : appliquez les conseils ci-dessus.", ConsoleColor.Yellow);
            return ExitOk;
        }

        var confirmCritical = false;
        if (!dryRun && chosen is not null && plan.Offers(chosen.Value))
        {
            if (!Elevation.IsAdministrator())
            {
                WriteLine("\nCette action nécessite une console administrateur (clic droit > Exécuter en tant qu'administrateur). Essayez d'abord --dry-run.", ConsoleColor.Red);
                return ExitError;
            }
            var label = plan.Actions.First(x => x.Kind == chosen).Label;
            if (plan.IsCritical)
            {
                // « --yes » ne suffit jamais pour un périphérique critique.
                confirmCritical = a.Flag("confirm-critical") ||
                                  Confirm($"\n⚠ Périphérique CRITIQUE ({plan.CriticalReason}). {label} quand même ?");
                if (!confirmCritical)
                {
                    WriteLine("Action annulée (périphérique critique : confirmez de façon interactive ou avec --confirm-critical).", ConsoleColor.Yellow);
                    return ExitOk;
                }
            }
            else if (!a.Flag("yes") && !Confirm($"\n{label} « {device.Name} » ?"))
            {
                return ExitOk;
            }
        }

        var result = await DeviceRepair.ExecuteAsync(plan, chosen, new RepairOptions { DryRun = dryRun, ConfirmCritical = confirmCritical || dryRun },
            new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        var color = result.Status switch
        {
            RepairStatus.Resolved => ConsoleColor.Green,
            RepairStatus.Simulated => ConsoleColor.Cyan,
            RepairStatus.NotResolved => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
        WriteLine($"\n{result.Message}", color);
        if (result.Status == RepairStatus.Simulated && plan.IsCritical)
            WriteLine("(En réel, une confirmation explicite serait exigée : périphérique critique.)", ConsoleColor.Yellow);
        if (result.Executed)
        {
            WriteLine($"État avant : code {result.ErrorCodeBefore} · après : {(result.ErrorCodeAfter is { } after ? $"code {after}" : "périphérique non redétecté")}", ConsoleColor.Gray);
            WriteLine("Intervention enregistrée dans l'historique (spokapilote history).", ConsoleColor.DarkGray);
        }
        return result.Status switch
        {
            RepairStatus.Resolved or RepairStatus.Simulated => result.RebootRequired ? ExitRebootRequired : ExitOk,
            RepairStatus.NotResolved => result.RebootRequired ? ExitRebootRequired : ExitPartial,
            _ => ExitError,
        };
    }
}
