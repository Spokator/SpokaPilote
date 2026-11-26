using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Health;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using CoreLog = SpokaPilote.Core.Infrastructure.Log;
using CoreLogLevel = SpokaPilote.Core.Infrastructure.LogLevel;

namespace SpokaPilote.Cli;

/// <summary>Commande « spokapilote health » : santé des pilotes du poste (score, note, constats).</summary>
internal static partial class Program
{
    private static async Task<int> HealthAsync(Args a, CancellationToken ct)
    {
        // « --json » seul : le JSON du contrat « health » sur la sortie standard, sans autre texte (scripts, RMM).
        var jsonToStdout = a.Flag("json");
        if (jsonToStdout) CoreLog.MinimumLevel = CoreLogLevel.Error;
        var settings = LoadSettings(a);
        if (!jsonToStdout)
        {
            Banner();
            WriteLine(a.Flag("quick")
                ? "Santé des pilotes (rapide : inventaire et politique seulement, sans recherche de mises à jour)."
                : "Santé des pilotes (analyse complète, rien n'est installé).", ConsoleColor.Gray);
        }

        var engine = new DriverScanEngine(settings, policyLoader: CreatePolicyLoader(a, settings));
        var lastStage = "";
        var scan = await engine.ScanAsync(new ScanOptions
        {
            DeepScan = a.Flag("deep"),
            IncludeOptional = true,
            // --quick : aucune source interrogée (les mises à jour disponibles ne sont alors pas évaluées).
            OnlySources = a.Flag("quick") ? new HashSet<DriverSourceKind>() : ParseSources(a.Value("sources")),
        }, jsonToStdout ? null : new Progress<ScanProgress>(p =>
        {
            if (p.Stage != lastStage || p.Percent is null) WriteLine($"› {p.Message}", ConsoleColor.Gray);
            lastStage = p.Stage;
        }), ct);

        var health = scan.Health ?? DriverHealth.Compute(scan);
        if (jsonToStdout)
        {
            Console.WriteLine(health.ToJson());
            return ExitOk;
        }

        WriteLine($"\nMachine   : {scan.System.ComputerName} — {scan.System.Manufacturer} {scan.System.DisplayModel}", ConsoleColor.White);
        PrintPolicySummary(scan.Policy);
        foreach (var (src, err) in scan.SourceErrors) WriteLine($"  ⚠ {src} indisponible : {err}", ConsoleColor.Yellow);
        PrintHealth(health, detailed: true);
        if (!a.Flag("quick")) PrintFirmware(scan, detailed: true);
        if (a.Flag("quick")) WriteLine("\n(Mode rapide : les mises à jour disponibles ne sont pas comptées ; relancez sans --quick pour une note complète.)", ConsoleColor.DarkGray);

        if (a.Value("json") is { } jsonFile)
        {
            File.WriteAllText(jsonFile, health.ToJson());
            WriteLine($"\nSanté (JSON) : {Path.GetFullPath(jsonFile)}", ConsoleColor.Gray);
        }
        if ((a.Value("html") ?? a.Value("report")) is { } html)
        {
            File.WriteAllText(html, ReportWriter.ToHtml(scan));
            WriteLine($"Rapport HTML : {Path.GetFullPath(html)}", ConsoleColor.Gray);
        }
        return ExitOk;
    }

    /// <summary>Bloc « Santé des pilotes » (scan : critiques et avertissements détaillés ; health : tout).</summary>
    private static void PrintHealth(HealthReport? health, bool detailed)
    {
        if (health is null) return;
        var color = health.Grade switch
        {
            "A" or "B" => ConsoleColor.Green,
            "C" => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
        WriteLine($"\nSanté des pilotes : note {health.Grade} ({health.Score}/100) — {health.GradeLabel}", color);
        if (health.Findings.Count == 0)
        {
            WriteLine("  ✓ Aucun constat : pilotes à jour, signés et sans erreur.", ConsoleColor.Green);
            return;
        }
        foreach (var f in health.Findings)
        {
            if (!detailed && f.Severity == HealthSeverity.Info) continue;
            var (symbol, c) = f.Severity switch
            {
                HealthSeverity.Critical => ("⛔", ConsoleColor.Red),
                HealthSeverity.Warning => ("⚠", ConsoleColor.Yellow),
                _ => ("·", ConsoleColor.Gray),
            };
            WriteLine($"  {symbol} [{f.SeverityLabel}] {f.Title}{(f.Penalty > 0 ? $" (−{f.Penalty})" : "")}", c);
            if (detailed || f.Severity == HealthSeverity.Critical) WriteLine($"      {f.Detail}", ConsoleColor.DarkGray);
        }
        if (!detailed && health.InfoCount > 0)
            WriteLine($"  + {health.InfoCount} information(s) (détail : spokapilote health ou rapport HTML).", ConsoleColor.DarkGray);
    }

    /// <summary>
    /// Ligne « BIOS : 1.12.0 installé, 1.18.0 disponible (critique) → https://... » et firmwares d'accessoires détectés.
    /// Information seulement : rien n'est installé sans --include-firmware.
    /// </summary>
    private static void PrintFirmware(ScanResult scan, bool detailed)
    {
        var firmware = scan.Firmware;
        var bios = firmware?.Bios;
        var color = bios?.State switch
        {
            BiosState.Outdated => bios.IsCritical ? ConsoleColor.Red : ConsoleColor.Yellow,
            BiosState.UpToDate or BiosState.InstalledNewer => ConsoleColor.Green,
            _ => ConsoleColor.Gray,
        };
        WriteLine($"\n{FirmwareStatus.Describe(firmware, scan.System)}", color);
        if (bios is { State: BiosState.Outdated })
            WriteLine($"  ⚠ {BiosStatus.UpdateWarning}", ConsoleColor.DarkYellow);
        if (firmware is null) return;
        foreach (var c in firmware.Components)
        {
            WriteLine($"  · {c.KindLabel} : {c.Title} {c.Version} disponible (information, non installé) — {string.Join(", ", c.Devices.Take(3))}", ConsoleColor.Gray);
            if (detailed && (c.InfoUrl ?? c.DownloadUrl) is { } url) WriteLine($"      → {url}", ConsoleColor.DarkGray);
        }
        if (detailed)
            foreach (var note in firmware.Notes) WriteLine($"  ({note})", ConsoleColor.DarkGray);
    }

    /// <summary>Précautions affichées avant d'installer une sélection qui contient un BIOS ou un firmware.</summary>
    private static void WarnFirmwareSelection(IEnumerable<DriverCandidate> selection)
    {
        if (!selection.Any(c => c.Kind == CandidateKind.Firmware)) return;
        WriteLine($"\n⚠ La sélection contient une mise à jour de BIOS / firmware. {BiosStatus.UpdateWarning}", ConsoleColor.Yellow);
    }
}
