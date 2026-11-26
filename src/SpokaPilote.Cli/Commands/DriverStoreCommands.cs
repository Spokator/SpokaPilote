using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Cli;

/// <summary>Commandes « spokapilote driverstore ... » : magasin de pilotes (liste, nettoyage des anciennes versions).</summary>
internal static partial class Program
{
    private static async Task<int> DriverStoreAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0)?.ToLowerInvariant() ?? "list";
        switch (sub)
        {
            case "list" or "liste": return DriverStoreList(a);
            case "clean" or "nettoyer": return await DriverStoreCleanAsync(a, ct);
            default:
                WriteLine("Usage : spokapilote driverstore list [--all]", ConsoleColor.Yellow);
                WriteLine("        spokapilote driverstore clean [--dry-run] [--yes] [--backup]", ConsoleColor.Yellow);
                WriteLine("Sans --yes, « clean » affiche l'aperçu (paquets supprimables et espace libérable) puis demande confirmation.", ConsoleColor.Gray);
                return ExitUsage;
        }
    }

    private static DriverStoreAnalysis AnalyzeDriverStore()
    {
        Banner();
        var analysis = DriverStoreCleaner.AnalyzeThisPc(new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.Gray)));
        if (!analysis.ExtensionUsageVerified)
            WriteLine("  ⚠ pnputil /enum-drivers /devices indisponible : les INF d'extension sont conservés par prudence.", ConsoleColor.Yellow);
        return analysis;
    }

    private static (string Label, ConsoleColor Color) VerdictLabel(DriverStoreVerdict v) => v switch
    {
        DriverStoreVerdict.Candidate => ("supprimable", ConsoleColor.Cyan),
        DriverStoreVerdict.InUse => ("utilisé", ConsoleColor.Gray),
        DriverStoreVerdict.OnlyVersion => ("seule version", ConsoleColor.Gray),
        DriverStoreVerdict.Newest => ("plus récent", ConsoleColor.Gray),
        DriverStoreVerdict.KeptForRollback => ("conservé (retour arrière)", ConsoleColor.DarkYellow),
        DriverStoreVerdict.SensitiveClass => ("conservé (prudence)", ConsoleColor.DarkYellow),
        DriverStoreVerdict.NotReplaced => ("conservé (non remplacé)", ConsoleColor.DarkYellow),
        _ => ("conservé (indéterminé)", ConsoleColor.DarkYellow),
    };

    private static int DriverStoreList(Args a)
    {
        var analysis = AnalyzeDriverStore();
        WriteLine($"\nMagasin de pilotes — paquets tiers ({analysis.Entries.Count}) :", ConsoleColor.White);
        WriteLine($"  {"Paquet",-11} {"INF d'origine",-30} {"Fournisseur",-22} {"Classe",-16} {"Version",-18} {"Date",-10} {"Taille",9}  État", ConsoleColor.DarkGray);
        foreach (var e in analysis.Entries)
        {
            var p = e.Package;
            var (label, color) = VerdictLabel(e.Verdict);
            WriteLine($"  {p.PublishedName,-11} {Cut(p.OriginalName ?? "?", 30),-30} {Cut(p.Provider ?? "?", 22),-22} {Cut(p.ClassName ?? "?", 16),-16} {Cut(p.Version ?? "?", 18),-18} {p.Date?.ToString("dd/MM/yyyy") ?? "?",-10} {p.SizeLabel,9}  {label}", color);
            var showReason = a.Flag("all") || e.Verdict is not (DriverStoreVerdict.InUse or DriverStoreVerdict.OnlyVersion or DriverStoreVerdict.Newest);
            if (showReason) WriteLine($"{"",14}{e.Reason}", ConsoleColor.DarkGray);
        }
        WriteLine($"\n{analysis.Summary}.", analysis.Candidates.Count > 0 ? ConsoleColor.Cyan : ConsoleColor.Green);
        if (analysis.Candidates.Count > 0) WriteLine("Aperçu du nettoyage : spokapilote driverstore clean --dry-run", ConsoleColor.DarkCyan);
        return ExitOk;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static async Task<int> DriverStoreCleanAsync(Args a, CancellationToken ct)
    {
        var analysis = AnalyzeDriverStore();
        var candidates = analysis.Candidates;
        var kept = analysis.Entries.Count(e => e.Verdict is DriverStoreVerdict.KeptForRollback or DriverStoreVerdict.SensitiveClass or DriverStoreVerdict.NotReplaced or DriverStoreVerdict.Undetermined);

        WriteLine($"\n{analysis.Summary}.", ConsoleColor.White);
        if (kept > 0) WriteLine($"  {kept} ancienne(s) version(s) conservée(s) par prudence (détail : spokapilote driverstore list).", ConsoleColor.DarkYellow);
        if (candidates.Count == 0)
        {
            WriteLine("\n✓ Rien à nettoyer : aucune ancienne version inutilisée et remplacée.", ConsoleColor.Green);
            return ExitOk;
        }

        WriteLine($"\nPaquets qui seraient supprimés ({candidates.Count}, {DriverStoreCleaner.FormatSize(analysis.ReclaimableBytes)}) :", ConsoleColor.White);
        foreach (var e in candidates)
        {
            var p = e.Package;
            WriteLine($"  • {p.PublishedName} {p.OriginalName} {p.Version} ({p.Date:dd/MM/yyyy}, {p.Provider}) — {p.SizeLabel}", ConsoleColor.Cyan);
            WriteLine($"      {e.Reason}", ConsoleColor.DarkGray);
        }
        WriteLine("  Suppression par « pnputil /delete-driver oemNN.inf » sans /force : Windows refuse si le paquet est utilisé.", ConsoleColor.DarkGray);
        WriteLine("  Conséquence : ces versions ne pourront plus être restaurées depuis le Gestionnaire de périphériques" +
                  (a.Flag("backup") ? " (copie de sauvegarde réalisée avant suppression)." : " (ajoutez --backup pour en garder une copie)."), ConsoleColor.DarkGray);

        if (a.Flag("dry-run"))
        {
            WriteLine("\nSimulation (--dry-run) : rien n'a été supprimé.", ConsoleColor.Cyan);
            return ExitOk;
        }
        if (!Elevation.IsAdministrator())
        {
            WriteLine("\nAperçu seulement : la suppression nécessite une console administrateur.", ConsoleColor.Yellow);
            return a.Flag("yes") ? ExitError : ExitOk;
        }
        if (!a.Flag("yes") && !Confirm($"\nSupprimer ces {candidates.Count} paquet(s) ?"))
        {
            WriteLine("Aucune suppression (aperçu seulement).", ConsoleColor.Gray);
            return ExitOk;
        }

        var result = await DriverStoreCleaner.CleanAsync(candidates, new DriverStoreCleanOptions { Backup = a.Flag("backup") },
            new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        foreach (var r in result.Removals)
            WriteLine($"  {(r.Success ? "✓" : "✗")} {r.Package.DisplayName} {r.Package.Version} — {r.Message}", r.Success ? ConsoleColor.Green : ConsoleColor.Yellow);
        WriteLine($"\n{result.Summary}", result.Success ? ConsoleColor.Green : ConsoleColor.Yellow);
        if (result.InterventionId is not null) WriteLine($"Intervention {result.InterventionId} enregistrée dans l'historique (spokapilote history).", ConsoleColor.DarkGray);
        if (result.BackupFolder is not null) WriteLine($"Sauvegarde : {result.BackupFolder} (réinstallable : spokapilote restore \"{result.BackupFolder}\")", ConsoleColor.DarkGray);
        return result.Success ? ExitOk : ExitPartial;
    }
}
