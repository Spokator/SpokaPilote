using System.Text;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Printers;
using SpokaPilote.Core.Repository;
using SpokaPilote.Core.Sources;
using CoreLogLevel = SpokaPilote.Core.Infrastructure.LogLevel;

namespace SpokaPilote.Cli;

internal static class Program
{
    // Codes de sortie exploitables par les scripts / RMM / Intune / GPO.
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitPartial = 2;
    private const int ExitUsage = 64;
    private const int ExitRebootRequired = 3010;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var a = new Args(args);
        Log.MinimumLevel = a.Flag("verbose") ? CoreLogLevel.Debug : a.Flag("quiet") ? CoreLogLevel.Warning : CoreLogLevel.Info;
        Log.Subscribe(e =>
        {
            if (e.Level == CoreLogLevel.Info && !a.Flag("verbose")) return; // les étapes sont déjà affichées par la CLI
            var color = e.Level switch { CoreLogLevel.Error => ConsoleColor.Red, CoreLogLevel.Warning => ConsoleColor.Yellow, _ => ConsoleColor.DarkGray };
            WriteLine($"  {e.Message}", color);
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); WriteLine("Annulation demandée...", ConsoleColor.Yellow); };

        try
        {
            return a.Command switch
            {
                "scan" => await ScanAsync(a, install: false, cts.Token),
                "install" or "update" => await ScanAsync(a, install: true, cts.Token),
                "export" => await ExportAsync(a, cts.Token),
                "import" => await ImportAsync(a, cts.Token),
                "printers" => await PrintersAsync(a, cts.Token),
                "config" => Config(a),
                "version" or "--version" => Version(),
                _ => Help(),
            };
        }
        catch (OperationCanceledException)
        {
            WriteLine("Opération annulée.", ConsoleColor.Yellow);
            return ExitError;
        }
        catch (Exception ex)
        {
            WriteLine($"Erreur : {ex.Message}", ConsoleColor.Red);
            Log.Error("Erreur fatale CLI", ex);
            return ExitError;
        }
    }

    // ------------------------------------------------------------------ scan / install

    private static async Task<int> ScanAsync(Args a, bool install, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        Banner();
        if (install && !a.Flag("dry-run") && !Elevation.IsAdministrator())
        {
            WriteLine("L'installation nécessite une console administrateur (clic droit > Exécuter en tant qu'administrateur).", ConsoleColor.Red);
            return ExitError;
        }

        var options = new ScanOptions
        {
            DeepScan = a.Flag("deep"),
            IncludeOptional = true,
            OnlySources = ParseSources(a.Value("sources")),
        };

        var engine = new DriverScanEngine(settings);
        var lastStage = "";
        var scan = await engine.ScanAsync(options, new Progress<ScanProgress>(p =>
        {
            if (p.Stage != lastStage || p.Percent is null) WriteLine($"› {p.Message}", ConsoleColor.Gray);
            lastStage = p.Stage;
        }), ct);

        PrintSystem(scan);
        PrintCandidates(scan);

        List<InstallOutcome>? outcomes = null;
        var exit = ExitOk;
        if (install)
        {
            var selection = Select(scan.Candidates, a);
            if (selection.Count == 0)
            {
                WriteLine("\nRien à installer avec les critères choisis.", ConsoleColor.Green);
            }
            else
            {
                WriteLine($"\n{selection.Count} pilote(s) sélectionné(s) pour installation{(a.Flag("dry-run") ? " (SIMULATION)" : "")}.", ConsoleColor.Cyan);
                if (!a.Flag("yes") && !a.Flag("dry-run") && !Confirm("Continuer ?"))
                    return ExitOk;

                var installer = new DriverInstaller(settings, scan.System);
                outcomes = await installer.InstallAsync(selection, scan.Devices, new InstallOptions
                {
                    DryRun = a.Flag("dry-run"),
                    CreateRestorePoint = settings.CreateRestorePoint && !a.Flag("no-restore-point"),
                    BackupDrivers = settings.BackupDriversBeforeInstall && !a.Flag("no-backup"),
                }, new Progress<InstallProgress>(p => WriteLine($"  [{p.Index}/{p.Total}] {p.Title} — {p.Step}", ConsoleColor.Gray)), ct);

                WriteLine("\nRésultat :", ConsoleColor.White);
                foreach (var o in outcomes)
                {
                    var color = o.Succeeded ? ConsoleColor.Green : o.Status == InstallStatus.Skipped ? ConsoleColor.Gray : ConsoleColor.Red;
                    WriteLine($"  {Symbol(o.Status)} {o.Candidate.Title} {o.Candidate.Version} — {o.Message}", color);
                }
                if (outcomes.Any(o => o.Status is InstallStatus.Failed or InstallStatus.Blocked)) exit = ExitPartial;
                if (outcomes.Any(o => o.RebootRequired))
                {
                    WriteLine("\nUn redémarrage est nécessaire pour finaliser l'installation.", ConsoleColor.Yellow);
                    if (exit == ExitOk) exit = ExitRebootRequired;
                }
            }
        }

        WriteReports(a, scan, outcomes);
        return exit;
    }

    private static List<DriverCandidate> Select(List<DriverCandidate> candidates, Args a)
    {
        var onlyMissing = a.Flag("missing");
        return candidates.Where(c => c.Kind switch
        {
            CandidateKind.MissingDriver => true,
            CandidateKind.Update => !onlyMissing,
            CandidateKind.Optional => !onlyMissing && a.Flag("include-optional"),
            CandidateKind.Firmware => !onlyMissing && a.Flag("include-firmware"),
            _ => false,
        }).ToList();
    }

    private static void PrintSystem(ScanResult scan)
    {
        var s = scan.System;
        WriteLine($"\nMachine   : {s.ComputerName} — {s.Manufacturer} {s.DisplayModel}", ConsoleColor.White);
        WriteLine($"Système   : {s.OsCaption} {s.OsDisplayVersion} ({s.OsVersion}, {s.Architecture})", ConsoleColor.White);
        if (s.SerialNumber is not null) WriteLine($"N° série  : {s.SerialNumber}", ConsoleColor.White);
        WriteLine($"Sources   : {string.Join(", ", scan.SourcesUsed)} ({scan.Duration.TotalSeconds:0} s)", ConsoleColor.White);
        foreach (var (src, err) in scan.SourceErrors) WriteLine($"  ⚠ {src} indisponible : {err}", ConsoleColor.Yellow);

        var problems = scan.ProblemDevices.ToList();
        WriteLine($"\nPériphériques : {scan.Devices.Count(d => d.IsPresent)} détectés, {problems.Count} en erreur", problems.Count > 0 ? ConsoleColor.Yellow : ConsoleColor.Green);
        foreach (var d in problems)
            WriteLine($"  ✗ {d.Name} — {d.StatusText}  [{d.HardwareIds.FirstOrDefault() ?? d.InstanceId}]", ConsoleColor.Yellow);
    }

    private static void PrintCandidates(ScanResult scan)
    {
        if (scan.Candidates.Count == 0)
        {
            WriteLine("\n✓ Aucun pilote manquant ou à mettre à jour.", ConsoleColor.Green);
        }
        else
        {
            WriteLine($"\nPilotes proposés ({scan.Candidates.Count}) :", ConsoleColor.White);
            foreach (var group in scan.Candidates.GroupBy(c => c.KindLabel))
            {
                WriteLine($"  {group.Key}", ConsoleColor.Cyan);
                foreach (var c in group)
                {
                    var devices = c.TargetDeviceNames.Count > 0 ? $" → {string.Join(", ", c.TargetDeviceNames.Distinct().Take(3))}" : "";
                    WriteLine($"    • {c.Title} {c.Version} [{c.SourceLabel}{(c.SizeLabel.Length > 0 ? ", " + c.SizeLabel : "")}]{devices}", ConsoleColor.Gray);
                }
            }
        }
        foreach (var d in scan.UnresolvedDevices)
        {
            WriteLine($"  ? Aucun pilote trouvé pour « {d.Name} » ({d.HardwareIds.FirstOrDefault()})", ConsoleColor.DarkYellow);
            if (d.Hint is not null) WriteLine($"      → {d.Hint}", ConsoleColor.DarkYellow);
        }
    }

    private static void WriteReports(Args a, ScanResult scan, List<InstallOutcome>? outcomes)
    {
        var json = a.Value("json");
        var html = a.Value("html") ?? a.Value("report");
        if (json is not null)
        {
            File.WriteAllText(json, ReportWriter.ToJson(scan, outcomes));
            WriteLine($"\nRapport JSON : {Path.GetFullPath(json)}", ConsoleColor.Gray);
        }
        if (html is not null)
        {
            File.WriteAllText(html, ReportWriter.ToHtml(scan, outcomes));
            WriteLine($"Rapport HTML : {Path.GetFullPath(html)}", ConsoleColor.Gray);
        }
        // Copie systématique dans %ProgramData%\SpokaPilote\Reports pour l'historique du poste.
        try
        {
            var auto = Path.Combine(AppPaths.Reports, $"{scan.System.ComputerName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(auto, ReportWriter.ToJson(scan, outcomes));
        }
        catch { /* historique facultatif */ }
    }

    private static HashSet<DriverSourceKind>? ParseSources(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var set = new HashSet<DriverSourceKind>();
        foreach (var s in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (s.ToLowerInvariant())
            {
                case "wu" or "windowsupdate": set.Add(DriverSourceKind.WindowsUpdate); break;
                case "catalog" or "microsoft": set.Add(DriverSourceKind.MicrosoftCatalog); break;
                case "repo" or "local": set.Add(DriverSourceKind.LocalRepository); break;
                case "oem": set.UnionWith(new[] { DriverSourceKind.Dell, DriverSourceKind.HP, DriverSourceKind.Lenovo }); break;
                case "dell": set.Add(DriverSourceKind.Dell); break;
                case "hp": set.Add(DriverSourceKind.HP); break;
                case "lenovo": set.Add(DriverSourceKind.Lenovo); break;
                default: throw new ArgumentException($"Source inconnue : {s} (valeurs : wu, catalog, oem, dell, hp, lenovo, repo)");
            }
        }
        return set;
    }

    // ------------------------------------------------------------------ dépôt

    private static async Task<int> ExportAsync(Args a, CancellationToken ct)
    {
        var target = a.Positional(0) ?? throw new ArgumentException("Usage : spokapilote export <dossier-du-dépôt>");
        Banner();
        WriteLine("Export des pilotes de ce poste de référence...", ConsoleColor.Gray);
        var (ok, _, msg) = await DriverExporter.ExportAsync(target, ct);
        WriteLine(msg, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitError;
    }

    private static async Task<int> ImportAsync(Args a, CancellationToken ct)
    {
        var source = a.Positional(0);
        var repo = a.Positional(1) ?? LoadSettings(a).RepositoryPaths.FirstOrDefault();
        if (source is null || repo is null)
        {
            WriteLine("Usage : spokapilote import <archive.zip|dossier> [dossier-du-dépôt]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        var (ok, msg) = await DriverExporter.ImportAsync(source, repo, ct);
        WriteLine(msg, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitError;
    }

    // ------------------------------------------------------------------ imprimantes

    private static async Task<int> PrintersAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        await PrinterVendorCatalog.LoadOrganizationCatalogAsync(settings, ct);
        var sub = a.Positional(0) ?? "discover";
        Banner();

        if (sub is "discover" or "scan" or "list")
        {
            var targets = a.Value("range") is { } range ? PrinterDiscovery.ParseTargets(range) : null;
            if (targets is null)
                foreach (var (net, prefix, nic) in PrinterDiscovery.LocalSubnets())
                    WriteLine($"Réseau {net}/{prefix} ({nic})", ConsoleColor.Gray);

            var printers = await PrinterDiscovery.DiscoverAsync(targets, new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
            WriteLine($"\n{printers.Count} imprimante(s) trouvée(s) :", ConsoleColor.White);
            foreach (var p in printers)
            {
                WriteLine($"  • {p.Address,-15} {p.DisplayName}", ConsoleColor.Cyan);
                WriteLine($"      ports : {p.Ports} · identifiée par : {(p.IdentifiedBy.Count > 0 ? string.Join("+", p.IdentifiedBy) : "—")}" +
                          $"{(p.SerialNumber is null ? "" : " · S/N " + p.SerialNumber)}{(p.Location is null ? "" : " · " + p.Location)}", ConsoleColor.Gray);
                if (p.InstalledAs is not null) WriteLine($"      déjà installée sous « {p.InstalledAs} »", ConsoleColor.Green);
                if (a.Flag("drivers"))
                {
                    var options = await PrinterDriverResolver.ResolveAsync(p, settings, ct);
                    foreach (var o in options.Take(4)) WriteLine($"      ↳ {o}", ConsoleColor.DarkCyan);
                }
            }
            if (a.Value("json") is { } jsonPath)
            {
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(printers, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                WriteLine($"\nExport JSON : {Path.GetFullPath(jsonPath)}", ConsoleColor.Gray);
            }
            return ExitOk;
        }

        if (sub is "find-driver" or "drivers")
        {
            var model = a.Positional(1) ?? throw new ArgumentException("Usage : spokapilote printers find-driver \"KYOCERA TASKalfa 3253ci\"");
            var fake = new DiscoveredPrinter { Address = "0.0.0.0", Model = model };
            fake.Manufacturer = PrinterVendorCatalog.FindVendor(model)?.Name;
            WriteLine($"Modèle : {model} · constructeur : {fake.Manufacturer ?? "inconnu"}", ConsoleColor.White);
            var options = await PrinterDriverResolver.ResolveAsync(fake, settings, ct);
            if (options.Count == 0) WriteLine("Aucune piste trouvée.", ConsoleColor.Yellow);
            foreach (var o in options) WriteLine($"  {o}{(o.Url is null ? "" : "  " + o.Url)}", ConsoleColor.Cyan);

            if (a.Flag("verify"))
            {
                // Télécharge et valide le premier paquet téléchargeable, sans rien installer.
                var installer = new PrinterInstaller(settings);
                foreach (var o in options.Where(o => o.Kind is PrinterDriverOptionKind.MicrosoftCatalog or PrinterDriverOptionKind.LocalRepository))
                {
                    WriteLine($"\nVérification de « {o.Label} »...", ConsoleColor.White);
                    var (inf, name, error) = await installer.PrepareInfAsync(o, fake, new Progress<string>(m => WriteLine($"  › {m}", ConsoleColor.DarkGray)), ct);
                    if (inf is not null)
                    {
                        WriteLine($"  ✓ Pilote « {name} » prêt ({inf})", ConsoleColor.Green);
                        return ExitOk;
                    }
                    WriteLine($"  ✗ {error}", ConsoleColor.Yellow);
                }
                return ExitError;
            }
            return options.Count > 0 ? ExitOk : ExitError;
        }

        if (sub == "install")
        {
            var address = a.Positional(1) ?? throw new ArgumentException("Usage : spokapilote printers install <ip|nom> [--name ...] [--driver ...] [--ipp]");
            if (!Elevation.IsAdministrator())
            {
                WriteLine("L'ajout d'une imprimante nécessite une console administrateur.", ConsoleColor.Red);
                return ExitError;
            }
            var ip = PrinterDiscovery.ParseTargets(address).FirstOrDefault()?.ToString() ?? address;
            var printer = (await PrinterDiscovery.DiscoverAsync(new[] { System.Net.IPAddress.Parse(ip) }, ct: ct)).FirstOrDefault();
            if (printer is null)
            {
                WriteLine($"Aucune imprimante ne répond sur {ip} (ports 9100/631/515).", ConsoleColor.Red);
                return ExitError;
            }
            WriteLine($"Imprimante : {printer.DisplayName} ({printer.Address})", ConsoleColor.White);

            var options = await PrinterDriverResolver.ResolveAsync(printer, settings, ct);
            PrinterDriverOption? choice;
            if (a.Flag("ipp")) choice = options.FirstOrDefault(o => o.Kind == PrinterDriverOptionKind.IppClass);
            else if (a.Value("driver") is { } wanted)
                choice = options.FirstOrDefault(o => string.Equals(o.DriverName, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? new PrinterDriverOption { Kind = PrinterDriverOptionKind.Installed, DriverName = wanted, Label = wanted };
            else choice = options.FirstOrDefault(o => o.Kind != PrinterDriverOptionKind.VendorPage);

            WriteLine("Pilotes possibles :", ConsoleColor.Gray);
            foreach (var o in options) WriteLine($"  {(o == choice ? "→" : " ")} {o}", o == choice ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
            if (choice is null)
            {
                WriteLine("Aucun pilote utilisable trouvé automatiquement.", ConsoleColor.Red);
                return ExitError;
            }
            if (!a.Flag("yes") && !Confirm($"Installer avec « {choice.Label} » ?")) return ExitOk;

            var result = await new PrinterInstaller(settings).InstallAsync(new PrinterInstallRequest
            {
                Printer = printer,
                Driver = choice,
                PrinterName = a.Value("name"),
                Location = a.Value("location") ?? printer.Location,
                SetAsDefault = a.Flag("default"),
                Shared = a.Flag("share"),
            }, new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
            WriteLine(result.Message, result.Success ? ConsoleColor.Green : ConsoleColor.Red);
            return result.Success ? ExitOk : ExitError;
        }

        return Help();
    }

    // ------------------------------------------------------------------ configuration

    private static int Config(Args a)
    {
        var settings = Settings.Load();
        var sub = a.Positional(0) ?? "show";
        switch (sub)
        {
            case "add-repo":
                var path = a.Positional(1) ?? throw new ArgumentException("Usage : spokapilote config add-repo <chemin>");
                if (!settings.RepositoryPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) settings.RepositoryPaths.Add(path);
                settings.Save();
                break;
            case "remove-repo":
                settings.RepositoryPaths.RemoveAll(p => p.Equals(a.Positional(1), StringComparison.OrdinalIgnoreCase));
                settings.Save();
                break;
            case "set":
                var key = a.Positional(1);
                var value = a.Positional(2);
                if (key is null || value is null) throw new ArgumentException("Usage : spokapilote config set <clé> <valeur>");
                var prop = typeof(Settings).GetProperties().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                           ?? throw new ArgumentException($"Clé inconnue : {key}");
                object converted = prop.PropertyType == typeof(bool) ? bool.Parse(value)
                    : prop.PropertyType == typeof(int) ? int.Parse(value)
                    : prop.PropertyType == typeof(string) ? value
                    : throw new ArgumentException($"La clé {key} ne se règle pas avec « set ».");
                prop.SetValue(settings, converted);
                settings.Save();
                break;
        }
        WriteLine($"Fichier : {AppPaths.SettingsFile}", ConsoleColor.Gray);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return ExitOk;
    }

    private static Settings LoadSettings(Args a)
    {
        var settings = Settings.Load();
        foreach (var repo in a.Values("repo"))
            if (!settings.RepositoryPaths.Contains(repo, StringComparer.OrdinalIgnoreCase)) settings.RepositoryPaths.Add(repo);
        if (a.Flag("include-firmware")) settings.IncludeBiosAndFirmware = true;
        if (a.Flag("no-catalog")) settings.UseMicrosoftCatalog = false;
        if (a.Flag("no-wu")) settings.UseWindowsUpdate = false;
        if (a.Flag("no-oem")) settings.UseOemCatalogs = false;
        return settings;
    }

    // ------------------------------------------------------------------ utilitaires console

    private static void Banner()
    {
        WriteLine($"SpokaPilote {typeof(Program).Assembly.GetName().Version?.ToString(3)} — pilotes de confiance, sources officielles uniquement", ConsoleColor.Cyan);
        if (!Elevation.IsAdministrator()) WriteLine("(console non administrateur : analyse seule)", ConsoleColor.DarkGray);
    }

    private static int Version()
    {
        Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
        return ExitOk;
    }

    private static string Symbol(InstallStatus s) => s switch
    {
        InstallStatus.Installed => "✓",
        InstallStatus.NotApplicable => "=",
        InstallStatus.Skipped => "·",
        InstallStatus.Blocked => "⛔",
        _ => "✗",
    };

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write($"{question} [o/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "o" or "oui" or "y" or "yes";
    }

    private static readonly object ConsoleLock = new();

    private static void WriteLine(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            SpokaPilote — installation de pilotes depuis les sources officielles (Microsoft, Dell, HP, Lenovo, dépôt interne)

            ANALYSE ET INSTALLATION
              spokapilote scan [--deep] [--html rapport.html] [--json rapport.json]
              spokapilote install [--missing] [--include-optional] [--include-firmware] [--deep]
                                  [--dry-run] [--yes] [--no-restore-point] [--no-backup] [--report rapport.html]
                --deep               interroge aussi le catalogue Microsoft pour les composants qui fonctionnent
                --missing            n'installe que les pilotes manquants (périphériques en erreur)
                --sources wu,catalog,oem,repo   limite les sources interrogées
                --repo <chemin>      ajoute un dépôt local/réseau pour cette exécution (répétable)

            DÉPÔT DE PILOTES (déploiement de parc, hors ligne)
              spokapilote export <\\serveur\pilotes>       exporte les pilotes de ce poste de référence
              spokapilote import <archive.zip|dossier> [dépôt]
              spokapilote config add-repo <\\serveur\pilotes>

            IMPRIMANTES
              spokapilote printers discover [--range 192.168.1.0/24] [--drivers] [--json imprimantes.json]
              spokapilote printers find-driver "KYOCERA TASKalfa 3253ci" [--verify]
                --verify             télécharge et valide le paquet (signature, modèle exact) sans l'installer
              spokapilote printers install <ip> [--name "Accueil RDC"] [--driver "nom exact"] [--ipp]
                                           [--location "..."] [--default] [--share] [--yes]

            CONFIGURATION
              spokapilote config [show | set <clé> <valeur> | add-repo <chemin> | remove-repo <chemin>]

            Codes de sortie : 0 OK · 1 erreur · 2 échec partiel · 3010 redémarrage requis
            Journaux : %ProgramData%\SpokaPilote\Logs
            """);
        return ExitUsage;
    }
}

/// <summary>Analyse minimaliste des arguments : commande, positionnels, --drapeaux et --clé valeur.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "html", "report", "sources", "repo", "range", "name", "driver", "location",
    };

    private readonly List<string> _positionals = new();
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, string Value)> _values = new();

    public string? Command { get; }

    public Args(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                var eq = key.IndexOf('=');
                if (eq > 0) _values.Add((key[..eq], key[(eq + 1)..]));
                else if (ValueOptions.Contains(key) && i + 1 < args.Length) _values.Add((key, args[++i]));
                else _flags.Add(key);
            }
            else if (arg is "-h" or "/?") Command = "help";
            else if (Command is null) Command = arg.ToLowerInvariant();
            else _positionals.Add(arg);
        }
    }

    public bool Flag(string name) => _flags.Contains(name);
    public string? Value(string name) => _values.LastOrDefault(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> Values(string name) => _values.Where(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value);
    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;
}
