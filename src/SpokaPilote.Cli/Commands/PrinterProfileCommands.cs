using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Cli;

/// <summary>Profils d'imprimantes : printers export-profile | deploy | remove.</summary>
internal static partial class Program
{
    private static Task<int> PrinterProfileAsync(string sub, Args a, Settings settings, CancellationToken ct) => sub switch
    {
        "export-profile" => Task.FromResult(ExportPrinterProfile(a)),
        "deploy" => DeployPrinterProfileAsync(a, settings, ct),
        "remove" => RemovePrinterAsync(a, ct),
        _ => Task.FromResult(Help()),
    };

    private static int ExportPrinterProfile(Args a)
    {
        var target = a.Positional(1);
        if (target is null)
        {
            WriteLine("Usage : spokapilote printers export-profile <profil.json> [--yes]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        var path = Path.GetFullPath(target);
        if (File.Exists(path) && !a.Flag("yes") && !Confirm($"{path} existe déjà. Le remplacer ?")) return ExitOk;

        var (profile, skipped) = PrinterProfileDeployer.Export(PrinterInventory.Read(), Environment.MachineName);
        WriteLine($"\nImprimantes exportées ({profile.Printers.Count}) :", ConsoleColor.White);
        foreach (var p in profile.Printers)
        {
            var target2 = p.IsConnection ? $"connexion partagée {p.Connection}" : PrinterDeployPlanner.DescribeTarget(p);
            WriteLine($"  ✓ {p.EffectiveName,-35} {target2}{(p.DriverName is null ? "" : $" · pilote « {p.DriverName} »")}{(p.SetDefault ? " · par défaut" : "")}", ConsoleColor.Green);
        }
        foreach (var (name, reason) in skipped) WriteLine($"  · {name} — ignorée : {reason}", ConsoleColor.DarkGray);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, profile.ToJson());
        WriteLine($"\nProfil enregistré : {path}", ConsoleColor.Cyan);
        if (profile.Printers.Count == 0) WriteLine("Aucune imprimante réseau sur ce poste : le profil est vide.", ConsoleColor.Yellow);
        else WriteLine("Ajoutez des conditions (« when » : subnets, computerNames) ou des groupes, puis : spokapilote printers deploy <profil.json>", ConsoleColor.Gray);
        return ExitOk;
    }

    private static async Task<int> DeployPrinterProfileAsync(Args a, Settings settings, CancellationToken ct)
    {
        var source = a.Positional(1);
        var dryRun = a.Flag("dry-run");
        var (profile, origin) = await PrinterProfileSource.LoadAsync(source, settings, ct);
        if (profile is null)
        {
            WriteLine($"Aucun profil d'imprimantes publié pour ce poste ({origin}) : rien à faire.", ConsoleColor.Green);
            return ExitOk;
        }

        var context = PrinterProfileContext.Current();
        WriteLine($"Profil    : {profile.Name ?? "(sans nom)"} — {origin}", ConsoleColor.White);
        WriteLine($"Poste     : {context}", ConsoleColor.White);

        var validation = profile.Validate();
        foreach (var w in validation.Warnings) WriteLine($"  ⚠ {w}", ConsoleColor.Yellow);
        if (!validation.IsValid)
        {
            WriteLine("Profil invalide, aucun changement effectué :", ConsoleColor.Red);
            foreach (var e in validation.Errors) WriteLine($"  ✗ {e}", ConsoleColor.Red);
            return ExitError;
        }

        var applicable = profile.ApplicableEntries(context);
        WriteLine($"\n{applicable.Count} imprimante(s) applicable(s) à ce poste sur {profile.AllEntries().Count()}{(profile.RemoveUnlisted ? " · retrait des imprimantes réseau non listées ACTIVÉ" : "")} :",
            profile.RemoveUnlisted ? ConsoleColor.Yellow : ConsoleColor.White);
        foreach (var e in applicable) WriteLine($"  • {e.EffectiveName,-35} {PrinterDeployPlanner.DescribeTarget(e)}", ConsoleColor.Gray);
        if (applicable.Count == 0 && !profile.RemoveUnlisted) return ExitOk;

        if (!dryRun)
        {
            if (!Elevation.IsAdministrator())
            {
                WriteLine("Le déploiement d'imprimantes nécessite une console administrateur (ou --dry-run pour simuler).", ConsoleColor.Red);
                return ExitError;
            }
            if (!a.Flag("yes") && !Confirm("Appliquer ce profil ?")) return ExitOk;
        }

        var report = await new PrinterProfileDeployer(settings).DeployAsync(profile, context, dryRun,
            new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        foreach (var w in report.Warnings.Except(validation.Warnings)) WriteLine($"  ⚠ {w}", ConsoleColor.Yellow);

        WriteLine($"\nRésultat{(dryRun ? " (SIMULATION, aucun changement)" : "")} :", ConsoleColor.White);
        foreach (var item in report.Items)
        {
            var (symbol, color) = item.Status switch
            {
                PrinterDeployStatus.Created or PrinterDeployStatus.Updated => ("✓", dryRun ? ConsoleColor.Cyan : ConsoleColor.Green),
                PrinterDeployStatus.UpToDate => ("=", ConsoleColor.Gray),
                PrinterDeployStatus.Removed => ("−", ConsoleColor.Yellow),
                PrinterDeployStatus.Skipped => ("·", ConsoleColor.DarkGray),
                _ => ("✗", ConsoleColor.Red),
            };
            WriteLine($"  {symbol} {item.Name} — {item.StatusLabel}{(item.Message.Length > 0 ? " : " + item.Message : "")}", color);
            if (item.Status != PrinterDeployStatus.UpToDate)
                foreach (var c in item.Changes) WriteLine($"      {c}", ConsoleColor.DarkGray);
        }
        var counts = report.Items.GroupBy(i => i.StatusLabel).Select(g => $"{g.Count()} {g.Key}");
        WriteLine($"\n{string.Join(" · ", counts)}{(report.NotApplicable > 0 ? $" · {report.NotApplicable} non applicable(s) à ce poste" : "")}", ConsoleColor.White);
        return report.AllFailed ? ExitError : report.HasFailures ? ExitPartial : ExitOk;
    }

    private static async Task<int> RemovePrinterAsync(Args a, CancellationToken ct)
    {
        var name = a.Positional(1);
        if (name is null)
        {
            WriteLine("Usage : spokapilote printers remove <nom> [--yes]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        if (!Elevation.IsAdministrator())
        {
            WriteLine("Le retrait d'une imprimante nécessite une console administrateur.", ConsoleColor.Red);
            return ExitError;
        }
        var printer = PrinterInventory.Read().Find(name);
        if (printer is null)
        {
            WriteLine($"Imprimante « {name} » introuvable sur ce poste.", ConsoleColor.Red);
            return ExitError;
        }
        WriteLine($"Imprimante : {printer.Name} — {PrinterDeployPlanner.DescribeExisting(printer)}, pilote « {printer.DriverName} »", ConsoleColor.White);
        if (!a.Flag("yes") && !Confirm("Retirer cette imprimante (file d'impression et port s'il ne sert plus) ?")) return ExitOk;
        var (ok, message) = await PrinterProfileDeployer.RemoveAsync(printer.Name, ct);
        WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitError;
    }
}
