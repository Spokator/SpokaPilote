using System.Globalization;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Storage;

/// <summary>Taille et contenu de la base (supervision, page Paramètres).</summary>
public sealed record DatabaseStats(long FileBytes, long WalBytes, long PageSize, long PageCount, long FreePages, int Machines, int Reports,
    int LegacyReports, int Jobs, int PendingJobs, int RunningJobs, int AuditEntries, int Sessions, int Groups, long Revision)
{
    /// <summary>Part de pages libres (réutilisées automatiquement ; un VACUUM rend la place au disque).</summary>
    public double FreeRatio => PageCount == 0 ? 0 : (double)FreePages / PageCount;
}

/// <summary>Sauvegarde présente dans /data/backups.</summary>
public sealed record BackupFile(string Name, long Bytes, DateTimeOffset CreatedAt);

public sealed partial class ServerStore
{
    // ------------------------------------------------------------------ migration des rapports 3.1 (en arrière-plan)

    /// <summary>
    /// Compresse un lot de rapports de l'historique encore au format 3.1 (JSON en clair, sans indicateurs) : lecture et
    /// calcul hors verrou, puis une courte transaction. Renvoie le nombre de rapports convertis (0 : migration terminée).
    /// </summary>
    public int MigrateLegacyReports(int batch = 200)
    {
        using var c = Open();
        var rows = new List<(long Id, string Json)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, summary_json FROM reports WHERE report_ts IS NULL ORDER BY id LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", batch);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        if (rows.Count == 0) return 0;
        var converted = rows.Select(row =>
        {
            MachineReport? report;
            try { report = Deserialize(row.Json); }
            catch (System.Text.Json.JsonException) { report = null; }
            return (row.Id, Report: report, Gz: report is null ? null : CompressSummary(report));
        }).ToList();

        using var tx = BeginWrite(c);
        using var update = c.CreateCommand();
        update.CommandText = """
            UPDATE reports SET summary_json = '', summary_gz = $gz, report_ts = $ts, app_version = $app, driver_problems = $dp, hardware_faults = $hf,
                unresolved = $un, proposed = $pr, blocked = $bl, installed = $in, failed = $fa, analysis_only = $ao, healthy = $he, health_score = $hs
            WHERE id = $id AND report_ts IS NULL;
            """;
        foreach (var (id, report, gz) in converted)
        {
            update.Parameters.Clear();
            if (report is null)
            {
                // JSON illisible (ne devrait pas arriver) : marqué migré pour ne pas être relu à chaque passage, contenu conservé.
                Execute(c, """
                    UPDATE reports SET report_ts = report_at, driver_problems = 0, hardware_faults = 0, unresolved = 0, proposed = 0, blocked = 0,
                        installed = 0, failed = 0, analysis_only = 1, healthy = 1 WHERE id = $id;
                    """, ("$id", id));
                continue;
            }
            foreach (var (n, v) in new (string, object?)[]
                     {
                         ("$id", id), ("$gz", gz), ("$ts", TimestampText(report.Timestamp)), ("$app", report.AppVersion),
                         ("$dp", report.DriverProblems), ("$hf", report.HardwareFaults), ("$un", report.Unresolved), ("$pr", report.Proposed),
                         ("$bl", report.Blocked), ("$in", report.Installed), ("$fa", report.Failed), ("$ao", report.AnalysisOnly ? 1 : 0),
                         ("$he", report.Healthy ? 1 : 0), ("$hs", report.Health?.Score),
                     })
                update.Parameters.AddWithValue(n, v ?? DBNull.Value);
            update.ExecuteNonQuery();
        }
        tx.Commit();
        return rows.Count;
    }

    public int CountLegacyReports()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM reports WHERE report_ts IS NULL;"), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ rétention

    /// <summary>
    /// Historique limité en âge (SPOKAPILOTE_HISTORY_DAYS) : supprime par lots les rapports plus anciens que
    /// <paramref name="days"/> jours, sauf le plus récent de chaque poste. Renvoie le nombre de rapports supprimés.
    /// </summary>
    public int PurgeOldReports(DateTimeOffset now, int days, int batch = 2000)
    {
        if (days <= 0) return 0;
        var total = 0;
        using var c = Open();
        while (true)
        {
            var victims = new List<(long Id, long Machine)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT r.id, r.machine_ref FROM reports r WHERE r.report_at < $cutoff
                      AND r.id <> (SELECT r2.id FROM reports r2 WHERE r2.machine_ref = r.machine_ref ORDER BY r2.report_at DESC, r2.id DESC LIMIT 1)
                    LIMIT $n;
                    """;
                cmd.Parameters.AddWithValue("$cutoff", Iso(now.AddDays(-days)));
                cmd.Parameters.AddWithValue("$n", batch);
                using var r = cmd.ExecuteReader();
                while (r.Read()) victims.Add((r.GetInt64(0), r.GetInt64(1)));
            }
            if (victims.Count == 0) return total;
            using (var tx = BeginWrite(c))
            {
                Execute(c, $"DELETE FROM reports WHERE id IN ({string.Join(",", victims.Select(v => v.Id))});");
                foreach (var m in victims.Select(v => v.Machine).Distinct())
                    Execute(c, "UPDATE machines SET report_count = (SELECT COUNT(*) FROM reports WHERE machine_ref = $m) WHERE id = $m;", ("$m", m));
                tx.Commit();
            }
            total += victims.Count;
            if (victims.Count < batch) return total;
        }
    }

    // ------------------------------------------------------------------ entretien de la base

    /// <summary>
    /// Entretien périodique : statistiques de l'optimiseur mises à jour si besoin (PRAGMA optimize, qui lance ANALYZE sur
    /// les tables qui en ont besoin) et point de contrôle du journal WAL sans bloquer les lectures ni les écritures.
    /// </summary>
    public void Optimize()
    {
        using var c = Open();
        Execute(c, "PRAGMA analysis_limit = 1000; PRAGMA optimize;");
        Execute(c, "PRAGMA wal_checkpoint(PASSIVE);");
    }

    /// <summary>
    /// VACUUM : réécrit la base pour rendre au disque la place libre (après de grosses suppressions). Bloque les écritures
    /// pendant toute sa durée (quelques secondes à quelques minutes sur un grand parc) : à lancer en période creuse.
    /// </summary>
    public void Vacuum()
    {
        using var c = Open();
        if (!_writeGate.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Base occupée : VACUUM non lancé.");
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandTimeout = 3600;
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
            Execute(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public DatabaseStats GetDatabaseStats()
    {
        using var c = Open();
        long L(string sql) => Convert.ToInt64(Scalar(c, sql) ?? 0L, CultureInfo.InvariantCulture);
        int I(string sql) => (int)L(sql);
        var wal = DatabasePath + "-wal";
        return new DatabaseStats(
            File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0,
            File.Exists(wal) ? new FileInfo(wal).Length : 0,
            L("PRAGMA page_size;"), L("PRAGMA page_count;"), L("PRAGMA freelist_count;"),
            I("SELECT COUNT(*) FROM machines;"), I("SELECT COUNT(*) FROM reports;"), I("SELECT COUNT(*) FROM reports WHERE report_ts IS NULL;"),
            I("SELECT COUNT(*) FROM jobs;"), I("SELECT COUNT(*) FROM jobs WHERE state = 'pending';"), I("SELECT COUNT(*) FROM jobs WHERE state = 'running';"),
            I("SELECT COUNT(*) FROM audit;"), I("SELECT COUNT(*) FROM sessions;"), I("SELECT COUNT(*) FROM machine_groups;"),
            L("SELECT value FROM meta WHERE key = 'fleet_rev';"));
    }

    /// <summary>Compteurs du parc pour la supervision (requêtes sur index, sans calcul de synthèse).</summary>
    public (int Machines, int ToProcess, int Stale, int AgentsLastDay) GetFleetCounters(DateTimeOffset now, int staleDays)
    {
        using var c = Open();
        var row = Row(c, """
            SELECT COUNT(*), COALESCE(SUM(healthy = 0), 0),
                   (SELECT COUNT(*) FROM machines WHERE $stale AND last_seen < $cutoff),
                   (SELECT COUNT(*) FROM machines WHERE agent_seen >= $day)
            FROM machines;
            """, ("$stale", staleDays > 0 ? 1 : 0), ("$cutoff", Iso(now - TimeSpan.FromDays(Math.Max(0, staleDays)))), ("$day", Iso(now.AddDays(-1))))!;
        int I(int i) => Convert.ToInt32(row[i] ?? 0, CultureInfo.InvariantCulture);
        return (I(0), I(1), I(2), I(3));
    }

    // ------------------------------------------------------------------ sauvegarde

    /// <summary>
    /// Sauvegarde à chaud par l'API de sauvegarde en ligne de SQLite : copie cohérente de la base (WAL compris) pendant que
    /// la console continue de recevoir rapports et tâches (en mode WAL, les écritures ne sont pas bloquées). La copie est
    /// vérifiée (PRAGMA quick_check) avant d'être renommée en .db ; une copie incomplète n'est jamais laissée sous ce nom.
    /// </summary>
    public BackupFile Backup(string directory, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var name = $"spokapilote-{now.ToUniversalTime():yyyyMMdd-HHmmss}.db";
        var target = Path.Combine(directory, name);
        var temp = target + ".partial";
        if (File.Exists(temp)) File.Delete(temp);
        var destination = new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using (var source = Open())
        using (var copy = new SqliteConnection(destination))
        {
            copy.Open();
            source.BackupDatabase(copy);
            using var check = copy.CreateCommand();
            check.CommandTimeout = 3600;
            check.CommandText = "PRAGMA quick_check;";
            var result = check.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Copie de sauvegarde invalide ({result}).");
            // Copie autonome : un seul fichier, sans journal WAL à côté.
            using var mode = copy.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode = DELETE;";
            mode.ExecuteNonQuery();
        }
        File.Move(temp, target, overwrite: true);
        var info = new FileInfo(target);
        return new BackupFile(name, info.Length, now);
    }

    public static List<BackupFile> ListBackups(string directory) =>
        !Directory.Exists(directory)
            ? new List<BackupFile>()
            : new DirectoryInfo(directory).GetFiles("spokapilote-*.db")
                .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();

    /// <summary>Ne garde que les <paramref name="keep"/> sauvegardes les plus récentes. Renvoie le nombre de fichiers supprimés.</summary>
    public static int PruneBackups(string directory, int keep)
    {
        var removed = 0;
        foreach (var old in ListBackups(directory).Skip(Math.Max(1, keep)))
        {
            File.Delete(Path.Combine(directory, old.Name));
            removed++;
        }
        return removed;
    }
}
