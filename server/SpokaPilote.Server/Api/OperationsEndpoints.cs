using System.Diagnostics;
using System.Globalization;
using System.Text;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>
/// Exploitation (3.2, Administrateur ou jeton administrateur) : supervision (GET /api/metrics, JSON ou format texte
/// Prometheus), sauvegardes à chaud dans /data/backups, compactage de la base. La sonde de vie publique reste
/// GET /api/health, qui ne révèle rien du parc.
/// </summary>
public static class OperationsEndpoints
{
    public static void MapOperationsApi(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapGet("/metrics", (string? format, ServerStore store, ServerOptions options, AdmissionControl admission, RequestMetrics requests,
            BackupService backups, StorageMaintenance maintenance, Cache.DriverCacheStore driverCache, TimeProvider clock) =>
        {
            var cacheStats = driverCache.Stats();
            var now = clock.GetUtcNow();
            var db = store.GetDatabaseStats();
            var fleet = store.GetFleetCounters(now, options.StaleDays);
            var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            var (hits, misses) = store.FleetCacheStats;
            var routes = requests.Snapshot();
            if (string.Equals(format, "prometheus", StringComparison.OrdinalIgnoreCase))
                return Results.Text(Prometheus(db, fleet, admission, routes, process, hits, misses, backups) + Cache.DriverCacheEndpoints.Prometheus(cacheStats),
                    "text/plain; version=0.0.4; charset=utf-8");
            return Results.Ok(new
            {
                version = ApiEndpoints.Version,
                startedAt = requests.StartedAt,
                uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - requests.StartedAt).TotalSeconds),
                database = new
                {
                    path = store.DatabasePath,
                    fileBytes = db.FileBytes,
                    walBytes = db.WalBytes,
                    pageSize = db.PageSize,
                    pageCount = db.PageCount,
                    freePages = db.FreePages,
                    freeRatio = Math.Round(db.FreeRatio, 4),
                    schemaVersion = ServerStore.SchemaVersion,
                    upgradedFrom = store.OpenedSchemaVersion < ServerStore.SchemaVersion ? store.OpenedSchemaVersion : (int?)null,
                    migrationSeconds = Math.Round(store.MigrationDuration.TotalSeconds, 2),
                    legacyReports = db.LegacyReports,
                    legacyReportsConverted = maintenance.LegacyReportsConverted,
                    revision = db.Revision,
                },
                fleet = new
                {
                    machines = fleet.Machines, toProcess = fleet.ToProcess, stale = fleet.Stale, agentsLastDay = fleet.AgentsLastDay,
                    reports = db.Reports, groups = db.Groups, historyPerMachine = options.HistoryPerMachine, historyDays = options.HistoryDays,
                },
                jobs = new { total = db.Jobs, pending = db.PendingJobs, running = db.RunningJobs },
                audit = new { entries = db.AuditEntries },
                sessions = db.Sessions,
                queues = new[] { Gate(admission.Uploads), Gate(admission.Agents) },
                cache = new { fleetHits = hits, fleetMisses = misses },
                process = new
                {
                    workingSetBytes = process.WorkingSet64,
                    gcHeapBytes = GC.GetTotalMemory(false),
                    gcCommittedBytes = gc.TotalCommittedBytes,
                    gen0Collections = GC.CollectionCount(0),
                    gen1Collections = GC.CollectionCount(1),
                    gen2Collections = GC.CollectionCount(2),
                    threads = process.Threads.Count,
                    threadPoolThreads = ThreadPool.ThreadCount,
                    threadPoolQueue = ThreadPool.PendingWorkItemCount,
                    cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 1),
                    processors = Environment.ProcessorCount,
                },
                requests = new { inFlight = requests.InFlight, routes },
                backup = new { running = backups.IsRunning, last = backups.Last, count = backups.List().Count, schedule = options.BackupTime },
                // Cache de pilotes (4.0) : taille, taux de réussite, octets évités, garde d'espace disque.
                driverCache = new
                {
                    enabled = cacheStats.Enabled, catalogs = cacheStats.Catalogs, sizeBytes = cacheStats.SizeBytes, maxBytes = cacheStats.MaxBytes,
                    files = cacheStats.Files, catalogEntries = cacheStats.CatalogEntries, hits = cacheStats.Hits, misses = cacheStats.Misses,
                    hitRatio = cacheStats.HitRatio is { } ratio ? Math.Round(ratio, 4) : (double?)null, bytesSaved = cacheStats.BytesServedFromCache,
                    bytesFromOrigin = cacheStats.BytesFromOrigin, bypassed = cacheStats.Bypassed, refused = cacheStats.Refused, errors = cacheStats.Errors,
                    hashMismatches = cacheStats.HashMismatches, downloadsInProgress = cacheStats.DownloadsInProgress, freeBytes = cacheStats.FreeBytes,
                    diskGuardActive = cacheStats.DiskGuardActive,
                },
            });
        });

        admin.MapGet("/admin/backups", (BackupService backups, ServerOptions options) => Results.Ok(new
        {
            running = backups.IsRunning,
            last = backups.Last,
            directory = options.BackupDirectory,
            keep = options.BackupKeep,
            schedule = options.BackupTime,
            backups = backups.List(),
        }));

        // Sauvegarde à chaud : en arrière-plan (202), ou ?wait=true pour attendre le résultat (scripts, tâche cron).
        admin.MapPost("/admin/backup", (bool? wait, HttpContext http, BackupService backups, AuditService audit) =>
        {
            var by = Principal.Of(http)!.Actor;
            if (wait == true)
            {
                BackupStatus status;
                try { status = backups.Run(by); }
                catch (InvalidOperationException ex) { return Results.Json(new ApiError(ex.Message), statusCode: StatusCodes.Status409Conflict); }
                audit.Record(http, AuditActions.MaintenanceBackup, status.File?.Name, status.Message, status.Success);
                return status.Success ? Results.Ok(status) : Results.Json(status, statusCode: StatusCodes.Status500InternalServerError);
            }
            if (!backups.TryStart(by)) return Results.Json(new ApiError("Une sauvegarde est déjà en cours."), statusCode: StatusCodes.Status409Conflict);
            audit.Record(http, AuditActions.MaintenanceBackup, null, "sauvegarde lancée en arrière-plan");
            return Results.Accepted("/api/admin/backups", new { running = true });
        });

        // Compactage (VACUUM) : rend au disque la place libérée ; bloque les écritures pendant sa durée (période creuse).
        admin.MapPost("/admin/vacuum", (HttpContext http, ServerStore store, AuditService audit, ILogger<ServerStore> log) =>
        {
            var before = store.GetDatabaseStats();
            var sw = Stopwatch.StartNew();
            try
            {
                store.Vacuum();
            }
            catch (TimeoutException ex)
            {
                return Results.Json(new ApiError(ex.Message), statusCode: StatusCodes.Status409Conflict);
            }
            var after = store.GetDatabaseStats();
            var detail = string.Create(CultureInfo.InvariantCulture,
                $"{before.FileBytes / 1048576.0:0.0} Mo → {after.FileBytes / 1048576.0:0.0} Mo en {sw.Elapsed.TotalSeconds:0.0} s");
            log.LogInformation("VACUUM : {Detail}.", detail);
            audit.Record(http, AuditActions.MaintenanceVacuum, null, detail);
            return Results.Ok(new { beforeBytes = before.FileBytes, afterBytes = after.FileBytes, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
        });
    }

    private static object Gate(AdmissionControl.Gate g) => new
    {
        name = g.Name, permits = g.Permits, available = g.Available, queued = g.Queued, queueLimit = g.QueueLimit, accepted = g.Accepted,
        rejected = g.Rejected,
    };

    /// <summary>Format texte d'exposition Prometheus (jauges et compteurs, sans dépendance).</summary>
    private static string Prometheus(DatabaseStats db, (int Machines, int ToProcess, int Stale, int AgentsLastDay) fleet, AdmissionControl admission,
        List<RequestMetrics.RouteStats> routes, Process process, long hits, long misses, BackupService backups)
    {
        var sb = new StringBuilder();
        void M(string name, string help, string type, params (string Labels, double Value)[] values)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            foreach (var (labels, value) in values)
                sb.Append(name).Append(labels.Length == 0 ? "" : "{" + labels + "}").Append(' ')
                    .Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        M("spokapilote_up", "Console en service.", "gauge", ("", 1));
        M("spokapilote_machines", "Postes connus.", "gauge", ("", fleet.Machines));
        M("spokapilote_machines_to_process", "Postes à traiter.", "gauge", ("", fleet.ToProcess));
        M("spokapilote_machines_stale", "Postes non vus depuis le délai configuré.", "gauge", ("", fleet.Stale));
        M("spokapilote_agents_last_day", "Agents vus depuis 24 h.", "gauge", ("", fleet.AgentsLastDay));
        M("spokapilote_reports", "Rapports conservés dans l'historique.", "gauge", ("", db.Reports));
        M("spokapilote_reports_legacy", "Rapports 3.1 restant à compresser.", "gauge", ("", db.LegacyReports));
        M("spokapilote_jobs", "Tâches à distance par état.", "gauge", ("state=\"pending\"", db.PendingJobs), ("state=\"running\"", db.RunningJobs),
            ("state=\"all\"", db.Jobs));
        M("spokapilote_database_bytes", "Taille des fichiers de la base.", "gauge", ("file=\"db\"", db.FileBytes), ("file=\"wal\"", db.WalBytes));
        M("spokapilote_database_free_ratio", "Part de pages libres de la base.", "gauge", ("", db.FreeRatio));
        foreach (var g in new[] { admission.Uploads, admission.Agents })
        {
            M($"spokapilote_queue_{g.Name}_queued", "Requêtes en attente.", "gauge", ("", g.Queued));
            M($"spokapilote_queue_{g.Name}_available", "Places libres.", "gauge", ("", g.Available));
            M($"spokapilote_queue_{g.Name}_rejected_total", "Requêtes refusées (503).", "counter", ("", g.Rejected));
            M($"spokapilote_queue_{g.Name}_accepted_total", "Requêtes admises.", "counter", ("", g.Accepted));
        }
        M("spokapilote_fleet_cache_hits_total", "Synthèses servies depuis le cache.", "counter", ("", hits));
        M("spokapilote_fleet_cache_misses_total", "Synthèses recalculées.", "counter", ("", misses));
        M("spokapilote_process_working_set_bytes", "Mémoire du processus.", "gauge", ("", process.WorkingSet64));
        M("spokapilote_gc_heap_bytes", "Tas géré.", "gauge", ("", GC.GetTotalMemory(false)));
        M("spokapilote_process_cpu_seconds_total", "Temps processeur.", "counter", ("", process.TotalProcessorTime.TotalSeconds));
        M("spokapilote_backup_last_success", "Dernière sauvegarde réussie (1) ou en échec (0).", "gauge", ("", backups.Last?.Success == false ? 0 : 1));
        M("spokapilote_http_requests_total", "Requêtes par route.", "counter",
            routes.Select(r => ($"route=\"{Escape(r.Route)}\"", (double)r.Count)).ToArray());
        M("spokapilote_http_errors_total", "Réponses 5xx par route.", "counter",
            routes.Select(r => ($"route=\"{Escape(r.Route)}\"", (double)r.Errors)).ToArray());
        M("spokapilote_http_latency_ms", "Temps de réponse (2 048 dernières requêtes) par route.", "gauge",
            routes.SelectMany(r => new[]
            {
                ($"route=\"{Escape(r.Route)}\",quantile=\"0.5\"", r.P50), ($"route=\"{Escape(r.Route)}\",quantile=\"0.95\"", r.P95),
                ($"route=\"{Escape(r.Route)}\",quantile=\"0.99\"", r.P99),
            }).ToArray());
        return sb.ToString();
    }
}
