using System.Diagnostics;
using System.Globalization;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Audit;

namespace SpokaPilote.Server.Storage;

/// <summary>Résultat de la dernière sauvegarde.</summary>
public sealed record BackupStatus(DateTimeOffset At, bool Success, string Message, BackupFile? File, double Seconds, string By);

/// <summary>
/// Sauvegardes de la base dans /data/backups (API de sauvegarde en ligne de SQLite) : à la demande (page Paramètres,
/// POST /api/admin/backup) ou chaque jour à l'heure SPOKAPILOTE_BACKUP_TIME. Une seule à la fois ; les
/// SPOKAPILOTE_BACKUP_KEEP plus récentes sont conservées.
/// </summary>
public sealed class BackupService(ServerStore store, ServerOptions options, TimeProvider clock, ILogger<BackupService> log)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public BackupStatus? Last { get; private set; }

    /// <summary>Lance une sauvegarde (synchrone). Lève InvalidOperationException si une sauvegarde est déjà en cours.</summary>
    public BackupStatus Run(string by)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Une sauvegarde est déjà en cours.");
        var sw = Stopwatch.StartNew();
        var now = clock.GetUtcNow();
        try
        {
            var file = store.Backup(options.BackupDirectory, now);
            var removed = ServerStore.PruneBackups(options.BackupDirectory, options.BackupKeep);
            Last = new BackupStatus(now, true,
                string.Create(CultureInfo.InvariantCulture, $"{file.Name} ({file.Bytes / 1048576.0:0.0} Mo) en {sw.Elapsed.TotalSeconds:0.0} s") +
                (removed > 0 ? $", {removed} ancienne(s) supprimée(s)" : ""), file, sw.Elapsed.TotalSeconds, by);
            log.LogInformation("Sauvegarde de la base : {Message}.", Last.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Last = new BackupStatus(now, false, ex.Message, null, sw.Elapsed.TotalSeconds, by);
            log.LogError(ex, "Sauvegarde de la base impossible.");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
        return Last;
    }

    /// <summary>Lance une sauvegarde en arrière-plan ; false si une sauvegarde est déjà en cours.</summary>
    public bool TryStart(string by)
    {
        if (IsRunning) return false;
        _ = Task.Run(() =>
        {
            try { Run(by); }
            catch (InvalidOperationException) { /* lancée entre-temps par un autre appel */ }
        });
        return true;
    }

    public List<BackupFile> List() => ServerStore.ListBackups(options.BackupDirectory);
}

/// <summary>
/// Entretien de la base en arrière-plan, par petits lots qui ne tiennent jamais le verrou d'écriture longtemps :
/// compression de l'historique hérité de la 3.1, rétention de l'historique par âge (SPOKAPILOTE_HISTORY_DAYS),
/// PRAGMA optimize (ANALYZE au besoin) toutes les heures, sauvegarde quotidienne facultative (SPOKAPILOTE_BACKUP_TIME).
/// </summary>
public sealed class StorageMaintenance(ServerStore store, ServerOptions options, BackupService backups, AuditService audit, TimeProvider clock,
    ILogger<StorageMaintenance> log) : BackgroundService
{
    private DateTimeOffset _lastOptimize = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    private string? _lastBackupDay;

    public int LegacyReportsConverted { get; private set; }
    public bool LegacyMigrationDone { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // 1. Historique hérité de la 3.1 : compressé par lots de 200 rapports, avec une courte pause entre deux lots.
        var sw = Stopwatch.StartNew();
        var pending = store.CountLegacyReports();
        if (pending > 0) log.LogInformation("Compression de l'historique hérité : {Count} rapport(s) à convertir, en arrière-plan.", pending);
        while (!stoppingToken.IsCancellationRequested)
        {
            int done;
            try { done = store.MigrateLegacyReports(); }
            catch (Exception ex)
            {
                log.LogError(ex, "Compression de l'historique : lot en échec, nouvel essai dans une minute.");
                try { await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            if (done == 0) break;
            LegacyReportsConverted += done;
            try { await Task.Delay(TimeSpan.FromMilliseconds(20), clock, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
        LegacyMigrationDone = true;
        if (LegacyReportsConverted > 0)
        {
            log.LogInformation("Historique compressé : {Count} rapport(s) en {Seconds:0} s.", LegacyReportsConverted, sw.Elapsed.TotalSeconds);
            try { store.Optimize(); } catch (Exception ex) { log.LogWarning(ex, "PRAGMA optimize en échec."); }
        }

        // 2. Entretien périodique.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                RunOnce(clock.GetUtcNow());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Entretien de la base en échec.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Un passage d'entretien (public pour les tests).</summary>
    public void RunOnce(DateTimeOffset now)
    {
        if (options.HistoryDays > 0 && now - _lastRetention >= TimeSpan.FromMinutes(15))
        {
            _lastRetention = now;
            var removed = store.PurgeOldReports(now, options.HistoryDays);
            if (removed > 0) log.LogInformation("Rétention : {Count} rapport(s) de plus de {Days} jours supprimé(s).", removed, options.HistoryDays);
        }
        if (now - _lastOptimize >= TimeSpan.FromHours(1))
        {
            _lastOptimize = now;
            store.Optimize();
        }
        if (AlertSettingsService.ParseTime(options.BackupTime) is { } at)
        {
            var local = TimeZoneInfo.ConvertTime(now, clock.LocalTimeZone);
            var today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Au redémarrage : pas de deuxième sauvegarde le même jour.
            _lastBackupDay ??= backups.List().FirstOrDefault() is { } latest
                ? TimeZoneInfo.ConvertTime(latest.CreatedAt, clock.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "";
            if (local.TimeOfDay >= at && _lastBackupDay != today && !backups.IsRunning)
            {
                _lastBackupDay = today;
                var status = backups.Run("Sauvegarde automatique");
                audit.Record("Sauvegarde automatique", null, AuditActions.MaintenanceBackup, status.File?.Name, status.Message, null, status.Success);
            }
        }
    }
}
