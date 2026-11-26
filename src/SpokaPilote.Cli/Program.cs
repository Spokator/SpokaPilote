using System.Text;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;
using SpokaPilote.Core.Printers;
using SpokaPilote.Core.Repository;
using SpokaPilote.Core.Sources;
using CoreLogLevel = SpokaPilote.Core.Infrastructure.LogLevel;

namespace SpokaPilote.Cli;

internal static partial class Program
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

        // Usage automatisé : mieux vaut attendre quelques minutes que rendre un résultat partiel.
        MicrosoftCatalogClient.WaitWhenRateLimited = true;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); WriteLine("Annulation demandée...", ConsoleColor.Yellow); };

        // « spokapilote scan --help » / « spokapilote help scan » : aide de la commande, sans l'exécuter.
        if (a.Command is "help" or null) return a.Positional(0) is { } topic ? HelpFor(topic) : Help();
        if (a.Flag("help")) return HelpFor(a.Command);

        try
        {
            return a.Command switch
            {
                "scan" => await ScanAsync(a, install: false, cts.Token),
                "install" or "update" => await ScanAsync(a, install: true, cts.Token),
                "export" => await ExportAsync(a, cts.Token),
                "import" => await ImportAsync(a, cts.Token),
                "printers" => await PrintersAsync(a, cts.Token),
                "pack" => await PackAsync(a, cts.Token),
                "driverpack" or "driver-pack" => await DriverPackAsync(a, cts.Token),
                "image" => await ImageAsync(a, cts.Token),
                "history" or "historique" => History(),
                "rollback" or "annuler" => await RollbackAsync(a, cts.Token),
                "restore" or "restaurer" => await RestoreAsync(a, cts.Token),
                "fleet-report" or "parc" => FleetReportCommand(a),
                "self-update" or "mise-a-jour" => await SelfUpdateAsync(a, cts.Token),
                "config" => Config(a),
                "policy" or "politique" => await PolicyAsync(a, cts.Token),
                "schedule" or "planification" => await ScheduleAsync(a, cts.Token),
                "maintenance" => await MaintenanceAsync(a, cts.Token),
                "server" or "serveur" => await ServerAsync(a, cts.Token),
                "health" or "sante" or "santé" => await HealthAsync(a, cts.Token),
                "device" or "peripherique" or "périphérique" => await DeviceAsync(a, cts.Token),
                "driverstore" or "driver-store" or "magasin" => await DriverStoreAsync(a, cts.Token),
                "agent" => await AgentAsync(a, cts.Token),
                "version" or "--version" => Version(),
                _ => Help(a.Command),
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

        var engine = new DriverScanEngine(settings, policyLoader: CreatePolicyLoader(a, settings));
        var lastStage = "";
        var scan = await engine.ScanAsync(options, new Progress<ScanProgress>(p =>
        {
            if (p.Stage != lastStage || p.Percent is null) WriteLine($"› {p.Message}", ConsoleColor.Gray);
            lastStage = p.Stage;
        }), ct);

        PrintSystem(scan);
        PrintCandidates(scan);
        PrintHealth(scan.Health, detailed: false);
        PrintFirmware(scan, detailed: false);

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
                WarnFirmwareSelection(selection);
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
        await UploadToServerAsync(a, settings, scan, outcomes, ct);
        return exit;
    }

    private static List<DriverCandidate> Select(List<DriverCandidate> candidates, Args a)
    {
        var onlyMissing = a.Flag("missing");
        var only = a.Values("only").ToList();
        var exclude = a.Values("exclude").ToList();
        bool Matches(DriverCandidate c, string text) =>
            c.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            c.TargetDeviceNames.Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
        return candidates
            .Where(c => c.BlockedReason is null) // jamais de pilote bloqué par la politique
            .Where(c => !c.IsInformational) // liens vers une page officielle : jamais installés automatiquement
            .Where(c => only.Count == 0 || only.Any(t => Matches(c, t)))
            .Where(c => !exclude.Any(t => Matches(c, t)))
            .Where(c => c.Kind switch
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
        PrintPolicySummary(scan.Policy);

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
                    if (c.BlockedReason is null)
                    {
                        WriteLine($"    • {c.Title} {c.Version} [{c.SourceLabel}{(c.SizeLabel.Length > 0 ? ", " + c.SizeLabel : "")}]{devices}", ConsoleColor.Gray);
                        if (c.IsInformational && c.InfoUrl is not null)
                            WriteLine($"        → à télécharger manuellement : {c.InfoUrl}", ConsoleColor.DarkGray);
                    }
                    else if (c.HeldByRollout)
                    {
                        WriteLine($"    ⏳ {c.Title} {c.Version} [{c.SourceLabel}]{devices}", ConsoleColor.DarkGray);
                        WriteLine($"       retenu par la diffusion par anneaux : {c.BlockedReason}", ConsoleColor.DarkYellow);
                    }
                    else
                    {
                        WriteLine($"    ⛔ {c.Title} {c.Version} [{c.SourceLabel}]{devices}", ConsoleColor.DarkGray);
                        WriteLine($"       bloqué par la politique : {c.BlockedReason}", ConsoleColor.DarkYellow);
                    }
                }
            }
            var held = scan.Candidates.Count(c => c.HeldByRollout);
            var blocked = scan.Candidates.Count(c => c.BlockedReason is not null) - held;
            if (blocked > 0) WriteLine($"  {blocked} pilote(s) bloqué(s) par la politique : jamais installé(s).", ConsoleColor.DarkYellow);
            if (held > 0) WriteLine($"  {held} pilote(s) retenu(s) par la diffusion par anneaux : installé(s) quand l'anneau du poste y aura droit.", ConsoleColor.DarkYellow);
        }
        var hidden = scan.PolicyDecisions.Count(d => d.Decision.Verdict == Core.Policy.PolicyVerdict.Hidden);
        if (hidden > 0) WriteLine($"  {hidden} autre(s) pilote(s) masqué(s) par la politique (détail : spokapilote policy test).", ConsoleColor.DarkGray);
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
        if (a.Value("output-dir") is { } outDir)
        {
            // Dépôt sur un partage de parc : <POSTE>.json (consolidable avec « fleet-report ») + <POSTE>.html.
            try
            {
                Directory.CreateDirectory(outDir);
                var baseName = Path.Combine(outDir, scan.System.ComputerName);
                File.WriteAllText(baseName + ".json", ReportWriter.ToJson(scan, outcomes));
                File.WriteAllText(baseName + ".html", ReportWriter.ToHtml(scan, outcomes));
                WriteLine($"Rapports déposés : {baseName}.json / .html", ConsoleColor.Gray);
            }
            catch (Exception ex)
            {
                WriteLine($"Dépôt des rapports impossible dans {outDir} : {ex.Message}", ConsoleColor.Yellow);
            }
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
                case "gpu": set.UnionWith(new[] { DriverSourceKind.Nvidia, DriverSourceKind.Intel, DriverSourceKind.Amd }); break;
                case "nvidia": set.Add(DriverSourceKind.Nvidia); break;
                case "intel": set.Add(DriverSourceKind.Intel); break;
                case "amd": set.Add(DriverSourceKind.Amd); break;
                default: throw new ArgumentException($"Source inconnue : {s} (valeurs : wu, catalog, oem, dell, hp, lenovo, gpu, nvidia, intel, amd, repo)");
            }
        }
        return set;
    }

    // ------------------------------------------------------------------ dépôt

    private static async Task<int> ExportAsync(Args a, CancellationToken ct)
    {
        var target = a.Positional(0) ?? AppPaths.DefaultRepository ?? throw new ArgumentException("Usage : spokapilote export <dossier-du-dépôt>");
        Banner();
        WriteLine("Export des pilotes de ce poste de référence...", ConsoleColor.Gray);
        var (ok, _, msg) = await DriverExporter.ExportAsync(target, ct);
        WriteLine(msg, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitError;
    }

    private static async Task<int> ImportAsync(Args a, CancellationToken ct)
    {
        var source = a.Positional(0);
        var repo = a.Positional(1) ?? LoadSettings(a).EffectiveRepositoryPaths().FirstOrDefault();
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

        if (sub is "export-profile" or "deploy" or "remove")
            return await PrinterProfileAsync(sub, a, settings, ct);

        return Usage("printers");
    }

    // ------------------------------------------------------------------ pack hors ligne, historique, parc, mise à jour

    private static async Task<int> PackAsync(Args a, CancellationToken ct)
    {
        var source = a.Positional(0);
        var output = a.Positional(1);
        if (source is null || output is null)
        {
            WriteLine("Usage : spokapilote pack <rapport.json | ce-poste> <dossier-du-pack> [--all]", ConsoleColor.Yellow);
            WriteLine("  1. Sur le poste sans réseau : spokapilote scan --json E:\\poste.json", ConsoleColor.Gray);
            WriteLine("  2. Sur un poste connecté    : spokapilote pack E:\\poste.json E:\\pack", ConsoleColor.Gray);
            WriteLine("  3. Sur le poste sans réseau : spokapilote install --repo E:\\pack --yes", ConsoleColor.Gray);
            return ExitUsage;
        }
        Banner();
        SystemInfo system;
        List<DeviceInfo> devices;
        if (source is "ce-poste" or "this" or ".")
        {
            system = Core.Inventory.SystemInfoProvider.Collect();
            devices = Core.Inventory.DeviceInventory.Collect();
        }
        else
        {
            var report = ScanReport.Load(source);
            system = report.System;
            devices = report.Devices;
        }
        WriteLine($"Poste cible : {system.ComputerName} — {system.Manufacturer} {system.DisplayModel}, {system.OsCaption} {system.OsDisplayVersion} ({system.Architecture})", ConsoleColor.White);
        WriteLine($"Périphériques en erreur : {devices.Count(d => d.IsPresent && d.HasProblem)}{(a.Flag("all") ? " (+ mises à jour de tout le matériel)" : "")}", ConsoleColor.White);

        var result = await OfflinePackBuilder.BuildAsync(system, devices, output, a.Flag("all"), LoadSettings(a),
            new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        WriteLine($"\n{result.Summary}", result.Drivers.Count > 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        foreach (var d in result.Drivers) WriteLine($"  ✓ {d.Title} {d.Version} → {string.Join(", ", d.Devices)}", ConsoleColor.Gray);
        foreach (var m in result.Missing) WriteLine($"  ? {m}", ConsoleColor.DarkYellow);
        WriteLine($"\nSur le poste cible : spokapilote install --repo \"{Path.GetFullPath(output)}\" --yes", ConsoleColor.Cyan);
        return result.Drivers.Count > 0 ? ExitOk : ExitError;
    }

    private static int History()
    {
        var list = InterventionHistory.List();
        if (list.Count == 0)
        {
            WriteLine("Aucune intervention enregistrée sur ce poste.", ConsoleColor.Gray);
            return ExitOk;
        }
        foreach (var i in list)
        {
            WriteLine($"{i.Id}  {i.Summary}", i.RolledBackAt is null ? ConsoleColor.White : ConsoleColor.DarkGray);
            foreach (var item in i.Items) WriteLine($"    {item.Status,-13} {item.Title} {item.Version}", ConsoleColor.Gray);
            if (i.AddedDriverPackages.Count > 0) WriteLine($"    paquets ajoutés : {string.Join(", ", i.AddedDriverPackages)}", ConsoleColor.DarkGray);
            if (i.RemovedDriverPackages.Count > 0) WriteLine($"    paquets retirés (non annulable{(i.BackupFolder is null ? "" : ", sauf restauration de la sauvegarde")}) : {string.Join(", ", i.RemovedDriverPackages)}", ConsoleColor.DarkGray);
            if (i.BackupFolder is not null) WriteLine($"    sauvegarde : {i.BackupFolder}", ConsoleColor.DarkGray);
        }
        WriteLine("\nAnnuler une intervention : spokapilote rollback <id|last>", ConsoleColor.Cyan);
        return ExitOk;
    }

    private static async Task<int> RollbackAsync(Args a, CancellationToken ct)
    {
        var intervention = InterventionHistory.Find(a.Positional(0) ?? "last");
        if (intervention is null)
        {
            WriteLine("Aucune intervention annulable trouvée (voir « spokapilote history »).", ConsoleColor.Yellow);
            return ExitError;
        }
        WriteLine($"Intervention {intervention.Id} : {intervention.Summary}", ConsoleColor.White);
        WriteLine($"Paquets qui seront retirés : {string.Join(", ", intervention.AddedDriverPackages)}", ConsoleColor.Gray);
        if (!a.Flag("yes") && !Confirm("Annuler cette intervention ?")) return ExitOk;
        var (ok, message) = await InterventionHistory.RollbackAsync(intervention, new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitPartial;
    }

    private static async Task<int> RestoreAsync(Args a, CancellationToken ct)
    {
        var folder = a.Positional(0);
        if (folder is null)
        {
            WriteLine("Sauvegardes disponibles :", ConsoleColor.White);
            foreach (var b in InterventionHistory.Backups()) WriteLine($"  {b}", ConsoleColor.Gray);
            WriteLine("Usage : spokapilote restore <dossier-de-sauvegarde>", ConsoleColor.Cyan);
            return ExitUsage;
        }
        var (ok, message) = await InterventionHistory.RestoreBackupAsync(folder, ct);
        WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
        return ok ? ExitOk : ExitError;
    }

    private static int FleetReportCommand(Args a)
    {
        var folder = a.Positional(0) ?? throw new ArgumentException("Usage : spokapilote fleet-report <dossier-des-rapports> [--out parc.html]");
        var machines = FleetReport.Load(folder);
        if (machines.Count == 0)
        {
            WriteLine($"Aucun rapport SpokaPilote dans {folder}.", ConsoleColor.Yellow);
            return ExitError;
        }
        var output = a.Value("out") ?? Path.Combine(folder, "parc.html");
        File.WriteAllText(output, FleetReport.ToHtml(machines, folder));
        WriteLine($"{machines.Count} poste(s), {machines.Count(m => !m.Healthy)} à traiter → {Path.GetFullPath(output)}", ConsoleColor.Green);
        foreach (var m in machines.Where(m => !m.Healthy))
            WriteLine($"  ⚠ {m.Name} ({m.Model}) : {m.Problems} en erreur, {m.Unresolved} sans solution, {m.Failed} échec(s)", ConsoleColor.Yellow);
        return ExitOk;
    }

    private static async Task<int> SelfUpdateAsync(Args a, CancellationToken ct)
    {
        var current = UpdateChecker.CurrentVersion;
        WriteLine($"Version {(AppPaths.IsPortable ? "portable" : "installée")} : {current.ToString(3)}", ConsoleColor.White);
        var update = await UpdateChecker.CheckAsync(current, ct);
        if (update is null)
        {
            WriteLine("SpokaPilote est à jour.", ConsoleColor.Green);
            return ExitOk;
        }
        WriteLine($"Nouvelle version disponible : {update.Version.ToString(3)} ({update.ReleaseUrl})", ConsoleColor.Cyan);
        if (AppPaths.IsPortable) return await PortableUpdateAsync(a, update, ct);
        if (!a.Flag("yes") && !Confirm("Installer la mise à jour ?")) return ExitOk;
        var file = await UpdateChecker.DownloadAsync(update, null, ct);
        WriteLine("Installeur téléchargé et vérifié (taille + SHA-256). Installation silencieuse lancée.", ConsoleColor.Green);
        UpdateChecker.LaunchInstaller(file, relaunch: false);
        return ExitOk;
    }

    /// <summary>
    /// Mode portable : aucun installeur n'est lancé. Avec accord (ou --yes), l'archive portable est téléchargée sur la
    /// clé et vérifiée (taille + SHA-256 publiés par GitHub) ; le technicien l'extrait ensuite lui-même.
    /// </summary>
    private static async Task<int> PortableUpdateAsync(Args a, UpdateInfo update, CancellationToken ct)
    {
        WriteLine($"Mode portable : téléchargez la nouvelle version ({update.ReleaseUrl}).", ConsoleColor.Yellow);
        if (update.PortableUrl is not null) WriteLine($"  Archive : {update.PortableUrl}", ConsoleColor.Gray);
        if (!a.Flag("yes") && !Confirm("Télécharger et vérifier l'archive portable sur la clé ?")) return ExitOk;
        try
        {
            var file = await UpdateChecker.DownloadPortableAsync(update, AppPaths.Updates, null, ct);
            WriteLine($"Archive téléchargée et vérifiée (taille + SHA-256) : {file}", ConsoleColor.Green);
            WriteLine($"Fermez SpokaPilote puis extrayez-la à la racine de la clé, par-dessus l'ancienne version : " +
                      $"les dossiers « {AppPaths.PortableDataFolderName} » et « {AppPaths.PortableRepositoryFolderName} » sont conservés.", ConsoleColor.Gray);
            return ExitOk;
        }
        catch (UpdateIntegrityException ex)
        {
            WriteLine(ex.Message, ConsoleColor.Red);
            return ExitError;
        }
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
                if (prop.Name == nameof(Settings.UseConsoleCache) && !Core.Online.ConsoleCache.IsValidMode(value))
                    throw new ArgumentException("UseConsoleCache : auto (défaut), on ou off.");
                if (prop.Name == nameof(Settings.UseConsoleCache)) value = value.Trim().ToLowerInvariant();
                object converted = prop.PropertyType == typeof(bool) ? bool.Parse(value)
                    : prop.PropertyType == typeof(int) ? int.Parse(value)
                    : prop.PropertyType == typeof(string) ? value
                    : throw new ArgumentException($"La clé {key} ne se règle pas avec « set ».");
                prop.SetValue(settings, converted);
                settings.Save();
                break;
        }
        WriteLine(AppPaths.Describe(), AppPaths.IsPortable ? ConsoleColor.Cyan : ConsoleColor.Gray);
        if (AppPaths.Layout.Mode == DataLocationMode.Portable)
            WriteLine($"Données de ce poste (journaux, historique, sauvegardes) : {AppPaths.MachineRoot}", ConsoleColor.Gray);
        if (AppPaths.IsPortable)
            WriteLine(AppPaths.DefaultRepository is { } depot
                ? $"Dépôt de la clé (utilisé automatiquement) : {depot}"
                : $"Dépôt de la clé : absent (créez {Path.Combine(AppPaths.PortableHome!, AppPaths.PortableRepositoryFolderName)} pour l'utiliser)", ConsoleColor.Gray);
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
        if (a.Flag("no-gpu")) settings.UseGpuVendorSources = false;
        if (a.Value("nvidia-branch") is { } branch) settings.NvidiaBranch = branch;
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

    /// <summary>Aide complète. Code 0 si demandée (« help », « --help »), 64 si la commande est inconnue.</summary>
    private static int Help(string? unknownCommand = null)
    {
        if (unknownCommand is not null) WriteLine($"Commande inconnue : {unknownCommand}", ConsoleColor.Red);
        Console.WriteLine(HelpText);
        return unknownCommand is null ? ExitOk : ExitUsage;
    }

    // Alias français → nom de la commande tel qu'il apparaît dans l'aide.
    private static readonly Dictionary<string, string> HelpAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["update"] = "install", ["driver-pack"] = "driverpack", ["historique"] = "history", ["annuler"] = "rollback",
        ["restaurer"] = "restore", ["parc"] = "fleet-report", ["mise-a-jour"] = "self-update", ["politique"] = "policy",
        ["planification"] = "schedule", ["serveur"] = "server", ["sante"] = "health", ["santé"] = "health",
        ["peripherique"] = "device", ["périphérique"] = "device", ["driver-store"] = "driverstore", ["magasin"] = "driverstore",
    };

    /// <summary>
    /// Aide d'une seule commande (« spokapilote scan --help », « spokapilote help printers ») : les rubriques de l'aide
    /// qui la décrivent, plutôt que de lancer la commande.
    /// </summary>
    private static int HelpFor(string command)
    {
        var name = HelpAliases.GetValueOrDefault(command, command).ToLowerInvariant();
        var blocks = HelpText.Replace("\r\n", "\n").Split("\n\n");
        var matches = blocks.Where(b => b.Split('\n').Any(l =>
            l.TrimStart().StartsWith($"spokapilote {name} ", StringComparison.OrdinalIgnoreCase) ||
            l.Trim().Equals($"spokapilote {name}", StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0) return Help(command);
        Console.WriteLine(string.Join("\n\n", matches));
        Console.WriteLine();
        Console.WriteLine("Codes de sortie : 0 OK · 1 erreur · 2 échec partiel · 3010 redémarrage requis   (aide complète : spokapilote help)");
        return ExitOk;
    }

    /// <summary>Sous-commande invalide : aide de la commande, code 64 pour les scripts.</summary>
    private static int Usage(string command)
    {
        HelpFor(command);
        return ExitUsage;
    }

    private static string HelpText => """
            SpokaPilote — installation de pilotes depuis les sources officielles (Microsoft, Dell, HP, Lenovo, NVIDIA, dépôt interne)

            ANALYSE ET INSTALLATION
              spokapilote scan [--deep] [--html rapport.html] [--json rapport.json]
              spokapilote install [--missing] [--include-optional] [--include-firmware] [--deep]
                                  [--dry-run] [--yes] [--no-restore-point] [--no-backup] [--report rapport.html]
                --deep               interroge aussi le catalogue Microsoft pour les composants qui fonctionnent
                --missing            n'installe que les pilotes manquants (périphériques en erreur)
                --only <texte>       n'installe que les pilotes dont le titre ou le périphérique contient ce texte
                --exclude <texte>    exclut ces pilotes (ex. --exclude NVIDIA) ; options répétables
                --sources wu,catalog,oem,gpu,repo   limite les sources interrogées
                                     (aussi : dell, hp, lenovo, nvidia, intel, amd ; gpu = nvidia,intel,amd)
                --no-gpu             n'interroge pas les sites des fabricants de cartes graphiques
                --nvidia-branch studio|gameready   branche des pilotes NVIDIA GeForce (défaut : studio)
                --repo <chemin>      ajoute un dépôt local/réseau pour cette exécution (répétable)
                --output-dir <dir>   dépose <POSTE>.json et <POSTE>.html (ex. \\srv\rapports) pour le rapport de parc
                --policy <fichier|url>  applique cette politique de pilotes pour cette exécution (essai avant déploiement)
                --no-upload          n'envoie pas le rapport à la console centrale (si ServerUrl est configuré)

            POLITIQUE DE PILOTES (approbation / liste noire, voir docs/POLITIQUE.md)
              spokapilote policy show                      politique en vigueur, origine et règles
              spokapilote policy check <fichier|url>       valide un driver-policy.json (numéros des règles fautives)
              spokapilote policy test [--policy <fichier>] analyse ce poste : ce qui serait bloqué / autorisé et pourquoi
              spokapilote policy init <fichier> [--force]  crée un modèle commenté

            BOÎTE À OUTILS DU TECHNICIEN (voir docs/OUTILS-TECHNICIEN.md)
              spokapilote health [--quick] [--json [fichier]] [--html rapport.html]
                                   note A-E et constats : pilotes dangereux installés, manquants, en erreur,
                                   non signés, anciens, mises à jour (--json seul : JSON sur la sortie standard),
                                   état du BIOS comparé au catalogue Dell / HP / Lenovo (information, rien n'est
                                   installé : le BIOS ne s'installe qu'avec « install --include-firmware »)
              spokapilote device list [--problems] [--all]      périphériques numérotés (en erreur d'abord)
              spokapilote device info <n°|id>                   fiche, diagnostic et actions possibles
              spokapilote device repair <n°|id> [--action restart|enable|reinstall|rescan] [--dry-run] [--yes]
                                   [--confirm-critical]   réparation guidée (pnputil), état contrôlé après l'action ;
                                   périphérique critique : confirmation explicite obligatoire (--yes ne suffit pas)
              spokapilote driverstore list [--all]              paquets tiers du magasin, utilisés ou non, taille
              spokapilote driverstore clean [--dry-run] [--yes] [--backup]
                                   supprime les anciennes versions inutilisées et remplacées (aperçu par défaut)

            HISTORIQUE ET RETOUR ARRIÈRE
              spokapilote history                          interventions passées sur ce poste
              spokapilote rollback [id|last] [--yes]       annule une intervention (retour aux pilotes précédents)
              spokapilote restore [dossier-de-sauvegarde]  réinstalle une sauvegarde de pilotes

            DÉPÔT DE PILOTES ET POSTES SANS RÉSEAU
              spokapilote export <\\serveur\pilotes>       exporte les pilotes de ce poste de référence
              spokapilote import <archive.zip|dossier> [dépôt]
              spokapilote config add-repo <\\serveur\pilotes>
              spokapilote pack <rapport.json|ce-poste> <dossier> [--all]
                                   prépare sur un poste connecté les pilotes d'un poste sans réseau

            PACKS DE PILOTES OFFICIELS (Dell, HP, Lenovo, Microsoft Surface — déploiement SCCM/MDT)
              spokapilote driverpack list [--model "Latitude 7420"] [--vendor dell|hp|lenovo|surface] [--os win11|win10]
                                          [--arch x64|arm64] [--all]
                                   sans --model : le pack du poste courant ; --all : toutes les versions
              spokapilote driverpack download <dépôt> [--model ...] [--os ...] [--yes] [--force] [--keep-download]
                                   télécharge, vérifie (empreinte, signature) et extrait le pack dans
                                   <dépôt>\<Fabricant>\<Modèle>\<WinXX-arch>, prêt pour « install --repo »
                                   Surface : MSI extrait sans installation (msiexec /a), firmwares UEFI
                                   installés seulement avec « install --include-firmware »

            IMAGES WINDOWS (DISM, console administrateur) — travaillez sur une copie de l'image
              spokapilote image info <install.wim> [--index n]       éditions, architecture, version
              spokapilote image inject --wim <install.wim> --index <n|1,3|all> --repo <dossier> [--no-firmware] [--dry-run] [--yes]
              spokapilote image inject --offline D:\ --repo <dossier> [--no-firmware] [--dry-run] [--yes]
                                   n'injecte que les INF signés et compatibles avec l'architecture de l'image ;
                                   --no-firmware écarte les firmwares UEFI (packs Surface)
              spokapilote image cleanup                    nettoie les montages orphelins (après une coupure)

            PARC
              spokapilote fleet-report <\\srv\rapports> [--out parc.html]   consolide les rapports de tous les postes
              spokapilote server status                    teste la connexion à la console centrale (ServerUrl, ServerToken)
              spokapilote server upload <rapport.json|dossier>   envoie des rapports existants à la console centrale
              spokapilote self-update [--yes]              met SpokaPilote à jour (Release GitHub vérifiée)

            IMPRIMANTES
              spokapilote printers discover [--range 192.168.1.0/24] [--drivers] [--json imprimantes.json]
              spokapilote printers find-driver "KYOCERA TASKalfa 3253ci" [--verify]
                --verify             télécharge et valide le paquet (signature, modèle exact) sans l'installer
              spokapilote printers install <ip> [--name "Accueil RDC"] [--driver "nom exact"] [--ipp]
                                           [--location "..."] [--default] [--share] [--yes]
              spokapilote printers export-profile <profil.json>   exporte les imprimantes réseau de ce poste
              spokapilote printers deploy [profil.json | https://... ] [--dry-run] [--yes]
                                   applique un profil (sans fichier : console centrale ServerUrl) ;
                                   idempotent : créée / à jour / mise à jour / échec
              spokapilote printers remove <nom> [--yes]    retire la file d'impression et son port s'il ne sert plus

            MAINTENANCE PLANIFIÉE (tâche \SpokaPilote\Maintenance, compte SYSTEM)
              spokapilote schedule enable [--weekly lundi | --daily | --at-startup] [--time 12:30]
                                          [--mode missing|updates|scan] [--output-dir \\srv\rapports]
                                          [--printers profil.json|https://...|serveur] [--random-delay 30]
                                          [--startup-delay 10] [--max-duration 120] [--on-battery]
              spokapilote schedule status | run-now | disable
              spokapilote maintenance --mode missing|updates|scan [...]   exécution immédiate (celle de la tâche)

            ACTIONS À DISTANCE (tâche \SpokaPilote\Agent, compte SYSTEM ; sans console configurée, ne fait rien)
              spokapilote agent enable [--every 15]        interroge la console toutes les N minutes (5 à 1440)
              spokapilote agent run                        récupère et exécute maintenant les tâches demandées depuis la console
                                   (analyse, installation, profil d'imprimantes, annulation, mise à jour — liste fermée)
              spokapilote agent status | run-now | disable

            CONFIGURATION
              spokapilote config [show | set <clé> <valeur> | add-repo <chemin> | remove-repo <chemin>]
                                   « show » indique aussi l'emplacement des données (installé ou mode portable)
              spokapilote config set UseConsoleCache auto|on|off   cache de pilotes de la console centrale (4.0) ;
                                   « server status » en donne l'état (taille, taux de réussite)

            MODE PORTABLE (clé USB, voir docs/MODE-PORTABLE.md)
              Archive SpokaPilote-Portable-<version>-<arch>.zip : données dans <clé>\SpokaPilote\Data (un dossier par
              poste pour journaux, historique et sauvegardes), dépôt <clé>\SpokaPilote\Depot utilisé automatiquement ;
              pas de tâche planifiée ; « self-update » télécharge l'archive portable vérifiée au lieu de l'installeur.

            Codes de sortie : 0 OK · 1 erreur · 2 échec partiel · 3010 redémarrage requis
            Journaux : %ProgramData%\SpokaPilote\Logs (mode portable : Data\Postes\<POSTE>\Logs sur la clé)
            """;
}

