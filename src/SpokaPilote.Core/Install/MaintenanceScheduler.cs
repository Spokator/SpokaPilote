using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

public enum MaintenanceMode
{
    /// <summary>Pilotes manquants seulement (par défaut : le moins intrusif).</summary>
    Missing,
    /// <summary>Pilotes manquants + mises à jour.</summary>
    Updates,
    /// <summary>Analyse seule (rapport), aucune installation.</summary>
    Scan,
}

public enum MaintenanceTrigger { Weekly, Daily, AtStartup }

public sealed class MaintenanceOptions
{
    public MaintenanceTrigger Trigger { get; set; } = MaintenanceTrigger.Weekly;
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public TimeSpan Time { get; set; } = new(12, 0, 0);
    /// <summary>Délai après le démarrage (déclencheur « au démarrage »).</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Délai aléatoire ajouté par le Planificateur pour étaler la charge sur le parc.</summary>
    public TimeSpan RandomDelay { get; set; } = TimeSpan.FromMinutes(30);
    public bool RunOnBattery { get; set; }
    public TimeSpan ExecutionLimit { get; set; } = TimeSpan.FromHours(2);
    public MaintenanceMode Mode { get; set; } = MaintenanceMode.Missing;
    /// <summary>Dossier (souvent UNC) où déposer &lt;POSTE&gt;.json / .html pour le rapport de parc.</summary>
    public string? OutputDir { get; set; }
    /// <summary>Profil d'imprimantes à déployer : fichier, URL HTTPS ou « serveur » (console centrale).</summary>
    public string? PrinterProfile { get; set; }
}

public sealed record MaintenanceStatus(
    bool Exists,
    string? State,
    DateTime? LastRun,
    int? LastResult,
    DateTime? NextRun,
    int MissedRuns,
    string? Command,
    string? Arguments,
    string? TriggerSummary);

/// <summary>
/// Tâche planifiée Windows « \SpokaPilote\Maintenance » : exécutée en SYSTEM (privilèges maximum), elle lance
/// « spokapilote maintenance » selon le mode choisi. Créée par schtasks.exe /Create /XML.
/// </summary>
public static class MaintenanceScheduler
{
    public const string TaskFolder = @"\SpokaPilote";
    public const string TaskName = @"\SpokaPilote\Maintenance";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string SchTasks => ProcessRunner.SystemTool("schtasks.exe");

    // ------------------------------------------------------------------ arguments et XML (logique pure)

    public static string ModeName(MaintenanceMode mode) => mode switch
    {
        MaintenanceMode.Updates => "updates",
        MaintenanceMode.Scan => "scan",
        _ => "missing",
    };

    public static MaintenanceMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "missing" or "manquants" => MaintenanceMode.Missing,
        "updates" or "update" or "mises-a-jour" or "maj" => MaintenanceMode.Updates,
        "scan" or "analyse" => MaintenanceMode.Scan,
        _ => null,
    };

    public static DayOfWeek? ParseDay(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "lundi" or "lun" or "monday" or "mon" => DayOfWeek.Monday,
        "mardi" or "mar" or "tuesday" or "tue" => DayOfWeek.Tuesday,
        "mercredi" or "mer" or "wednesday" or "wed" => DayOfWeek.Wednesday,
        "jeudi" or "jeu" or "thursday" or "thu" => DayOfWeek.Thursday,
        "vendredi" or "ven" or "friday" or "fri" => DayOfWeek.Friday,
        "samedi" or "sam" or "saturday" or "sat" => DayOfWeek.Saturday,
        "dimanche" or "dim" or "sunday" or "sun" => DayOfWeek.Sunday,
        _ => null,
    };

    public static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Replace('h', ':').Replace('H', ':');
        if (v.EndsWith(':')) v += "00";
        return TimeSpan.TryParseExact(v, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;
    }

    /// <summary>Arguments de « spokapilote maintenance » correspondant aux options.</summary>
    public static IReadOnlyList<string> BuildArguments(MaintenanceOptions o)
    {
        var args = new List<string> { "maintenance", "--mode", ModeName(o.Mode) };
        if (!string.IsNullOrWhiteSpace(o.OutputDir)) { args.Add("--output-dir"); args.Add(o.OutputDir.Trim()); }
        if (!string.IsNullOrWhiteSpace(o.PrinterProfile)) { args.Add("--printers"); args.Add(o.PrinterProfile.Trim()); }
        return args;
    }

    /// <summary>
    /// Échappe un argument selon les règles de CommandLineToArgvW (celles de .NET) : guillemets si espace ou
    /// guillemet, barres obliques inverses doublées devant un guillemet et en fin d'argument entre guillemets.
    /// </summary>
    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string JoinArguments(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteArgument));

    private static string Duration(TimeSpan t) => XmlConvert.ToString(t);

    /// <summary>Définition XML complète de la tâche (schéma Task Scheduler 1.2, Windows 7 et ultérieur).</summary>
    public static string BuildTaskXml(MaintenanceOptions o, string executable, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        XElement trigger = o.Trigger switch
        {
            MaintenanceTrigger.AtStartup => new XElement(Ns + "BootTrigger",
                new XElement(Ns + "Enabled", "true"),
                // Pas de délai aléatoire au démarrage (schéma) : les postes démarrent déjà à des heures différentes ;
                // le délai fixe laisse le réseau s'établir.
                new XElement(Ns + "Delay", Duration(o.StartupDelay))),
            MaintenanceTrigger.Daily => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", (today + o.Time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                new XElement(Ns + "Enabled", "true"),
                o.RandomDelay > TimeSpan.Zero ? new XElement(Ns + "RandomDelay", Duration(o.RandomDelay)) : null,
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", "1"))),
            _ => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", (today + o.Time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                new XElement(Ns + "Enabled", "true"),
                o.RandomDelay > TimeSpan.Zero ? new XElement(Ns + "RandomDelay", Duration(o.RandomDelay)) : null,
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek", new XElement(Ns + o.Day.ToString())),
                    new XElement(Ns + "WeeksInterval", "1"))),
        };

        var description = $"Entretien automatique SpokaPilote : {ModeLabel(o.Mode)}" +
                          (o.PrinterProfile is null ? "" : ", déploiement du profil d'imprimantes") +
                          $". Journal : %ProgramData%\\SpokaPilote\\Logs.";

        return BuildSystemTaskXml(TaskName, description, trigger, o.RunOnBattery, o.ExecutionLimit, executable, BuildArguments(o));
    }

    /// <summary>Espace de noms XML des définitions de tâches (pour construire un déclencheur).</summary>
    public static XNamespace TaskNamespace => Ns;

    /// <summary>
    /// Définition XML d'une tâche SpokaPilote exécutée en SYSTEM (privilèges maximum, instance unique) : socle commun
    /// à la maintenance (\SpokaPilote\Maintenance) et à l'agent des tâches à distance (\SpokaPilote\Agent).
    /// </summary>
    public static string BuildSystemTaskXml(string taskName, string description, XElement trigger, bool runOnBattery,
        TimeSpan executionLimit, string executable, IEnumerable<string> arguments) =>
        BuildSystemTaskXml(taskName, description, new[] { trigger }, runOnBattery, executionLimit, executable, arguments);

    /// <summary>
    /// Même définition avec plusieurs déclencheurs (ex. au démarrage ET chaque jour) ; l'auteur est le produit en
    /// vigueur (<see cref="AppIdentity.ProductName"/>).
    /// </summary>
    public static string BuildSystemTaskXml(string taskName, string description, IEnumerable<XElement> triggers, bool runOnBattery,
        TimeSpan executionLimit, string executable, IEnumerable<string> arguments)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", AppIdentity.Current.ProductName),
                    new XElement(Ns + "Description", description),
                    new XElement(Ns + "URI", taskName)),
                new XElement(Ns + "Triggers", triggers),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", "S-1-5-18"), // SYSTEM, quel que soit la langue de Windows
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", runOnBattery ? "false" : "true"),
                    new XElement(Ns + "StopIfGoingOnBatteries", runOnBattery ? "false" : "true"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", Duration(executionLimit)),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", executable),
                        new XElement(Ns + "Arguments", JoinArguments(arguments)),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(executable) ?? "")))));

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf16StringWriter(sb), new XmlWriterSettings { Indent = true, Encoding = Encoding.Unicode }))
            doc.Save(writer);
        return sb.ToString();
    }

    private sealed class Utf16StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.Unicode;
    }

    public static string ModeLabel(MaintenanceMode mode) => mode switch
    {
        MaintenanceMode.Updates => "pilotes manquants et mises à jour",
        MaintenanceMode.Scan => "analyse seule",
        _ => "pilotes manquants",
    };

    public static string TriggerLabel(MaintenanceOptions o) => o.Trigger switch
    {
        MaintenanceTrigger.AtStartup => $"au démarrage (après {o.StartupDelay.TotalMinutes:0} min)",
        MaintenanceTrigger.Daily => $"chaque jour à {o.Time:hh\\:mm}{RandomLabel(o)}",
        _ => $"chaque {CultureInfo.GetCultureInfo("fr-FR").DateTimeFormat.GetDayName(o.Day)} à {o.Time:hh\\:mm}{RandomLabel(o)}",
    };

    private static string RandomLabel(MaintenanceOptions o) =>
        o.RandomDelay > TimeSpan.Zero ? $" (+ jusqu'à {o.RandomDelay.TotalMinutes:0} min aléatoires)" : "";

    /// <summary>Refus des tâches planifiées en mode portable (clé USB).</summary>
    public const string PortableRefusal =
        "Mode portable : les tâches planifiées (maintenance, agent) ne sont pas disponibles. Elles pointeraient vers la clé USB, " +
        "absente une fois le technicien parti. Installez SpokaPilote sur le poste pour planifier la maintenance.";

    /// <summary>
    /// La tâche tourne en SYSTEM : l'exécutable doit être la version installée, dans Program Files (non modifiable
    /// par un utilisateur standard). Un dossier temporaire ou de compilation est refusé, de même qu'une copie
    /// portable (fichier témoin SpokaPilote.portable à côté de l'exécutable).
    /// </summary>
    public static string? ValidateExecutable(string? path) => ValidateExecutable(path, "spokapilote.exe", "SpokaPilote", PortableRefusal);

    /// <summary>
    /// Même contrôle pour la ligne de commande d'un autre produit (<paramref name="expectedFileName"/>, ex.
    /// « spokaprint.exe ») : version installée dans Program Files obligatoire.
    /// </summary>
    public static string? ValidateExecutable(string? path, string expectedFileName, string productName, string? portableRefusal = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Chemin de l'exécutable inconnu.";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) { return $"Chemin de l'exécutable invalide : {ex.Message}"; }
        if (!Path.GetFileName(full).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
            return $"La tâche doit lancer {expectedFileName} (exécutable actuel : {full}).";
        if (Path.GetDirectoryName(full) is { } dir && AppPaths.IsPortableDirectory(dir))
            return portableRefusal ?? $"Copie portable : les tâches planifiées ne sont pas disponibles. Installez {productName} sur le poste.";

        static bool Under(string file, string? folder) =>
            !string.IsNullOrWhiteSpace(folder) &&
            file.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (Under(full, Path.GetTempPath()) || Under(full, Environment.GetEnvironmentVariable("TEMP")))
            return $"Exécutable dans un dossier temporaire ({full}) : installez {productName} dans Program Files.";
        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            return $"Exécutable de compilation ({full}) : installez {productName} dans Program Files.";
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (!programFiles.Any(pf => Under(full, pf)))
            return $"La tâche s'exécute en SYSTEM : l'exécutable doit être la version installée dans Program Files (actuel : {full}).";
        return null;
    }

    // ------------------------------------------------------------------ opérations système

    public static async Task<(bool Ok, string Message)> EnableAsync(MaintenanceOptions options, string executable, CancellationToken ct = default)
    {
        if (AppPaths.IsPortable) return (false, PortableRefusal);
        if (ValidateExecutable(executable) is { } error) return (false, error);
        if (!Elevation.IsAdministrator()) return (false, "La création de la tâche planifiée nécessite une console administrateur.");

        var (ok, message, file) = await RegisterTaskAsync(TaskName, BuildTaskXml(options, Path.GetFullPath(executable)), "maintenance-task.xml", ct).ConfigureAwait(false);
        if (!ok) return (false, message);
        Log.Info($"Tâche planifiée {TaskName} créée : {TriggerLabel(options)}, {ModeLabel(options.Mode)} ({JoinArguments(BuildArguments(options))}).");
        return (true, $"Tâche « {TaskName} » créée : {TriggerLabel(options)}, {ModeLabel(options.Mode)}. Définition : {file}");
    }

    /// <summary>Supprime la tâche, puis le dossier de tâches \SpokaPilote s'il est vide.</summary>
    public static Task<(bool Ok, string Message)> DisableAsync(CancellationToken ct = default) => DeleteTaskAsync(TaskName, ct);

    public static Task<(bool Ok, string Message)> RunNowAsync(CancellationToken ct = default) => RunNowAsync(TaskName, ct);

    /// <summary>Définition XML de la tâche (null si elle n'existe pas ou n'est pas lisible).</summary>
    public static Task<string?> QueryXmlAsync(CancellationToken ct = default) => QueryXmlAsync(TaskName, ct);

    /// <summary>État de la tâche de maintenance.</summary>
    public static Task<MaintenanceStatus> StatusAsync(CancellationToken ct = default) => StatusAsync(TaskName, ct);

    // ------------------------------------------------------------------ opérations communes à toutes les tâches \SpokaPilote\*
    // (publiques : un produit construit sur ce moteur gère ses propres tâches, dans son dossier du Planificateur)

    /// <summary>
    /// Enregistre (ou remplace) une tâche à partir de sa définition XML. schtasks lit la définition dans un dossier de
    /// travail neuf et privé (jamais un fichier existant, qu'un autre compte pourrait modifier entre l'écriture et la
    /// lecture : la tâche s'exécute en SYSTEM) ; une copie de référence est conservée dans %ProgramData%\SpokaPilote.
    /// </summary>
    public static async Task<(bool Ok, string Message, string File)> RegisterTaskAsync(string taskName, string xml, string fileName, CancellationToken ct)
    {
        var file = Path.Combine(AppPaths.Ensure(AppPaths.Root), fileName);
        ProcessResult r;
        using (var work = WorkDirectory.Create("task"))
        {
            var definition = Path.Combine(work.Path, AppPaths.SafeName(fileName));
            await File.WriteAllTextAsync(definition, xml, Encoding.Unicode, ct).ConfigureAwait(false);
            r = await ProcessRunner.RunAsync(SchTasks, $"/Create /TN \"{taskName}\" /XML \"{definition}\" /F", timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false);
        }
        try { DataProtection.WriteTextAtomically(file, xml, Encoding.Unicode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"Copie de la définition non conservée ({file}) : {ex.Message}"); }
        return r.ExitCode == 0 ? (true, "", file) : (false, $"schtasks a refusé la tâche (code {r.ExitCode}) : {r.Combined.Trim()}", file);
    }

    /// <summary>
    /// Supprime une tâche, puis son dossier de tâches (\SpokaPilote pour \SpokaPilote\Maintenance) s'il est vide.
    /// Le dossier est celui du nom de la tâche : un dossier de premier niveau, jamais la racine.
    /// </summary>
    public static async Task<(bool Ok, string Message)> DeleteTaskAsync(string taskName, CancellationToken ct)
    {
        if (!Elevation.IsAdministrator()) return (false, "La suppression de la tâche planifiée nécessite une console administrateur.");
        var exists = (await QueryXmlAsync(taskName, ct).ConfigureAwait(false)) is not null;
        if (exists)
        {
            var r = await ProcessRunner.RunAsync(SchTasks, $"/Delete /TN \"{taskName}\" /F", timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false);
            if (r.ExitCode != 0) return (false, $"schtasks n'a pas supprimé la tâche (code {r.ExitCode}) : {r.Combined.Trim()}");
        }
        var taskFolder = TaskFolderOf(taskName);
        var folderNote = "";
        if (taskFolder is not null)
        {
            // schtasks ne sait pas supprimer un dossier : API COM du Planificateur.
            var folder = ProcessRunner.PsQuote(taskFolder);
            var leaf = ProcessRunner.PsQuote(taskFolder.TrimStart('\\'));
            var script = "$s = New-Object -ComObject Schedule.Service; $s.Connect(); " +
                         $"try {{ $f = $s.GetFolder({folder}) }} catch {{ 'ABSENT'; exit 0 }}; " +
                         $"if ($f.GetTasks(1).Count -eq 0 -and $f.GetFolders(0).Count -eq 0) {{ $s.GetFolder('\\').DeleteFolder({leaf}, 0); 'DELETED' }} else {{ 'KEPT' }}";
            var del = await ProcessRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (del.StdOut.Contains("KEPT")) folderNote = $" (dossier {taskFolder} conservé : il contient d'autres tâches)";
        }
        Log.Info($"Tâche planifiée {taskName} supprimée.");
        return (true, exists ? $"Tâche « {taskName} » supprimée{folderNote}." : $"Aucune tâche « {taskName} » sur ce poste{folderNote}.");
    }

    /// <summary>Dossier de premier niveau d'une tâche (« \SpokaPilote\Maintenance » → « \SpokaPilote »), sinon null.</summary>
    public static string? TaskFolderOf(string taskName)
    {
        var parts = taskName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && taskName.StartsWith('\\') ? "\\" + parts[0] : null;
    }

    public static async Task<(bool Ok, string Message)> RunNowAsync(string taskName, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(SchTasks, $"/Run /TN \"{taskName}\"", timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false);
        return r.ExitCode == 0
            ? (true, $"Tâche « {taskName} » démarrée. Journal : {AppPaths.Logs}")
            : (false, $"Démarrage impossible (code {r.ExitCode}) : {r.Combined.Trim()}");
    }

    public static async Task<string?> QueryXmlAsync(string taskName, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(SchTasks, $"/Query /TN \"{taskName}\" /XML", timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false);
        return r.ExitCode == 0 && r.StdOut.Contains("<Task", StringComparison.Ordinal) ? r.StdOut : null;
    }

    /// <summary>
    /// État d'une tâche. La définition vient du XML (schtasks /Query /XML) et l'état d'exécution de
    /// Get-ScheduledTaskInfo (valeurs numériques et dates ISO : indépendant de la langue de Windows).
    /// </summary>
    public static async Task<MaintenanceStatus> StatusAsync(string taskName, CancellationToken ct)
    {
        var xml = await QueryXmlAsync(taskName, ct).ConfigureAwait(false);
        if (xml is null) return new MaintenanceStatus(false, null, null, null, null, 0, null, null, null);
        var (command, arguments, trigger) = ParseDefinition(xml);

        var cut = taskName.LastIndexOf('\\');
        var path = ProcessRunner.PsQuote(taskName[..(cut + 1)]);
        var leaf = ProcessRunner.PsQuote(taskName[(cut + 1)..]);
        var script = $"$t = Get-ScheduledTask -TaskPath {path} -TaskName {leaf}; $i = $t | Get-ScheduledTaskInfo; " +
                     "function D($d) { if ($d -and $d.Year -gt 2000) { $d.ToString('o') } else { $null } }; " +
                     "[pscustomobject]@{ State = [string]$t.State; LastRun = D $i.LastRunTime; LastResult = [int64]$i.LastTaskResult; " +
                     "NextRun = D $i.NextRunTime; Missed = [int]$i.NumberOfMissedRuns } | ConvertTo-Json -Compress";
        var r = await ProcessRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        string? state = null; DateTime? last = null, next = null; int? result = null; var missed = 0;
        if (r.ExitCode == 0)
        {
            try
            {
                using var json = JsonDocument.Parse(r.StdOut.Trim());
                var root = json.RootElement;
                state = root.GetProperty("State").GetString();
                last = ParseDate(root.GetProperty("LastRun"));
                next = ParseDate(root.GetProperty("NextRun"));
                result = unchecked((int)(uint)root.GetProperty("LastResult").GetInt64());
                missed = root.GetProperty("Missed").GetInt32();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Log.Debug($"État de la tâche illisible : {ex.Message}");
            }
        }
        return new MaintenanceStatus(true, state, last, result, next, missed, command, arguments, trigger);
    }
    private static DateTime? ParseDate(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>Extrait commande, arguments et résumé du déclencheur d'une définition XML de tâche.</summary>
    public static (string? Command, string? Arguments, string? Trigger) ParseDefinition(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml.TrimStart('﻿'));
            var exec = doc.Descendants(Ns + "Exec").FirstOrDefault();
            var triggers = doc.Descendants(Ns + "Triggers").Elements().Select(t =>
            {
                var start = DateTime.TryParse(t.Element(Ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : (DateTime?)null;
                var random = t.Element(Ns + "RandomDelay")?.Value;
                var randomText = random is null ? "" : $" (+ jusqu'à {XmlConvert.ToTimeSpan(random).TotalMinutes:0} min aléatoires)";
                return t.Name.LocalName switch
                {
                    "BootTrigger" => $"au démarrage (après {XmlConvert.ToTimeSpan(t.Element(Ns + "Delay")?.Value ?? "PT0S").TotalMinutes:0} min)",
                    "LogonTrigger" => $"à l'ouverture de session (après {XmlConvert.ToTimeSpan(t.Element(Ns + "Delay")?.Value ?? "PT0S").TotalMinutes:0} min)",
                    "CalendarTrigger" when t.Element(Ns + "ScheduleByWeek") is { } w =>
                        $"chaque {string.Join(", ", w.Element(Ns + "DaysOfWeek")?.Elements().Select(d => Enum.TryParse<DayOfWeek>(d.Name.LocalName, out var day) ? CultureInfo.GetCultureInfo("fr-FR").DateTimeFormat.GetDayName(day) : d.Name.LocalName) ?? Array.Empty<string>())} à {start:HH\\:mm}{randomText}",
                    "CalendarTrigger" => $"chaque jour à {start:HH\\:mm}{randomText}",
                    "TimeTrigger" when t.Element(Ns + "Repetition")?.Element(Ns + "Interval")?.Value is { } every =>
                        $"toutes les {XmlConvert.ToTimeSpan(every).TotalMinutes:0} min{randomText}",
                    var other => other,
                };
            });
            return (exec?.Element(Ns + "Command")?.Value, exec?.Element(Ns + "Arguments")?.Value, string.Join(" ; ", triggers));
        }
        catch (XmlException)
        {
            return (null, null, null);
        }
    }

    /// <summary>Libellé du code de dernier résultat (codes SpokaPilote et codes du Planificateur).</summary>
    public static string ResultLabel(int? code) => code switch
    {
        null => "inconnu",
        0 => "succès (0)",
        1 => "erreur (1)",
        2 => "échec partiel (2)",
        64 => "usage incorrect (64)",
        3010 => "succès, redémarrage requis (3010)",
        0x41300 => "prête à s'exécuter",
        0x41301 => "en cours d'exécution",
        0x41303 => "jamais exécutée",
        0x41306 => "interrompue",
        unchecked((int)0x8004131F) => "déjà en cours (instance unique)",
        unchecked((int)0x800710E0) => "refusée par une condition (batterie, etc.)",
        _ => $"code 0x{code.Value:X8}",
    };

    /// <summary>Journal de maintenance horodaté (un fichier par exécution, les 30 derniers conservés).</summary>
    public static string NewMaintenanceLogPath()
    {
        var logs = AppPaths.Logs;
        try
        {
            foreach (var old in new DirectoryInfo(logs).GetFiles("maintenance-*.log").OrderByDescending(f => f.Name).Skip(29))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nettoyage facultatif
        }
        return Path.Combine(logs, $"maintenance-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}
