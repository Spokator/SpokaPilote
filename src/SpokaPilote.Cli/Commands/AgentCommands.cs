using System.Globalization;
using System.Text;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Cli;

/// <summary>
/// Agent des tâches à distance : agent run | enable | disable | status | run-now. L'agent récupère les tâches demandées
/// depuis la console centrale et les exécute avec la logique habituelle (analyse, installation avec point de restauration
/// et sauvegarde, profil d'imprimantes, annulation, mise à jour), puis renvoie le résultat et le rapport.
/// </summary>
internal static partial class Program
{
    /// <summary>Dernier rapport envoyé à la console par UploadToServerAsync (pour le relier à la tâche qui l'a produit).</summary>
    private static ServerUploadResult? _lastUpload;
    /// <summary>Erreur du dernier envoi de rapport (null si réussi ou non tenté).</summary>
    private static string? _lastUploadError;

    private static string AgentStateFile => Path.Combine(AppPaths.Ensure(AppPaths.MachineRoot), "agent-state.json");

    private static async Task<int> AgentAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0) ?? "status";
        switch (sub)
        {
            case "run" or "executer":
                return await AgentRunAsync(a, ct);
            case "enable" or "activer":
            {
                if (AppPaths.IsPortable)
                {
                    WriteLine(MaintenanceScheduler.PortableRefusal, ConsoleColor.Red);
                    return ExitError;
                }
                Banner();
                var every = AgentScheduler.DefaultIntervalMinutes;
                if (a.Value("every") is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out every) ||
                                                     every is < AgentScheduler.MinIntervalMinutes or > AgentScheduler.MaxIntervalMinutes))
                {
                    WriteLine($"--every : nombre de minutes de {AgentScheduler.MinIntervalMinutes} à {AgentScheduler.MaxIntervalMinutes}.", ConsoleColor.Red);
                    return ExitUsage;
                }
                var settings = LoadSettings(a);
                if (!ServerClient.IsConfigured(settings))
                    WriteLine("Attention : aucune console centrale configurée (ServerUrl) : l'agent ne fera rien tant qu'elle ne l'est pas.", ConsoleColor.Yellow);
                var exe = Environment.ProcessPath ?? "";
                WriteLine($"Tâche       : {AgentScheduler.TaskName} (compte SYSTEM, privilèges maximum)", ConsoleColor.White);
                WriteLine($"Fréquence   : toutes les {every} min (+ jusqu'à 1 min aléatoire), y compris sur batterie", ConsoleColor.White);
                WriteLine($"Commande    : \"{exe}\" {MaintenanceScheduler.JoinArguments(AgentScheduler.Arguments)}", ConsoleColor.Gray);
                var (ok, message) = await AgentScheduler.EnableAsync(every, exe, ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "disable" or "desactiver" or "remove":
            {
                var (ok, message) = await AgentScheduler.DisableAsync(ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "run-now":
            {
                var (ok, message) = await AgentScheduler.RunNowAsync(ct);
                WriteLine(message, ok ? ConsoleColor.Green : ConsoleColor.Red);
                return ok ? ExitOk : ExitError;
            }
            case "status" or "etat":
                return await AgentStatusAsync(a, ct);
            default:
                WriteLine("Usage : spokapilote agent run | enable [--every 15] | disable | status | run-now", ConsoleColor.Yellow);
                return ExitUsage;
        }
    }

    private static async Task<int> AgentStatusAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        WriteLine($"Console centrale   : {(ServerClient.IsConfigured(settings) ? settings.ServerUrl : "non configurée (l'agent ne fait rien)")}",
            ServerClient.IsConfigured(settings) ? ConsoleColor.White : ConsoleColor.Yellow);
        WriteLine($"Identifiant poste  : {SystemInfoProvider.ReadMachineId() ?? "illisible"} ({Environment.MachineName})", ConsoleColor.Gray);
        var s = await AgentScheduler.StatusAsync(ct);
        if (!s.Exists)
        {
            WriteLine($"Tâche planifiée    : aucune ({AgentScheduler.TaskName}){(Elevation.IsAdministrator() ? "" : " — ou lecture refusée : essayez en administrateur")}", ConsoleColor.Gray);
            WriteLine("Activer : spokapilote agent enable --every 15   (console administrateur)", ConsoleColor.Cyan);
        }
        else
        {
            var culture = CultureInfo.GetCultureInfo("fr-FR");
            string Date(DateTime? d) => d?.ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm", culture) ?? "—";
            WriteLine($"Tâche planifiée    : {AgentScheduler.TaskName} ({s.State ?? "état inconnu"}) — {s.TriggerSummary}", ConsoleColor.White);
            WriteLine($"Commande           : \"{s.Command}\" {s.Arguments}", ConsoleColor.Gray);
            WriteLine($"Prochaine exécution: {Date(s.NextRun)}", ConsoleColor.White);
            WriteLine($"Dernière exécution : {(s.LastRun is null ? "jamais" : $"{Date(s.LastRun)} — {MaintenanceScheduler.ResultLabel(s.LastResult)}")}",
                s.LastResult is 0 or 0x41303 or null ? ConsoleColor.White : ConsoleColor.Yellow);
        }
        try
        {
            if (File.Exists(AgentStateFile))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(AgentStateFile, ct));
                var r = doc.RootElement;
                WriteLine($"Dernier contact    : {r.GetProperty("at").GetString()} — {r.GetProperty("summary").GetString()}", ConsoleColor.Gray);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // état facultatif
        }
        return ExitOk;
    }

    /// <summary>« spokapilote agent run » : ce que lance la tâche planifiée Agent (utilisable aussi à la main).</summary>
    private static async Task<int> AgentRunAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        if (!ServerClient.IsConfigured(settings))
        {
            WriteLine("Aucune console centrale configurée sur ce poste (ServerUrl) : rien à faire.", ConsoleColor.Gray);
            return ExitOk;
        }

        // Une seule exécution à la fois (tâche planifiée + lancement manuel).
        using var mutex = TryAcquireAgentMutex();
        if (mutex is null)
        {
            WriteLine("Un agent SpokaPilote est déjà en cours d'exécution sur ce poste.", ConsoleColor.Gray);
            return ExitOk;
        }

        var machineId = SystemInfoProvider.ReadMachineId();
        if (machineId is null)
        {
            WriteLine("Identifiant du poste (MachineGuid) illisible : l'agent ne peut pas s'identifier auprès de la console.", ConsoleColor.Red);
            return ExitError;
        }
        var name = Environment.MachineName;
        ServerClient client;
        AgentJobBatch batch;
        try
        {
            client = new ServerClient(settings);
            batch = await client.GetPendingJobsAsync(machineId, name, ct);
        }
        catch (ServerException ex)
        {
            WriteLine($"Console injoignable ou refus : {ex.Message}", ConsoleColor.Yellow);
            SaveAgentState($"échec de l'interrogation : {ex.Message}");
            return ExitError;
        }

        if (!batch.Known)
        {
            WriteLine("Ce poste n'est pas encore connu de la console : lancez « spokapilote scan » pour envoyer un premier rapport.", ConsoleColor.Yellow);
            SaveAgentState("poste inconnu de la console (aucun rapport reçu)");
            return ExitOk;
        }
        if (batch.Jobs.Count == 0)
        {
            WriteLine("Aucune tâche en attente.", ConsoleColor.Gray);
            SaveAgentState("aucune tâche en attente");
            return ExitOk;
        }

        // Journal de l'exécution (seulement quand il y a des tâches : pas de fichier toutes les 15 minutes pour rien).
        var logPath = NewAgentLogPath();
        var original = Console.Out;
        using var logFile = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        Console.SetOut(new TeeWriter(original, logFile));
        var exit = ExitOk;
        var summary = new List<string>();
        try
        {
            WriteLine($"=== Agent SpokaPilote — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {name} — {batch.Jobs.Count} tâche(s) ===", ConsoleColor.Cyan);
            foreach (var job in batch.Jobs)
            {
                ct.ThrowIfCancellationRequested();
                WriteLine($"\n=== Tâche {job.Id} : {AgentJobTypes.Label(job.Type)}{(job.DryRun ? " (SIMULATION)" : "")} — demandée par {job.CreatedBy ?? "la console"} ===", ConsoleColor.Cyan);
                var capture = new StringWriter();
                var before = Console.Out;
                Console.SetOut(new TeeWriter(before, capture));
                AgentJobResult result;
                Func<Task>? afterResult = null;
                try
                {
                    (result, afterResult) = await ExecuteJobAsync(job, settings, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Agent : tâche {job.Id} en échec", ex);
                    result = new AgentJobResult(false, ExitError, $"Erreur : {ex.Message}");
                }
                finally
                {
                    Console.SetOut(before);
                }

                result = result with { Log = Tail(capture.ToString(), 60_000) };
                exit = Worst(exit, result.ExitCode);
                WriteLine($"→ {(result.Success ? "réussie" : "échouée")} : {result.Message}", result.Success ? ConsoleColor.Green : ConsoleColor.Yellow);
                summary.Add($"{job.Type} {(result.Success ? "réussie" : "échouée")}");
                try
                {
                    await client.SendJobResultAsync(job.Id, machineId, name, result, ct);
                }
                catch (ServerException ex)
                {
                    WriteLine($"⚠ Résultat non transmis à la console : {ex.Message}", ConsoleColor.Yellow);
                    Log.Info($"Agent : résultat de la tâche {job.Id} non transmis : {ex.Message}");
                }
                if (afterResult is not null) await afterResult();
            }
            WriteLine($"\n=== Fin de l'agent — code {exit} — {DateTime.Now:HH:mm:ss} ===", exit is ExitOk or ExitRebootRequired ? ConsoleColor.Green : ConsoleColor.Yellow);
        }
        finally
        {
            Console.SetOut(original);
        }
        SaveAgentState(string.Join(", ", summary));
        return exit;
    }

    /// <summary>
    /// Exécute une tâche de la liste blanche en réutilisant les commandes existantes. Renvoie le résultat et, pour la
    /// mise à jour de SpokaPilote, l'action à lancer APRÈS l'envoi du résultat (l'installeur ferme spokapilote).
    /// </summary>
    private static async Task<(AgentJobResult Result, Func<Task>? AfterResult)> ExecuteJobAsync(AgentJob job, Settings settings, CancellationToken ct)
    {
        if (!AgentJobTypes.IsAllowed(job.Type))
            return (new AgentJobResult(false, ExitUsage, $"Type de tâche « {job.Type} » refusé par ce poste (liste blanche) : mettez SpokaPilote à jour."), null);

        _lastUpload = null;
        _lastUploadError = null;
        var dry = job.DryRun ? new[] { "--dry-run" } : [];
        int code;
        switch (job.Type)
        {
            case AgentJobTypes.Scan:
                code = await ScanAsync(new Args(["scan"]), install: false, ct);
                return (ScanResultFor(job, code), null);

            case AgentJobTypes.InstallMissing:
                code = await ScanAsync(new Args(["install", "--missing", "--yes", .. dry]), install: true, ct);
                return (ScanResultFor(job, code), null);

            case AgentJobTypes.InstallUpdates:
                code = await ScanAsync(new Args(["install", "--yes", .. dry]), install: true, ct);
                return (ScanResultFor(job, code), null);

            case AgentJobTypes.DeployPrinters:
                code = await PrintersAsync(new Args(["printers", "deploy", "--yes", .. dry]), ct);
                return (new AgentJobResult(IsSuccess(code), code, $"Profil d'imprimantes{(job.DryRun ? " (simulation)" : "")} : {CodeLabel(code)}."), null);

            case AgentJobTypes.RollbackLast:
            {
                var intervention = InterventionHistory.Find("last");
                // Tâche distante : jamais au-delà de RollbackLastMaxAgeDays, ni pour une intervention aux paquets non vérifiables.
                if (intervention is not null &&
                    InterventionHistory.CheckAutomaticRollback(intervention, RollbackOptions.Last(Settings.Load()), DateTime.Now) is { } refusal)
                    return (new AgentJobResult(false, ExitError, refusal), null);
                if (job.DryRun)
                    return (intervention is null
                        ? new AgentJobResult(false, ExitError, "Simulation : aucune intervention annulable sur ce poste.")
                        : new AgentJobResult(true, ExitOk, $"Simulation : l'intervention {intervention.Id} ({intervention.Summary}) serait annulée ; " +
                                                          $"paquets retirés : {string.Join(", ", intervention.AddedDriverPackages)}."), null);
                code = await RollbackAsync(new Args(["rollback", "last", "--yes"]), ct);
                if (code != ExitOk)
                    return (new AgentJobResult(false, code, intervention is null ? "Aucune intervention annulable sur ce poste." : $"Annulation : {CodeLabel(code)}."), null);
                // Nouvelle analyse : la console affiche aussitôt l'état des pilotes après l'annulation.
                WriteLine("\nAnalyse après annulation...", ConsoleColor.Gray);
                await ScanAsync(new Args(["scan"]), install: false, ct);
                return (new AgentJobResult(true, ExitOk, $"Intervention {intervention!.Id} annulée ; nouvelle analyse envoyée.", _lastUpload?.ReportId), null);
            }

            case AgentJobTypes.SelfUpdate:
            {
                if (AppPaths.IsPortable)
                    return (new AgentJobResult(false, ExitError, "Mode portable : la mise à jour automatique n'est pas disponible (aucun installeur n'est lancé depuis une clé USB)."), null);
                var current = UpdateChecker.CurrentVersion;
                WriteLine($"Version installée : {current.ToString(3)}", ConsoleColor.White);
                var update = await UpdateChecker.CheckAsync(current, ct);
                if (update is null) return (new AgentJobResult(true, ExitOk, $"SpokaPilote est à jour ({current.ToString(3)})."), null);
                if (job.DryRun)
                    return (new AgentJobResult(true, ExitOk, $"Simulation : la version {update.Version.ToString(3)} serait installée (actuelle : {current.ToString(3)})."), null);
                var file = await UpdateChecker.DownloadAsync(update, null, ct);
                WriteLine("Installeur téléchargé et vérifié (taille + SHA-256).", ConsoleColor.Green);
                return (new AgentJobResult(true, ExitOk, $"Version {update.Version.ToString(3)} téléchargée et vérifiée : installation silencieuse lancée."),
                    () =>
                    {
                        UpdateChecker.LaunchInstaller(file, relaunch: false);
                        return Task.CompletedTask;
                    });
            }
        }
        return (new AgentJobResult(false, ExitUsage, $"Type de tâche non géré : {job.Type}."), null);
    }

    private static AgentJobResult ScanResultFor(AgentJob job, int code)
    {
        var what = AgentJobTypes.Label(job.Type);
        var text = new StringBuilder($"{char.ToUpperInvariant(what[0])}{what[1..]}{(job.DryRun ? " (simulation)" : "")} : {CodeLabel(code)}");
        if (_lastUpload is { } u) text.Append($" — {u.DriverProblems} pilote(s) en erreur, {u.Unresolved} sans solution");
        else if (_lastUploadError is { } e) text.Append($" — rapport non envoyé : {e}");
        return new AgentJobResult(IsSuccess(code), code, text.Append('.').ToString(), _lastUpload?.ReportId);
    }

    private static bool IsSuccess(int code) => code is ExitOk or ExitRebootRequired;

    private static string CodeLabel(int code) => code switch
    {
        ExitOk => "terminé",
        ExitRebootRequired => "terminé, redémarrage requis (3010)",
        ExitPartial => "échec partiel (2)",
        ExitUsage => "usage incorrect (64)",
        _ => $"erreur ({code})",
    };

    private static string Tail(string text, int max) => text.Length <= max ? text : "[…]\n" + text[^max..];

    private static Mutex? TryAcquireAgentMutex()
    {
        try
        {
            var mutex = new Mutex(false, @"Global\SpokaPilote.Agent");
            try
            {
                if (mutex.WaitOne(0)) return mutex;
            }
            catch (AbandonedMutexException)
            {
                return mutex; // exécution précédente interrompue : le verrou est récupéré
            }
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // verrou détenu par l'agent SYSTEM
        }
    }

    private static void SaveAgentState(string summary)
    {
        try
        {
            File.WriteAllText(AgentStateFile, JsonSerializer.Serialize(new
            {
                at = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                summary,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // état facultatif
        }
    }

    /// <summary>Journal d'agent horodaté (seulement quand des tâches sont exécutées), les 30 derniers conservés.</summary>
    private static string NewAgentLogPath()
    {
        var logs = AppPaths.Logs;
        try
        {
            foreach (var old in new DirectoryInfo(logs).GetFiles("agent-*.log").OrderByDescending(f => f.Name).Skip(29))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nettoyage facultatif
        }
        return Path.Combine(logs, $"agent-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}
