using System.Globalization;
using System.Threading.Channels;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Alerts;

/// <summary>File des alertes à envoyer (consommée par AlertDispatcher, en arrière-plan : la réception d'un rapport n'attend jamais un envoi).</summary>
public sealed class AlertQueue
{
    private readonly Channel<Alert> _channel = Channel.CreateBounded<Alert>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public bool Enqueue(Alert alert) => _channel.Writer.TryWrite(alert);
    public ChannelReader<Alert> Reader => _channel.Reader;
}

/// <summary>
/// Déclencheurs des alertes et anti-spam : une même alerte (même poste, même motif) n'est jamais envoyée deux fois en
/// moins de 24 h. Aucune alerte n'est générée tant qu'aucun canal (e-mail ou webhook) n'est configuré.
/// </summary>
public sealed class AlertService(ServerStore store, AlertSettingsService settings, AlertQueue queue, TimeProvider clock, ILogger<AlertService> log)
{
    public static readonly TimeSpan AntiSpamWindow = TimeSpan.FromHours(24);
    /// <summary>Clé anti-spam des alertes qui concernent tout le parc (résumé quotidien).</summary>
    public const string FleetSubject = "parc";

    private static string Subject(long machineRef) => "poste:" + machineRef.ToString(CultureInfo.InvariantCulture);

    /// <summary>Après l'enregistrement d'un rapport : poste devenu « à traiter », échec d'installation, pilote dangereux.</summary>
    public void OnReportSaved(MachineReport report, SaveResult save)
    {
        if (!save.IsLatest) return; // reprise d'anciens rapports : pas d'alerte sur un état dépassé
        var s = settings.Current();
        if (!s.HasChannel) return;
        var now = clock.GetUtcNow();
        var facts = MachineFacts(report);

        if (s.Triggers.MachineToProcess && !report.Healthy && (save.Previous is null || save.Previous.Healthy))
        {
            var reasons = new List<string>();
            if (report.DriverProblems > 0) reasons.Add($"{report.DriverProblems} périphérique(s) en erreur à cause d'un pilote");
            if (report.Unresolved > 0) reasons.Add($"dont {report.Unresolved} sans pilote trouvé");
            if (report.Failed > 0) reasons.Add($"{report.Failed} échec(s) d'installation");
            var devices = report.ProblemDevices.Where(d => !d.IsHardwareFault).Take(5).Select(d => d.Name).ToList();
            Raise(Subject(save.MachineRef), new Alert(AlertReasons.MachineToProcess, "warning",
                $"{report.Name} est à traiter",
                $"Le dernier rapport de {report.Name} signale : {string.Join(", ", reasons)}." +
                (devices.Count > 0 ? $"\nPériphériques : {string.Join(", ", devices)}." : ""),
                report.Name, facts, Link(s, report.Name), now));
        }

        if (s.Triggers.InstallFailed && report.Failed > 0)
        {
            var failed = report.Installation!.Where(o => o.IsFailure).Take(5).Select(o => $"{o.Title} {o.Version} : {o.Message}".Trim()).ToList();
            Raise(Subject(save.MachineRef), new Alert(AlertReasons.InstallFailed, "critical",
                $"Échec d'installation sur {report.Name}",
                $"{report.Failed} pilote(s) n'ont pas pu être installés :\n{string.Join("\n", failed)}",
                report.Name, facts, Link(s, report.Name), now));
        }

        if (s.Triggers.DangerousDriver && report.Health?.HasDangerousDriver == true)
        {
            var findings = report.Health.Findings.Where(f => f.IsDangerousDriver).Take(5)
                .Select(f => string.IsNullOrWhiteSpace(f.Detail) ? f.Title : $"{f.Title} — {f.Detail}").ToList();
            Raise(Subject(save.MachineRef), new Alert(AlertReasons.DangerousDriver, "critical",
                $"Pilote dangereux détecté sur {report.Name}",
                $"Un pilote connu pour provoquer des pannes (écrans bleus) est présent :\n{string.Join("\n", findings)}",
                report.Name, facts, Link(s, report.Name), now));
        }
    }

    /// <summary>Tâche à distance terminée en échec (ou restée sans résultat).</summary>
    public void OnJobFinished(JobRecord job)
    {
        if (job.State is not (JobStates.Failed or JobStates.Expired)) return;
        if (job.State == JobStates.Expired && job.StartedAt is null) return; // jamais récupérée : couvert par « poste non vu »
        var s = settings.Current();
        if (!s.HasChannel || !s.Triggers.JobFailed) return;
        var type = JobTypes.Find(job.Type)?.Label ?? job.Type;
        Raise(Subject(job.MachineRef), new Alert(AlertReasons.JobFailed, "warning",
            $"Tâche à distance échouée sur {job.Machine}",
            $"« {type} »{(job.Parameters.DryRun ? " (simulation)" : "")} : {job.Message ?? "échec sans message"}",
            job.Machine,
            [
                new AlertFact("Poste", job.Machine),
                new AlertFact("Tâche", $"n° {job.Id} — {type}"),
                new AlertFact("Demandée par", job.CreatedBy),
                new AlertFact("Code de sortie", job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"),
            ],
            Link(s, job.Machine), clock.GetUtcNow()));
    }

    /// <summary>
    /// Vérification périodique des postes non vus : une alerte groupée pour les postes qui viennent de dépasser le délai
    /// (une seule fois par période d'absence). Renvoie le nombre de postes signalés.
    /// </summary>
    public int CheckStaleMachines(DateTimeOffset now)
    {
        var s = settings.Current();
        if (!s.HasChannel || !s.Triggers.MachineStale || s.StaleDays <= 0) return 0;
        // Indicateurs précalculés (aucun rapport relu), puis seulement les postes non vus.
        var stale = store.GetMachineSummaries().Where(m => FleetAggregator.IsStale(m.LastSeen, now, s.StaleDays)).OrderBy(m => m.LastSeen).ToList();
        // Clé anti-spam = poste + date du dernier contact : une alerte par période d'absence, renouvelée au plus tous les 30 jours.
        var fresh = stale.Where(m => store.TryRegisterAlert(Subject(m.Id), $"{AlertReasons.MachineStale}:{ServerStore.Iso(m.LastSeen)}", now, TimeSpan.FromDays(30))).ToList();
        if (fresh.Count == 0) return 0;
        var lines = fresh.Take(30).Select(m => $"{m.Name} ({m.DisplayModel}) — dernier contact le {m.LastSeen.ToLocalTime():dd/MM/yyyy HH:mm}").ToList();
        if (fresh.Count > 30) lines.Add($"… et {fresh.Count - 30} autre(s).");
        Enqueue(new Alert(AlertReasons.MachineStale, "warning",
            fresh.Count == 1 ? $"{fresh[0].Name} n'a pas été vu depuis {s.StaleDays} jours" : $"{fresh.Count} postes non vus depuis {s.StaleDays} jours",
            $"Aucun rapport reçu depuis plus de {s.StaleDays} jours (poste éteint, hors réseau, SpokaPilote désinstallé ?) :\n{string.Join("\n", lines)}",
            fresh.Count == 1 ? fresh[0].Name : null, [new AlertFact("Postes", fresh.Count.ToString(CultureInfo.InvariantCulture))],
            fresh.Count == 1 ? Link(s, fresh[0].Name) : Link(s, null, "#/postes?statut=stale"), now));
        return fresh.Count;
    }

    /// <summary>Résumé quotidien du parc (toujours envoyé à l'heure prévue, sans anti-spam).</summary>
    public Alert BuildDailySummary(DateTimeOffset now)
    {
        var s = settings.Current();
        var fleet = store.GetFleetSummary(null, now, s.StaleDays);
        var jobs = store.GetJobStats(now);
        var facts = new List<AlertFact>
        {
            new("Postes", fleet.MachineCount.ToString(CultureInfo.InvariantCulture)),
            new("Sains", fleet.Healthy.ToString(CultureInfo.InvariantCulture)),
            new("À traiter", fleet.ToProcess.ToString(CultureInfo.InvariantCulture)),
            new($"Non vus depuis {s.StaleDays} j", fleet.Stale.ToString(CultureInfo.InvariantCulture)),
            new("Matériel sans pilote", fleet.MissingDrivers.Count.ToString(CultureInfo.InvariantCulture)),
            new("Échecs d'installation", fleet.Failed.ToString(CultureInfo.InvariantCulture)),
            new("Tâches en attente / en cours", $"{jobs.Pending} / {jobs.Running}"),
            new("Tâches échouées (24 h)", jobs.FailedLast24h.ToString(CultureInfo.InvariantCulture)),
        };
        if (fleet.AverageHealth is { } health)
            facts.Add(new AlertFact("Santé moyenne", $"{health.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} / 100 ({fleet.HealthMachines} postes)"));
        if (fleet.DangerousDrivers > 0) facts.Add(new AlertFact("Postes avec pilote dangereux", fleet.DangerousDrivers.ToString(CultureInfo.InvariantCulture)));
        var todo = fleet.ToProcessMachines.Take(10).Select(m => $"{m.Name} : {m.DriverProblems} en erreur, {m.Failed} échec(s)").ToList();
        return new Alert(AlertReasons.DailySummary, fleet.ToProcess > 0 || jobs.FailedLast24h > 0 ? "warning" : "info",
            $"Résumé du parc : {fleet.ToProcess} poste(s) à traiter sur {fleet.MachineCount}",
            todo.Count == 0 ? "Tous les postes sont sains." : $"Postes à traiter :\n{string.Join("\n", todo)}" +
                                                                (fleet.ToProcess > todo.Count ? $"\n… et {fleet.ToProcess - todo.Count} autre(s)." : ""),
            null, facts, Link(s, null), now);
    }

    public Alert BuildTest(DateTimeOffset now) => new(AlertReasons.Test, "info", "Alerte de test SpokaPilote",
        "Si vous lisez ce message, les alertes de la console SpokaPilote arrivent bien sur ce canal.", null,
        [new AlertFact("Serveur", Environment.MachineName), new AlertFact("Envoyée le", now.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))],
        Link(settings.Current(), null), now);

    /// <summary>Envoie l'alerte si elle n'a pas déjà été envoyée pour ce sujet et ce motif dans la fenêtre anti-spam.</summary>
    public bool Raise(string subject, Alert alert)
    {
        if (!store.TryRegisterAlert(subject, alert.Reason, alert.Timestamp, AntiSpamWindow))
        {
            log.LogDebug("Alerte « {Reason} » pour {Subject} ignorée (déjà envoyée il y a moins de 24 h).", alert.Reason, subject);
            return false;
        }
        Enqueue(alert);
        return true;
    }

    public void Enqueue(Alert alert)
    {
        if (!queue.Enqueue(alert)) log.LogWarning("File des alertes indisponible : alerte « {Title} » perdue.", alert.Title);
    }

    private static List<AlertFact> MachineFacts(MachineReport r)
    {
        var facts = new List<AlertFact> { new("Poste", r.Name), new("Modèle", r.System.DisplayModel) };
        if (!string.IsNullOrWhiteSpace(r.System.SerialNumber)) facts.Add(new AlertFact("N° de série", r.System.SerialNumber));
        facts.Add(new AlertFact("Windows", $"{r.System.OsCaption} {r.System.OsDisplayVersion}".Trim()));
        facts.Add(new AlertFact("Rapport du", r.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));
        if (r.Health?.Score is { } score) facts.Add(new AlertFact("Santé", $"{score}/100{(r.Health.Grade is null ? "" : $" ({r.Health.Grade})")}"));
        return facts;
    }

    private static string? Link(AlertSettings s, string? machine, string? fragment = null) =>
        string.IsNullOrWhiteSpace(s.ConsoleUrl) ? null
            : $"{s.ConsoleUrl.TrimEnd('/')}/{fragment ?? (machine is null ? "#/" : $"#/postes/{Uri.EscapeDataString(machine)}")}";
}
