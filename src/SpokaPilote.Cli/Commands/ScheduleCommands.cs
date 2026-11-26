using System.Globalization;
using System.Security.Principal;
using System.Text;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Cli;

/// <summary>Maintenance planifiée : schedule enable | disable | status | run-now, et l'exécution « maintenance ».</summary>
internal static partial class Program
{
    private static async Task<int> ScheduleAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0) ?? "status";
        Banner();
        switch (sub)
        {
            case "enable" or "activer":
            {
                if (AppPaths.IsPortable)
                {
                    WriteLine(MaintenanceScheduler.PortableRefusal, ConsoleColor.Red);
                    return ExitError;
                }
                var (options, error) = ParseMaintenanceOptions(a);
                if (options is null)
                {
                    WriteLine(error!, ConsoleColor.Red);
                    return ExitUsage;
                }
                var exe = Environment.ProcessPath;
                WriteLine($"Déclencheur : {MaintenanceScheduler.TriggerLabel(options)}", ConsoleColor.White);
                WriteLine($"Action      : {MaintenanceScheduler.ModeLabel(options.Mode)}" +
                          $"{(options.OutputDir is null ? "" : $", rapports dans {options.OutputDir}")}" +
                          $"{(options.PrinterProfile is null ? "" : $", profil d'imprimantes {options.PrinterProfile}")}", ConsoleColor.White);
                WriteLine($"Compte      : SYSTEM, privilèges maximum{(options.RunOnBattery ? "" : ", pas sur batterie")}, durée max. {options.ExecutionLimit.TotalMinutes:0} min", ConsoleColor.White);
                WriteLine($"Commande    : \"{exe}\" {MaintenanceScheduler.JoinArguments(MaintenanceScheduler.BuildArguments(options))}", ConsoleColor.Gray);
                if (options.OutputDir?.StartsWith(@"\\", StringComparison.Ordinal) == true)
                    WriteLine("  Le partage doit autoriser l'écriture au compte ordinateur (groupe « Ordinateurs du domaine »).", ConsoleColor.DarkGray);
                var (ok, message) = await MaintenanceScheduler.EnableAsync(options, exe ?? "", ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "disable" or "desactiver" or "remove":
            {
                var (ok, message) = await MaintenanceScheduler.DisableAsync(ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "run-now" or "run":
            {
                var (ok, message) = await MaintenanceScheduler.RunNowAsync(ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "status" or "etat":
            {
                var s = await MaintenanceScheduler.StatusAsync(ct);
                if (!s.Exists)
                {
                    WriteLine($"Aucune tâche « {MaintenanceScheduler.TaskName} » sur ce poste" +
                              $"{(Elevation.IsAdministrator() ? "" : " (ou lecture refusée : essayez dans une console administrateur)")}.", ConsoleColor.Gray);
                    WriteLine("Activer : spokapilote schedule enable --weekly lundi --time 12:30 --mode missing", ConsoleColor.Cyan);
                    return ExitOk;
                }
                var culture = CultureInfo.GetCultureInfo("fr-FR");
                string Date(DateTime? d) => d?.ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm", culture) ?? "—";
                WriteLine($"Tâche              : {MaintenanceScheduler.TaskName} ({s.State ?? "état inconnu"})", ConsoleColor.White);
                WriteLine($"Déclencheur        : {s.TriggerSummary}", ConsoleColor.White);
                WriteLine($"Commande           : \"{s.Command}\" {s.Arguments}", ConsoleColor.Gray);
                WriteLine($"Prochaine exécution: {Date(s.NextRun)}", ConsoleColor.White);
                WriteLine($"Dernière exécution : {Date(s.LastRun)}", ConsoleColor.White);
                var resultColor = s.LastResult is 0 or 3010 ? ConsoleColor.Green : s.LastResult is 2 ? ConsoleColor.Yellow
                    : s.LastResult is 0x41301 or 0x41303 or null ? ConsoleColor.Gray : ConsoleColor.Red;
                WriteLine($"Dernier résultat   : {MaintenanceScheduler.ResultLabel(s.LastResult)}", resultColor);
                if (s.MissedRuns > 0) WriteLine($"Exécutions manquées: {s.MissedRuns}", ConsoleColor.Yellow);
                var lastLog = LastMaintenanceLog();
                if (lastLog is not null) WriteLine($"Dernier journal    : {lastLog}", ConsoleColor.Gray);
                return ExitOk;
            }
            default:
                return Usage("schedule");
        }
    }

    private static string? LastMaintenanceLog()
    {
        try
        {
            return new DirectoryInfo(AppPaths.Logs).GetFiles("maintenance-*.log").OrderByDescending(f => f.Name).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (MaintenanceOptions? Options, string? Error) ParseMaintenanceOptions(Args a)
    {
        var o = new MaintenanceOptions();
        var triggers = new[] { a.Flag("weekly") || a.Value("weekly") is not null, a.Flag("daily"), a.Flag("at-startup") }.Count(x => x);
        if (triggers > 1) return (null, "Choisissez un seul déclencheur : --weekly <jour>, --daily ou --at-startup.");
        if (a.Flag("daily")) o.Trigger = MaintenanceTrigger.Daily;
        else if (a.Flag("at-startup")) o.Trigger = MaintenanceTrigger.AtStartup;
        else
        {
            var day = MaintenanceScheduler.ParseDay(a.Value("weekly"));
            if (day is null) return (null, $"Jour inconnu : {a.Value("weekly")} (lundi, mardi, ... dimanche).");
            o.Day = day.Value;
        }
        if (a.Value("time") is { } time)
        {
            if (MaintenanceScheduler.ParseTime(time) is not { } t) return (null, $"Heure invalide : {time} (format 12:30).");
            o.Time = t;
        }
        var mode = MaintenanceScheduler.ParseMode(a.Value("mode"));
        if (mode is null) return (null, $"Mode inconnu : {a.Value("mode")} (missing, updates ou scan).");
        o.Mode = mode.Value;

        int? Minutes(string key, int max)
        {
            var v = a.Value(key);
            return v is null ? null : int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m <= max ? m : -1;
        }
        if (Minutes("random-delay", 720) is { } random) { if (random < 0) return (null, "--random-delay : minutes de 0 à 720."); o.RandomDelay = TimeSpan.FromMinutes(random); }
        if (Minutes("startup-delay", 720) is { } startup) { if (startup < 0) return (null, "--startup-delay : minutes de 0 à 720."); o.StartupDelay = TimeSpan.FromMinutes(startup); }
        if (Minutes("max-duration", 1440) is { } max) { if (max < 10) return (null, "--max-duration : minutes de 10 à 1440."); o.ExecutionLimit = TimeSpan.FromMinutes(max); }
        o.RunOnBattery = a.Flag("on-battery");

        if (a.Value("output-dir") is { } outDir)
            o.OutputDir = outDir.StartsWith(@"\\", StringComparison.Ordinal) ? outDir : Path.GetFullPath(outDir);
        if (a.Value("printers") is { } printers)
        {
            if (PrinterProfileSource.IsServerKeyword(printers) || printers.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                printers.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                o.PrinterProfile = printers;
            else
            {
                // La tâche s'exécute en SYSTEM depuis un autre dossier : chemin absolu, et le fichier doit exister.
                var full = printers.StartsWith(@"\\", StringComparison.Ordinal) ? printers : Path.GetFullPath(printers);
                if (!File.Exists(full)) return (null, $"Profil d'imprimantes introuvable : {full}");
                o.PrinterProfile = full;
            }
        }
        return (o, null);
    }

    // ------------------------------------------------------------------ exécution de la maintenance

    /// <summary>
    /// « spokapilote maintenance --mode missing|updates|scan [--output-dir ...] [--printers ...] » : ce que lance la tâche
    /// planifiée (utilisable aussi à la main). Toute la sortie est copiée dans Logs\maintenance-AAAAMMJJ-HHMMSS.log.
    /// </summary>
    private static async Task<int> MaintenanceAsync(Args a, CancellationToken ct)
    {
        var mode = MaintenanceScheduler.ParseMode(a.Value("mode"));
        if (mode is null)
        {
            WriteLine($"Mode inconnu : {a.Value("mode")} (missing, updates ou scan).", ConsoleColor.Red);
            return ExitUsage;
        }

        var logPath = MaintenanceScheduler.NewMaintenanceLogPath();
        var original = Console.Out;
        using var logFile = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        Console.SetOut(new TeeWriter(original, logFile));
        var exit = ExitOk;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            WriteLine($"=== Maintenance SpokaPilote — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {Environment.MachineName} ({identity.Name}) — {MaintenanceScheduler.ModeLabel(mode.Value)} ===", ConsoleColor.Cyan);
            Log.Info($"Maintenance démarrée ({MaintenanceScheduler.ModeName(mode.Value)}), journal : {logPath}");

            var scanArgs = mode switch
            {
                MaintenanceMode.Scan => new List<string> { "scan" },
                MaintenanceMode.Updates => new List<string> { "install", "--yes" },
                _ => new List<string> { "install", "--missing", "--yes" },
            };
            if (a.Value("output-dir") is { } outDir) scanArgs.AddRange(new[] { "--output-dir", outDir });
            if (a.Flag("verbose")) scanArgs.Add("--verbose");
            try
            {
                exit = Worst(exit, await ScanAsync(new Args(scanArgs.ToArray()), install: mode != MaintenanceMode.Scan, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteLine($"Analyse / installation en échec : {ex.Message}", ConsoleColor.Red);
                Log.Error("Maintenance : analyse en échec", ex);
                exit = ExitError;
            }

            if (a.Value("printers") is { } printers)
            {
                WriteLine("\n=== Profil d'imprimantes ===", ConsoleColor.Cyan);
                var printerArgs = new List<string> { "printers", "deploy" };
                if (!PrinterProfileSource.IsServerKeyword(printers)) printerArgs.Add(printers);
                printerArgs.Add("--yes");
                try
                {
                    exit = Worst(exit, await PrintersAsync(new Args(printerArgs.ToArray()), ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    WriteLine($"Déploiement des imprimantes en échec : {ex.Message}", ConsoleColor.Red);
                    Log.Error("Maintenance : profil d'imprimantes en échec", ex);
                    exit = Worst(exit, ExitPartial);
                }
            }

            WriteLine($"\n=== Fin de la maintenance — code {exit} — {DateTime.Now:HH:mm:ss} ===", exit is ExitOk or ExitRebootRequired ? ConsoleColor.Green : ConsoleColor.Yellow);
            Log.Info($"Maintenance terminée (code {exit}).");
            return exit;
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>Code le plus grave : erreur &gt; échec partiel &gt; redémarrage requis &gt; OK.</summary>
    private static int Worst(int a, int b)
    {
        static int Rank(int code) => code switch { ExitOk => 0, ExitRebootRequired => 1, ExitPartial => 2, _ => 3 };
        return Rank(b) > Rank(a) ? b : a;
    }

    /// <summary>Recopie la sortie console dans le journal de maintenance.</summary>
    private sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            try { file.Write(value); } catch (IOException) { /* journal facultatif */ }
        }

        public override void Write(string? value)
        {
            console.Write(value);
            try { file.Write(value); } catch (IOException) { /* journal facultatif */ }
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            try { file.WriteLine(value); } catch (IOException) { /* journal facultatif */ }
        }

        public override void Flush()
        {
            console.Flush();
            try { file.Flush(); } catch (IOException) { /* journal facultatif */ }
        }
    }
}
