using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Cli;

/// <summary>
/// spokapilote image info|inject|cleanup : injection des pilotes d'un dépôt dans une image Windows (install.wim,
/// boot.wim, WinPE) ou dans un Windows hors ligne, via DISM.
/// </summary>
internal static partial class Program
{
    private static async Task<int> ImageAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0);
        if (sub is not ("info" or "inject" or "cleanup")) return Help();
        Banner();
        if (!Elevation.IsAdministrator())
        {
            WriteLine("DISM nécessite une console administrateur (clic droit > Exécuter en tant qu'administrateur).", ConsoleColor.Red);
            return ExitError;
        }

        return sub switch
        {
            "info" => await ImageInfoAsync(a, ct),
            "cleanup" => await ImageCleanupAsync(ct),
            _ => await ImageInjectAsync(a, ct),
        };
    }

    private static async Task<int> ImageInfoAsync(Args a, CancellationToken ct)
    {
        var wim = a.Positional(1) ?? a.Value("wim");
        if (wim is null)
        {
            WriteLine("Usage : spokapilote image info <install.wim> [--index n]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        var indexes = a.Value("index") is { } spec
            ? ParseIndexes(spec, null)
            : (await ImageInjector.GetWimInfoAsync(wim, ct)).Select(i => i.Index).ToList();

        WriteLine($"\n{Path.GetFullPath(wim)} — {indexes.Count} image(s) :", ConsoleColor.White);
        foreach (var index in indexes)
        {
            var info = await ImageInjector.GetWimIndexInfoAsync(wim, index, ct);
            WriteLine($"  [{info.Index}] {info.Name}", ConsoleColor.Cyan);
            if (info.Architecture is null || info.Version is null)
            {
                WriteLine("      architecture et version inconnues : ce n'est pas une image de Windows (injection impossible)", ConsoleColor.Yellow);
                continue;
            }
            WriteLine($"      {info.ArchitectureLabel} · version {info.Version}{(info.Edition is null ? "" : " · édition " + info.Edition)}" +
                      $"{(info.SizeBytes is > 0 ? $" · {info.SizeBytes.Value / 1073741824.0:0.0} Go" : "")}" +
                      $"{(info.Languages.Count > 0 ? " · " + string.Join(", ", info.Languages) : "")}", ConsoleColor.Gray);
        }
        WriteLine($"\nInjecter un dépôt : spokapilote image inject --wim \"{wim}\" --index <n> --repo <dossier>", ConsoleColor.Gray);
        return ExitOk;
    }

    private static async Task<int> ImageCleanupAsync(CancellationToken ct)
    {
        var mounted = await ImageInjector.GetMountedImagesAsync(ct);
        WriteLine(mounted.Trim(), ConsoleColor.Gray);
        var result = await ImageInjector.CleanupMountPointsAsync(ct);
        WriteLine(result.ExitCode == 0 ? "Points de montage orphelins nettoyés." : $"Nettoyage : {ImageInjector.DescribeFailure(result)}",
            result.ExitCode == 0 ? ConsoleColor.Green : ConsoleColor.Red);
        return result.ExitCode == 0 ? ExitOk : ExitError;
    }

    private static async Task<int> ImageInjectAsync(Args a, CancellationToken ct)
    {
        WriteLine("Fonction expérimentale : travaillez sur une COPIE de l'image (install.wim / dossier Windows hors ligne).", ConsoleColor.Yellow);
        var wim = a.Value("wim");
        var offline = a.Value("offline");
        var repos = a.Values("repo").ToList();
        if ((wim is null) == (offline is null) || repos.Count == 0)
        {
            WriteLine("Usage : spokapilote image inject --wim <install.wim> --index <n|1,3|all> --repo <dossier> [--dry-run] [--yes]", ConsoleColor.Yellow);
            WriteLine("        spokapilote image inject --offline D:\\ --repo <dossier> [--dry-run] [--yes]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        var dryRun = a.Flag("dry-run");
        var progress = new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray));

        var reports = new List<InjectionReport>();
        if (offline is not null)
        {
            WriteLine($"Injection des pilotes de {string.Join(", ", repos)} dans le Windows hors ligne {offline}{(dryRun ? " (SIMULATION)" : "")}.", ConsoleColor.Cyan);
            if (!dryRun && !a.Flag("yes") && !Confirm("Continuer ?")) return ExitOk;
            reports.Add(await ImageInjector.InjectIntoOfflineAsync(offline, repos, dryRun, progress, ct));
        }
        else
        {
            var all = await ImageInjector.GetWimInfoAsync(wim!, ct);
            List<int> indexes;
            if (a.Value("index") is { } spec) indexes = ParseIndexes(spec, all.Select(i => i.Index).ToList());
            else if (all.Count == 1) indexes = new List<int> { all[0].Index };
            else
            {
                WriteLine($"{Path.GetFileName(wim)} contient {all.Count} images : précisez --index (ex. --index 6, --index 1,3 ou --index all).", ConsoleColor.Yellow);
                foreach (var i in all) WriteLine($"  [{i.Index}] {i.Name}", ConsoleColor.Gray);
                return ExitUsage;
            }
            WriteLine($"Injection des pilotes de {string.Join(", ", repos)} dans {Path.GetFileName(wim)}, index {string.Join(", ", indexes)}{(dryRun ? " (SIMULATION)" : "")}.", ConsoleColor.Cyan);
            if (!dryRun) WriteLine("L'image est montée, enrichie puis enregistrée ; en cas d'erreur elle reste inchangée. Gardez une copie de l'original.", ConsoleColor.Gray);
            if (!dryRun && !a.Flag("yes") && !Confirm("Continuer ?")) return ExitOk;
            foreach (var index in indexes)
            {
                ct.ThrowIfCancellationRequested();
                reports.Add(await ImageInjector.InjectIntoWimAsync(wim!, index, repos, dryRun, progress, ct));
            }
        }

        foreach (var r in reports) PrintInjectionReport(r, a.Flag("verbose"));
        return reports.All(r => r.Success) ? ExitOk : reports.Any(r => r.Committed) ? ExitPartial : ExitError;
    }

    private static void PrintInjectionReport(InjectionReport r, bool verbose)
    {
        WriteLine($"\n{r.Target} — {r.Architecture ?? "?"} {r.OsVersion}", ConsoleColor.White);
        var refused = r.Refused.ToList();
        WriteLine($"  Contrôle : {r.Accepted.Count()} INF accepté(s), {refused.Count} écarté(s)", ConsoleColor.Gray);
        foreach (var group in refused.GroupBy(c => ReasonKind(c.Reason)).OrderByDescending(g => g.Count()))
        {
            WriteLine($"    ⚠ {group.Count()} × {group.Key}", ConsoleColor.Yellow);
            foreach (var c in group.Take(verbose ? int.MaxValue : 3)) WriteLine($"        {c.Path} — {c.Reason}", ConsoleColor.DarkYellow);
            if (!verbose && group.Count() > 3) WriteLine($"        ... (--verbose pour tout afficher)", ConsoleColor.DarkYellow);
        }
        if (r.Results.Count > 0)
        {
            WriteLine($"  DISM : {r.InjectedCount} pilote(s) injecté(s), {r.FailedCount} refusé(s)", r.FailedCount > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray);
            foreach (var f in r.Results.Where(x => !x.Success).Take(verbose ? int.MaxValue : 10)) WriteLine($"    ✗ {f.InfPath} : {f.Message}", ConsoleColor.Yellow);
            if (verbose) foreach (var s in r.Results.Where(x => x.Success)) WriteLine($"    ✓ {s.InfPath}", ConsoleColor.DarkGreen);
        }
        if (r.DriversInImage > 0) WriteLine($"  Pilotes tiers présents dans l'image : {r.DriversInImage}", ConsoleColor.Gray);
        WriteLine($"  {r.Message} ({r.Duration:hh\\:mm\\:ss})", r.Success ? ConsoleColor.Green : ConsoleColor.Red);
        if (r.ReportFile is not null) WriteLine($"  Rapport : {r.ReportFile}", ConsoleColor.DarkGray);
        if (r.DismLog is not null && File.Exists(r.DismLog)) WriteLine($"  Journal DISM : {r.DismLog}", ConsoleColor.DarkGray);
    }

    /// <summary>Regroupe les motifs de refus pour un affichage lisible (« ne convient pas à l'image », « non signé »...).</summary>
    private static string ReasonKind(string reason) =>
        reason.StartsWith("ne convient pas", StringComparison.OrdinalIgnoreCase) ? "architecture ou version de Windows différente"
        : reason.Contains("pas signé", StringComparison.OrdinalIgnoreCase) || reason.Contains(".cat", StringComparison.OrdinalIgnoreCase) || reason.Contains("catalogue", StringComparison.OrdinalIgnoreCase) ? "signature absente ou invalide"
        : reason.StartsWith("aucun périphérique", StringComparison.OrdinalIgnoreCase) ? "aucun périphérique déclaré"
        : "illisible";

    /// <summary>« 3 », « 1,3,6 », « 1-4 » ou « all ».</summary>
    internal static List<int> ParseIndexes(string spec, IReadOnlyList<int>? available)
    {
        spec = spec.Trim();
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase) || spec.Equals("tous", StringComparison.OrdinalIgnoreCase))
            return available?.ToList() ?? throw new ArgumentException("--index all n'est pas utilisable ici.");
        var result = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var from) && int.TryParse(range[1], out var to) && from <= to)
                result.AddRange(Enumerable.Range(from, to - from + 1));
            else if (int.TryParse(part, out var single) && single > 0)
                result.Add(single);
            else
                throw new ArgumentException($"Index invalide : {part}");
        }
        var missing = available is null ? new List<int>() : result.Where(i => !available.Contains(i)).ToList();
        if (missing.Count > 0) throw new ArgumentException($"Index absent de l'image : {string.Join(", ", missing)}");
        return result.Distinct().ToList();
    }
}
