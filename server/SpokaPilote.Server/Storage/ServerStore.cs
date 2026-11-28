using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Storage;

/// <summary>Entrée de l'historique d'un poste (indicateurs du rapport, sans le détail).</summary>
public sealed record HistoryEntry(long Id, DateTimeOffset Timestamp, DateTimeOffset ReceivedAt, string? AppVersion,
    int DriverProblems, int HardwareFaults, int Unresolved, int Proposed, int Blocked, int Installed, int Failed, bool AnalysisOnly, bool Healthy,
    int? HealthScore = null);

/// <summary>Document JSON brut (politique de pilotes, profil d'imprimantes) et sa date de mise à jour.</summary>
public sealed record StoredDocument(string Json, DateTimeOffset UpdatedAt);

/// <summary>Résultat de l'enregistrement d'un rapport (sert aux alertes et à l'accusé de réception).</summary>
/// <param name="MachineRef">Clé interne du poste.</param>
/// <param name="ReportId">Identifiant du rapport dans l'historique.</param>
/// <param name="IsNewMachine">Premier rapport de ce poste.</param>
/// <param name="IsLatest">Le rapport devient l'état actuel du poste (false : ancien rapport ajouté à l'historique).</param>
/// <param name="Previous">État actuel du poste avant ce rapport (null pour un nouveau poste).</param>
/// <param name="RenamedFrom">Ancien nom si le poste (reconnu par son identifiant) a changé de nom.</param>
/// <param name="Rollout">Effets sur la diffusion par anneaux (versions vues pour la première fois, installations, échecs).</param>
public sealed record SaveResult(long MachineRef, long ReportId, bool IsNewMachine, bool IsLatest, MachineReport? Previous, string? RenamedFrom,
    RolloutTrack? Rollout = null);

/// <summary>
/// Stockage SQLite (un seul fichier dans le volume de données) : postes, historique des rapports, documents, tâches à
/// distance, alertes, groupes, planifications, comptes et journal d'audit. Une connexion par opération (le pool de
/// Microsoft.Data.Sqlite les réutilise) ; journal WAL pour les lectures concurrentes ; les écritures fréquentes passent
/// une à une par un verrou du processus (pas d'attente active de SQLite entre écrivains).
/// <para>
/// Grands parcs (3.2) : les indicateurs de chaque poste sont précalculés à la réception de son rapport (colonnes de la
/// table machines, table machine_missing, appartenance aux groupes par règle dans group_auto_members) ; les rapports
/// normalisés sont compressés (gzip). Les listes et la synthèse ne relisent jamais les rapports.
/// </para>
/// </summary>
public sealed partial class ServerStore
{
    public const string PolicyKey = "policy";
    public const string PrinterProfileKey = "printer-profile";
    internal const int SchemaVersion = 6;

    private readonly string _connectionString;
    private readonly int _historyPerMachine;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Version du schéma trouvée à l'ouverture (avant migration) et durée de la migration (0 si aucune).</summary>
    public int OpenedSchemaVersion { get; private set; }
    public TimeSpan MigrationDuration { get; private set; }

    public ServerStore(ServerOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _historyPerMachine = options.HistoryPerMachine;
        DatabasePath = options.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open(bool foreignKeys = true)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        // synchronous=NORMAL : en mode WAL, la base reste toujours cohérente ; seule une coupure de courant peut faire perdre
        // les toutes dernières écritures (un rapport renvoyé au passage suivant). Gain d'écriture important.
        pragma.CommandText = $"PRAGMA foreign_keys = {(foreignKeys ? "ON" : "OFF")}; PRAGMA busy_timeout = 10000; " +
                             "PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA journal_size_limit = 67108864;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Transaction d'écriture (BEGIN IMMEDIATE) précédée du verrou d'écriture du processus : les écrivains attendent leur
    /// tour dans l'ordre, sans l'attente active (paliers de 1 à 100 ms) de SQLite qui ferait s'effondrer le débit.
    /// </summary>
    private WriteScope BeginWrite(SqliteConnection c)
    {
        if (!_writeGate.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Base de données occupée (écriture en attente depuis 30 s).");
        try
        {
            return new WriteScope(this, c.BeginTransaction(deferred: false));
        }
        catch
        {
            _writeGate.Release();
            throw;
        }
    }

    private sealed class WriteScope(ServerStore store, SqliteTransaction tx) : IDisposable
    {
        private bool _disposed;
        public void Commit() => tx.Commit();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { tx.Dispose(); }
            finally { store._writeGate.Release(); }
        }
    }

    // ------------------------------------------------------------------ schéma

    /// <summary>Tables de la version 2 (clé interne des postes, identifiant machine, tâches, alertes).</summary>
    private const string SchemaV2 = """
        CREATE TABLE IF NOT EXISTS machines (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
            machine_id    TEXT UNIQUE,
            last_seen     TEXT NOT NULL,
            last_report   TEXT NOT NULL,
            summary_json  TEXT NOT NULL,
            raw_report    BLOB,
            agent_seen    TEXT
        );
        CREATE TABLE IF NOT EXISTS reports (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            report_at     TEXT NOT NULL,
            received_at   TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_reports_machine ON reports(machine_ref, report_at DESC);
        CREATE TABLE IF NOT EXISTS documents (
            key           TEXT NOT NULL PRIMARY KEY,
            json          TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS jobs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            type          TEXT NOT NULL,
            parameters    TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            created_by    TEXT NOT NULL,
            state         TEXT NOT NULL,
            started_at    TEXT,
            finished_at   TEXT,
            message       TEXT,
            exit_code     INTEGER,
            report_id     INTEGER,
            log           TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_jobs_machine ON jobs(machine_ref, state);
        CREATE INDEX IF NOT EXISTS ix_jobs_state ON jobs(state, created_at);
        CREATE TABLE IF NOT EXISTS alert_log (
            subject       TEXT NOT NULL,
            reason        TEXT NOT NULL,
            last_sent     TEXT NOT NULL,
            PRIMARY KEY (subject, reason)
        );
        CREATE TABLE IF NOT EXISTS alert_events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at    TEXT NOT NULL,
            machine       TEXT,
            reason        TEXT NOT NULL,
            title         TEXT NOT NULL,
            status        TEXT NOT NULL,
            detail        TEXT
        );
        """;

    /// <summary>Tables ajoutées par la version 3 (console 3.1).</summary>
    private const string SchemaV3 = """
        CREATE TABLE IF NOT EXISTS machine_groups (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
            description   TEXT,
            rules_json    TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS group_members (
            group_id      INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,
            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            PRIMARY KEY (group_id, machine_ref)
        );
        CREATE INDEX IF NOT EXISTS ix_group_members_machine ON group_members(machine_ref);
        CREATE TABLE IF NOT EXISTS schedules (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            name           TEXT NOT NULL,
            job_type       TEXT NOT NULL,
            dry_run        INTEGER NOT NULL DEFAULT 0,
            group_id       INTEGER REFERENCES machine_groups(id),
            frequency      TEXT NOT NULL,
            days           TEXT NOT NULL,
            time           TEXT NOT NULL,
            window_minutes INTEGER,
            enabled        INTEGER NOT NULL DEFAULT 1,
            created_at     TEXT NOT NULL,
            created_by     TEXT NOT NULL,
            updated_at     TEXT NOT NULL,
            next_run       TEXT,
            last_run       TEXT,
            last_result    TEXT
        );
        CREATE TABLE IF NOT EXISTS users (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            login               TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name        TEXT,
            role                TEXT NOT NULL,
            password_hash       TEXT NOT NULL,
            enabled             INTEGER NOT NULL DEFAULT 1,
            created_at          TEXT NOT NULL,
            updated_at          TEXT NOT NULL,
            password_changed_at TEXT NOT NULL,
            last_login          TEXT
        );
        CREATE TABLE IF NOT EXISTS sessions (
            id_hash       TEXT NOT NULL PRIMARY KEY,
            user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            csrf          TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            last_seen     TEXT NOT NULL,
            expires_at    TEXT NOT NULL,
            ip            TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_user ON sessions(user_id);
        CREATE TABLE IF NOT EXISTS audit (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            at            TEXT NOT NULL,
            actor         TEXT NOT NULL,
            role          TEXT,
            action        TEXT NOT NULL,
            target        TEXT,
            detail        TEXT,
            ip            TEXT,
            success       INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);
        """;

    /// <summary>
    /// Version 4 (console 3.2, grands parcs) : indicateurs précalculés des postes et des rapports, rapports normalisés
    /// compressés, matériel sans pilote par poste, appartenance aux groupes par règle, compteur de révision du parc
    /// (invalidation des synthèses mises en cache), index des requêtes fréquentes.
    /// <para>
    /// La table machines est reconstruite (procédure de modification de table de SQLite : nouvelle table, copie, suppression,
    /// renommage ; clés, identifiants et références conservés) : ses lignes ne contiennent plus que des colonnes courtes,
    /// les données volumineuses (rapport brut, rapport normalisé) passent dans machine_data. Lire les indicateurs de
    /// 20 000 postes ne parcourt ainsi que quelques Mo, au lieu de traverser les chaînes de débordement des rapports.
    /// </para>
    /// </summary>
    private const string MachinesV4 = """
        CREATE TABLE machines_v4 (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT NOT NULL UNIQUE COLLATE NOCASE,
            machine_id      TEXT UNIQUE,
            last_seen       TEXT NOT NULL,
            last_report     TEXT NOT NULL,
            agent_seen      TEXT,
            last_ip         TEXT,
            report_ts       TEXT,
            sys_machine_id  TEXT,
            manufacturer    TEXT,
            model           TEXT,
            friendly_model  TEXT,
            display_model   TEXT NOT NULL DEFAULT '',
            serial          TEXT,
            os_caption      TEXT,
            os_display      TEXT,
            os_version      TEXT,
            architecture    TEXT,
            app_version     TEXT,
            report_count    INTEGER NOT NULL DEFAULT 0,
            device_count    INTEGER NOT NULL DEFAULT 0,
            driver_problems INTEGER NOT NULL DEFAULT 0,
            hardware_faults INTEGER NOT NULL DEFAULT 0,
            unresolved      INTEGER NOT NULL DEFAULT 0,
            proposed        INTEGER NOT NULL DEFAULT 0,
            blocked         INTEGER NOT NULL DEFAULT 0,
            installed       INTEGER NOT NULL DEFAULT 0,
            failed          INTEGER NOT NULL DEFAULT 0,
            source_errors   INTEGER NOT NULL DEFAULT 0,
            analysis_only   INTEGER NOT NULL DEFAULT 1,
            reboot_required INTEGER NOT NULL DEFAULT 0,
            healthy         INTEGER NOT NULL DEFAULT 1,
            health_score    INTEGER,
            health_grade    TEXT,
            health_critical INTEGER NOT NULL DEFAULT 0,
            health_warnings INTEGER NOT NULL DEFAULT 0,
            dangerous_driver INTEGER NOT NULL DEFAULT 0,
            rev             INTEGER NOT NULL DEFAULT 0,
            summary_json    TEXT NOT NULL DEFAULT '',
            raw_report      BLOB
        );
        """;

    private const string SchemaV4Reports = """
        ALTER TABLE reports ADD COLUMN summary_gz BLOB;
        ALTER TABLE reports ADD COLUMN report_ts TEXT;
        ALTER TABLE reports ADD COLUMN app_version TEXT;
        ALTER TABLE reports ADD COLUMN driver_problems INTEGER;
        ALTER TABLE reports ADD COLUMN hardware_faults INTEGER;
        ALTER TABLE reports ADD COLUMN unresolved INTEGER;
        ALTER TABLE reports ADD COLUMN proposed INTEGER;
        ALTER TABLE reports ADD COLUMN blocked INTEGER;
        ALTER TABLE reports ADD COLUMN installed INTEGER;
        ALTER TABLE reports ADD COLUMN failed INTEGER;
        ALTER TABLE reports ADD COLUMN analysis_only INTEGER;
        ALTER TABLE reports ADD COLUMN healthy INTEGER;
        ALTER TABLE reports ADD COLUMN health_score INTEGER;
        """;

    private const string SchemaV4Tables = """
        CREATE TABLE IF NOT EXISTS meta (
            key           TEXT NOT NULL PRIMARY KEY,
            value         INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO meta(key, value) VALUES('fleet_rev', 1);
        CREATE TABLE IF NOT EXISTS machine_data (
            machine_ref   INTEGER PRIMARY KEY REFERENCES machines(id) ON DELETE CASCADE,
            summary_gz    BLOB,
            raw_report    BLOB
        );
        CREATE TABLE IF NOT EXISTS machine_missing (
            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            ord           INTEGER NOT NULL,
            hw_key        TEXT NOT NULL,
            name          TEXT NOT NULL,
            error_code    INTEGER NOT NULL,
            status_text   TEXT,
            hint          TEXT,
            only_blocked  INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (machine_ref, ord)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS group_auto_members (
            group_id      INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,
            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            PRIMARY KEY (group_id, machine_ref)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_group_auto_members_machine ON group_auto_members(machine_ref);
        CREATE INDEX IF NOT EXISTS ix_jobs_state_finished ON jobs(state, finished_at);
        CREATE INDEX IF NOT EXISTS ix_jobs_pending_window ON jobs(not_after) WHERE state = 'pending' AND not_after IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_reports_at ON reports(report_at);
        CREATE INDEX IF NOT EXISTS ix_reports_legacy ON reports(id) WHERE report_ts IS NULL;
        """;

    /// <summary>Index et triggers de révision (créés après la reconstruction de la table machines).</summary>
    private const string SchemaV4Triggers = """
        CREATE INDEX IF NOT EXISTS ix_machines_last_seen ON machines(last_seen);
        CREATE INDEX IF NOT EXISTS ix_machines_status ON machines(healthy, failed DESC, driver_problems DESC);
        CREATE INDEX IF NOT EXISTS ix_machines_rev ON machines(rev);
        -- Chaque poste modifié reçoit la nouvelle révision du parc (rev) : le cache des indicateurs ne relit que ces postes.
        CREATE TRIGGER IF NOT EXISTS trg_rev_machines_insert AFTER INSERT ON machines
            BEGIN
                UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev';
                UPDATE machines SET rev = (SELECT value FROM meta WHERE key = 'fleet_rev') WHERE id = NEW.id;
            END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_machines_delete AFTER DELETE ON machines
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_machines_update
            AFTER UPDATE OF name, machine_id, last_seen, last_report, report_ts, last_ip, report_count, healthy ON machines
            BEGIN
                UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev';
                UPDATE machines SET rev = (SELECT value FROM meta WHERE key = 'fleet_rev') WHERE id = NEW.id;
            END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_groups_insert AFTER INSERT ON machine_groups
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_groups_update AFTER UPDATE ON machine_groups
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_groups_delete AFTER DELETE ON machine_groups
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_members_insert AFTER INSERT ON group_members
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_members_delete AFTER DELETE ON group_members
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_auto_insert AFTER INSERT ON group_auto_members
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER IF NOT EXISTS trg_rev_auto_delete AFTER DELETE ON group_auto_members
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        """;

    private void Migrate()
    {
        using var c = Open(foreignKeys: false);
        Execute(c, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        OpenedSchemaVersion = version;
        if (version >= SchemaVersion) return;

        var sw = Stopwatch.StartNew();
        using var tx = c.BeginTransaction();
        if (version < 3)
        {
            if (version == 1)
            {
                // Version 1 (console 2.x) : postes indexés par leur nom. On passe à une clé interne pour qu'un poste
                // renommé reste le même poste ; l'historique est conservé à l'identique.
                Execute(c, """
                    DROP INDEX IF EXISTS ix_reports_machine;
                    ALTER TABLE machines RENAME TO machines_v1;
                    ALTER TABLE reports RENAME TO reports_v1;
                    """);
                Execute(c, SchemaV2);
                Execute(c, """
                    INSERT INTO machines(name, last_seen, last_report, summary_json, raw_report)
                        SELECT name, last_seen, last_report, summary_json, raw_report FROM machines_v1;
                    INSERT INTO reports(id, machine_ref, report_at, received_at, summary_json)
                        SELECT r.id, m.id, r.report_at, r.received_at, r.summary_json
                        FROM reports_v1 r JOIN machines m ON m.name = r.machine;
                    DROP TABLE reports_v1;
                    DROP TABLE machines_v1;
                    """);
            }
            else if (version < 2)
            {
                Execute(c, SchemaV2);
            }
            // Version 3 (console 3.1) : groupes, planifications, comptes, sessions, journal d'audit ; adresse IP du dernier
            // rapport (règles « sous-réseau ») et fenêtre de maintenance des tâches. Ajouts uniquement : rien n'est perdu.
            Execute(c, """
                ALTER TABLE machines ADD COLUMN last_ip TEXT;
                ALTER TABLE jobs ADD COLUMN not_after TEXT;
                ALTER TABLE jobs ADD COLUMN schedule_id INTEGER;
                """);
            Execute(c, SchemaV3);
        }
        if (version < 4)
        {
            // Version 4 (console 3.2) : colonnes et tables ajoutées, indicateurs des postes calculés tout de suite (listes et
            // synthèse correctes dès le démarrage) ; l'historique des rapports est compressé ensuite, par lots, en arrière-plan.
            Execute(c, SchemaV4Reports);
            Execute(c, SchemaV4Tables);
            RebuildMachinesTable(c);
            Execute(c, SchemaV4Triggers);
            BackfillMachines(c);
        }
        // Version 5 (console 4.0) : diffusion par anneaux et suspension automatique. Tables ajoutées uniquement (aucune donnée
        // existante n'est réécrite : migration instantanée) ; les versions sont suivies à partir des rapports suivants.
        Execute(c, SchemaV5);
        // Version 6 (console 4.0.1) : identité des propositions sans numéro de version et matériel visé (même pilote d'une
        // source à l'autre). Table des versions reconstruite à l'identique (mêmes identifiants), clé unique élargie.
        if (version < 6) RebuildRolloutVersionsV6(c);
        Execute(c, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
        MigrationDuration = sw.Elapsed;
    }

    /// <summary>
    /// Migration 4 : reconstruit la table machines (colonnes courtes d'abord, mêmes identifiants), le rapport brut passant
    /// dans machine_data. Les clés étrangères sont désactivées pendant la migration : rapports, tâches et groupes gardent
    /// leurs références (même nom de table, mêmes identifiants) ; la séquence AUTOINCREMENT est conservée (un identifiant
    /// de poste supprimé n'est jamais réutilisé).
    /// </summary>
    private static void RebuildMachinesTable(SqliteConnection c)
    {
        var sequence = Scalar(c, "SELECT seq FROM sqlite_sequence WHERE name = 'machines';");
        Execute(c, MachinesV4);
        Execute(c, "INSERT INTO machine_data(machine_ref, raw_report) SELECT id, raw_report FROM machines;");
        // Chaque ligne est écrite complète (indicateurs calculés depuis son dernier rapport) avant la suivante, dans l'ordre
        // des identifiants : la nouvelle table est compacte, sans fragmentation.
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT id, name, machine_id, last_seen, last_report, agent_seen, last_ip, summary_json FROM machines ORDER BY id;";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                Execute(c, """
                    INSERT INTO machines_v4(id, name, machine_id, last_seen, last_report, agent_seen, last_ip) VALUES($id, $n, $mid, $seen, $at, $agent, $ip);
                    """, ("$id", id), ("$n", r.GetString(1)), ("$mid", r.IsDBNull(2) ? null : r.GetString(2)), ("$seen", r.GetString(3)),
                    ("$at", r.GetString(4)), ("$agent", r.IsDBNull(5) ? null : r.GetString(5)), ("$ip", r.IsDBNull(6) ? null : r.GetString(6)));
                var report = Deserialize(r.GetString(7));
                WriteMachineSummary(c, id, report, CompressSummary(report), "machines_v4");
            }
        }
        Execute(c, """
            DROP TABLE machines;
            ALTER TABLE machines_v4 RENAME TO machines;
            """);
        if (sequence is not null)
            Execute(c, """
                UPDATE sqlite_sequence SET seq = MAX(seq, $s) WHERE name = 'machines';
                INSERT INTO sqlite_sequence(name, seq) SELECT 'machines', $s WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'machines');
                """, ("$s", sequence));
    }

    /// <summary>Migration 4 : indicateurs, résumé compressé, matériel sans pilote et groupes par règle de chaque poste.</summary>
    private static void BackfillMachines(SqliteConnection c)
    {
        var rows = new List<(long Id, string Json)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, summary_json FROM machines WHERE summary_json <> '';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (id, json) in rows)
        {
            var report = Deserialize(json);
            WriteMachineSummary(c, id, report, CompressSummary(report));
        }
        Execute(c, "UPDATE machines SET report_count = (SELECT COUNT(*) FROM reports r WHERE r.machine_ref = machines.id);");
        var groups = LoadRuleGroups(c);
        foreach (var g in groups) RebuildAutoMembers(c, g.Id, g.Rules);
    }

    // ------------------------------------------------------------------ rapports

    /// <summary>
    /// Enregistre un rapport. Le poste est reconnu d'abord par son identifiant machine (MachineGuid), sinon par son nom :
    /// un poste renommé reste le même poste ; un poste connu par son seul nom (rapports antérieurs à la 3.0) reçoit
    /// l'identifiant au premier rapport qui le contient. Le « dernier rapport » d'un poste est celui dont l'horodatage
    /// est le plus récent : renvoyer un ancien rapport (reprise d'un partage) l'ajoute à l'historique sans écraser l'état actuel.
    /// </summary>
    /// <param name="rawGzip">Rapport brut déjà compressé (corps gzip envoyé par le poste) : conservé tel quel, sans recompresser.</param>
    public SaveResult SaveReport(MachineReport report, string rawJson, DateTimeOffset receivedAt, string? remoteIp = null, byte[]? rawGzip = null)
    {
        // Tout ce qui coûte (sérialisation, compression) est fait avant de prendre le verrou d'écriture.
        var summaryGz = CompressSummary(report);
        var raw = rawGzip ?? Compress(rawJson);
        // Dernier contact : date du rapport, bornée par la date de réception (horloge du poste en avance).
        var seen = report.Timestamp < receivedAt ? report.Timestamp : receivedAt;
        var machineId = report.System.MachineId;
        var reportAt = Iso(report.Timestamp);

        using var c = Open();
        using var tx = BeginWrite(c);
        var (machineRef, renamedFrom, previous, displaced) = ResolveForReport(c, report.Name, machineId, reportAt);
        bool isLatest;
        var isNew = machineRef is null;
        if (machineRef is null)
        {
            machineRef = Convert.ToInt64(Scalar(c, """
                INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json) VALUES($n, $mid, $seen, $at, '')
                RETURNING id;
                """, ("$n", report.Name), ("$mid", machineId), ("$seen", Iso(seen)), ("$at", reportAt)),
                CultureInfo.InvariantCulture);
            isLatest = true;
        }
        else
        {
            var current = (string)Scalar(c, "SELECT last_report FROM machines WHERE id = $id;", ("$id", machineRef))!;
            isLatest = string.CompareOrdinal(reportAt, current) >= 0;
            if (isLatest)
                Execute(c, "UPDATE machines SET last_report = $at, summary_json = '', raw_report = NULL WHERE id = $id;", ("$id", machineRef), ("$at", reportAt));
            Execute(c, "UPDATE machines SET last_seen = $seen WHERE id = $id AND last_seen < $seen;", ("$id", machineRef), ("$seen", Iso(seen)));
        }
        if (isLatest)
        {
            Execute(c, """
                INSERT INTO machine_data(machine_ref, raw_report) VALUES($id, $raw)
                ON CONFLICT(machine_ref) DO UPDATE SET raw_report = excluded.raw_report;
                """, ("$id", machineRef), ("$raw", raw));
            WriteMachineSummary(c, machineRef.Value, report, summaryGz);
        }
        // Adresse d'envoi du rapport le plus récent : sert aux règles de groupe « sous-réseau » (jamais transmise aux postes).
        if (remoteIp is not null && isLatest)
            Execute(c, "UPDATE machines SET last_ip = $ip WHERE id = $id AND last_ip IS NOT $ip;", ("$id", machineRef), ("$ip", remoteIp));

        var reportId = Convert.ToInt64(Scalar(c, """
            INSERT INTO reports(machine_ref, report_at, received_at, summary_json, summary_gz, report_ts, app_version, driver_problems,
                                hardware_faults, unresolved, proposed, blocked, installed, failed, analysis_only, healthy, health_score)
            VALUES($m, $at, $rcv, '', $gz, $ts, $app, $dp, $hf, $un, $pr, $bl, $in, $fa, $ao, $he, $hs) RETURNING id;
            """, ("$m", machineRef), ("$at", reportAt), ("$rcv", Iso(receivedAt)), ("$gz", summaryGz), ("$ts", TimestampText(report.Timestamp)),
            ("$app", report.AppVersion), ("$dp", report.DriverProblems), ("$hf", report.HardwareFaults), ("$un", report.Unresolved),
            ("$pr", report.Proposed), ("$bl", report.Blocked), ("$in", report.Installed), ("$fa", report.Failed),
            ("$ao", report.AnalysisOnly ? 1 : 0), ("$he", report.Healthy ? 1 : 0), ("$hs", report.Health?.Score)), CultureInfo.InvariantCulture);
        // Ne garde que les N rapports les plus récents du poste.
        Execute(c, """
            DELETE FROM reports WHERE machine_ref = $m AND id NOT IN
              (SELECT id FROM reports WHERE machine_ref = $m ORDER BY report_at DESC, id DESC LIMIT $keep);
            """, ("$m", machineRef), ("$keep", _historyPerMachine));
        Execute(c, "UPDATE machines SET report_count = (SELECT COUNT(*) FROM reports WHERE machine_ref = $m) WHERE id = $m;", ("$m", machineRef));

        // Groupes par règle : seul ce poste (et celui qui a dû céder son nom) peut avoir changé d'appartenance.
        if (isLatest || renamedFrom is not null || displaced is not null)
        {
            var groups = LoadRuleGroups(c);
            if (isLatest || renamedFrom is not null) RefreshAutoMembership(c, machineRef.Value, groups);
            if (displaced is { } other) RefreshAutoMembership(c, other, groups);
        }
        // Diffusion par anneaux : première détection des versions proposées, installations, régressions (après les groupes :
        // l'anneau du poste en dépend).
        var rollout = TrackRollout(c, machineRef.Value, report, receivedAt, isLatest);
        tx.Commit();
        return new SaveResult(machineRef.Value, reportId, isNew, isLatest, previous, renamedFrom, rollout);
    }

    /// <summary>Colonnes d'indicateurs d'un poste et son matériel sans pilote (dernier rapport).</summary>
    private static void WriteMachineSummary(SqliteConnection c, long machineRef, MachineReport r, byte[] summaryGz, string table = "machines")
    {
        var s = r.System;
        Execute(c, $"""
            INSERT INTO machine_data(machine_ref, summary_gz) VALUES($id, $gz) ON CONFLICT(machine_ref) DO UPDATE SET summary_gz = excluded.summary_gz;
            UPDATE {table} SET summary_json = '', sys_machine_id = $smid, report_ts = $ts, manufacturer = $man, model = $mod,
                friendly_model = $fm, display_model = $dm, serial = $sn, os_caption = $osc, os_display = $osd, os_version = $osv,
                architecture = $arch, app_version = $app, device_count = $dc, driver_problems = $dp, hardware_faults = $hf, unresolved = $un,
                proposed = $pr, blocked = $bl, installed = $in, failed = $fa, source_errors = $se, analysis_only = $ao, reboot_required = $rr,
                healthy = $he, health_score = $hs, health_grade = $hg, health_critical = $hc, health_warnings = $hw, dangerous_driver = $dd
            WHERE id = $id;
            """, ("$id", machineRef), ("$gz", summaryGz), ("$smid", s.MachineId), ("$ts", TimestampText(r.Timestamp)), ("$man", s.Manufacturer),
            ("$mod", s.Model), ("$fm", s.FriendlyModel), ("$dm", s.DisplayModel), ("$sn", s.SerialNumber), ("$osc", s.OsCaption),
            ("$osd", s.OsDisplayVersion), ("$osv", s.OsVersion), ("$arch", s.Architecture), ("$app", r.AppVersion), ("$dc", r.DeviceCount),
            ("$dp", r.DriverProblems), ("$hf", r.HardwareFaults), ("$un", r.Unresolved), ("$pr", r.Proposed), ("$bl", r.Blocked),
            ("$in", r.Installed), ("$fa", r.Failed), ("$se", r.SourceErrors.Count), ("$ao", r.AnalysisOnly ? 1 : 0),
            ("$rr", r.RebootRequired ? 1 : 0), ("$he", r.Healthy ? 1 : 0), ("$hs", r.Health?.Score), ("$hg", r.Health?.Grade),
            ("$hc", r.Health?.Critical ?? 0), ("$hw", r.Health?.Warnings ?? 0), ("$dd", r.Health?.HasDangerousDriver == true ? 1 : 0));
        Execute(c, "DELETE FROM machine_missing WHERE machine_ref = $m;", ("$m", machineRef));
        var ord = 0;
        foreach (var d in MachineSummary.MissingDevices(r))
            Execute(c, """
                INSERT INTO machine_missing(machine_ref, ord, hw_key, name, error_code, status_text, hint, only_blocked)
                VALUES($m, $o, $k, $n, $e, $st, $h, $b);
                """, ("$m", machineRef), ("$o", ord++), ("$k", d.AggregationKey), ("$n", d.Name), ("$e", d.ErrorCode), ("$st", d.StatusText),
                ("$h", d.Hint), ("$b", d.OnlyBlockedCandidates ? 1 : 0));
    }

    /// <summary>
    /// Retrouve le poste d'un rapport. Règles : 1) même identifiant machine → même poste (renommé si le rapport est le
    /// plus récent ; un ancien enregistrement qui portait déjà ce nom est mis de côté sous « NOM~id ») ; 2) même nom →
    /// même poste, qui adopte l'identifiant (poste antérieur à la 3.0, ou Windows réinstallé : nouvel identifiant) ;
    /// 3) sinon nouveau poste.
    /// </summary>
    private static (long? Id, string? RenamedFrom, MachineReport? Previous, long? Displaced) ResolveForReport(SqliteConnection c, string name,
        string? machineId, string reportAt)
    {
        if (machineId is not null &&
            Row(c, """
                SELECT m.id, m.name, m.last_report, m.summary_json, d.summary_gz FROM machines m LEFT JOIN machine_data d ON d.machine_ref = m.id
                WHERE m.machine_id = $mid;
                """, ("$mid", machineId)) is { } byId)
        {
            var (id, currentName, lastReport) = ((long)byId[0]!, (string)byId[1]!, (string)byId[2]!);
            string? renamed = null;
            long? displaced = null;
            if (!string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase) && string.CompareOrdinal(reportAt, lastReport) >= 0)
            {
                displaced = Scalar(c, "SELECT id FROM machines WHERE name = $n AND id <> $id;", ("$n", name), ("$id", id)) as long?;
                if (displaced is not null)
                    Execute(c, "UPDATE machines SET name = name || '~' || id WHERE id = $other;", ("$other", displaced));
                Execute(c, "UPDATE machines SET name = $n WHERE id = $id;", ("$n", name), ("$id", id));
                renamed = currentName;
            }
            return (id, renamed, ReadSummary(byId[4] as byte[], byId[3] as string), displaced);
        }
        if (Row(c, """
                SELECT m.id, m.machine_id, m.summary_json, d.summary_gz FROM machines m LEFT JOIN machine_data d ON d.machine_ref = m.id
                WHERE m.name = $n;
                """, ("$n", name)) is { } byName)
        {
            var id = (long)byName[0]!;
            if (machineId is not null && !string.Equals(byName[1] as string, machineId, StringComparison.Ordinal))
                Execute(c, "UPDATE machines SET machine_id = $mid WHERE id = $id;", ("$mid", machineId), ("$id", id));
            return (id, null, ReadSummary(byName[3] as byte[], byName[2] as string), null);
        }
        return (null, null, null, null);
    }

    private const string MachineColumns = """
        m.name, m.last_seen, m.summary_json, (SELECT d.summary_gz FROM machine_data d WHERE d.machine_ref = m.id), m.report_count, m.id,
        m.machine_id, m.agent_seen, m.last_ip
        """;

    /// <summary>Tous les postes avec leur dernier rapport normalisé complet (coûteux sur un grand parc : réservé aux tests et outils).</summary>
    public List<StoredMachine> GetMachines()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MachineColumns} FROM machines m ORDER BY m.name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredMachine>();
        while (reader.Read()) list.Add(ReadMachine(reader));
        return list;
    }

    public StoredMachine? GetMachine(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MachineColumns} FROM machines m WHERE m.name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMachine(reader) : null;
    }

    public StoredMachine? GetMachine(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MachineColumns} FROM machines m WHERE m.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMachine(reader) : null;
    }

    public List<HistoryEntry> GetHistory(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Indicateurs précalculés ; un rapport pas encore migré (base 3.1, compression en cours) est relu depuis son JSON.
        cmd.CommandText = """
            SELECT r.id, r.received_at, r.report_ts, r.app_version, r.driver_problems, r.hardware_faults, r.unresolved, r.proposed, r.blocked,
                   r.installed, r.failed, r.analysis_only, r.healthy, r.health_score,
                   CASE WHEN r.report_ts IS NULL THEN r.summary_json END, CASE WHEN r.report_ts IS NULL THEN r.summary_gz END
            FROM reports r JOIN machines m ON m.id = r.machine_ref
            WHERE m.name = $n ORDER BY r.report_at DESC, r.id DESC;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (reader.Read())
        {
            if (reader.IsDBNull(2))
            {
                var r = ReadSummary(reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15), reader.IsDBNull(14) ? null : reader.GetString(14));
                list.Add(new HistoryEntry(reader.GetInt64(0), r.Timestamp, ParseIso(reader.GetString(1)), r.AppVersion,
                    r.DriverProblems, r.HardwareFaults, r.Unresolved, r.Proposed, r.Blocked, r.Installed, r.Failed, r.AnalysisOnly, r.Healthy,
                    r.Health?.Score));
                continue;
            }
            list.Add(new HistoryEntry(reader.GetInt64(0), ParseTimestamp(reader.GetString(2)), ParseIso(reader.GetString(1)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11) != 0, reader.GetInt32(12) != 0,
                reader.IsDBNull(13) ? null : reader.GetInt32(13)));
        }
        return list;
    }

    public MachineReport? GetHistoricReport(string name, long id)
    {
        using var c = Open();
        return Row(c, "SELECT r.summary_json, r.summary_gz FROM reports r JOIN machines m ON m.id = r.machine_ref WHERE m.name = $n AND r.id = $id;",
            ("$n", name), ("$id", id)) is { } row ? ReadSummary(row[1] as byte[], row[0] as string) : null;
    }

    /// <summary>Un rapport appartient-il à ce poste ? (lien entre une tâche et le rapport qu'elle a produit)</summary>
    public bool ReportBelongsTo(long reportId, long machineRef)
    {
        using var c = Open();
        return Scalar(c, "SELECT 1 FROM reports WHERE id = $id AND machine_ref = $m;", ("$id", reportId), ("$m", machineRef)) is not null;
    }

    /// <summary>Dernier rapport brut du poste (tel qu'envoyé), réutilisable avec « spokapilote pack ».</summary>
    public string? GetRawReport(string name)
    {
        using var c = Open();
        return Scalar(c, """
            SELECT COALESCE((SELECT d.raw_report FROM machine_data d WHERE d.machine_ref = m.id), m.raw_report) FROM machines m WHERE m.name = $n;
            """, ("$n", name)) is byte[] raw ? Decompress(raw) : null;
    }

    public bool DeleteMachine(string name)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var deleted = Execute(c, "DELETE FROM machines WHERE name = $n;", ("$n", name)) > 0;
        tx.Commit();
        return deleted;
    }

    // ------------------------------------------------------------------ documents (politique, profil d'imprimantes, réglages)

    public StoredDocument? GetDocument(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, updated_at FROM documents WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new StoredDocument(reader.GetString(0), ParseIso(reader.GetString(1))) : null;
    }

    public void SetDocument(string key, string json, DateTimeOffset updatedAt)
    {
        using var c = Open();
        Execute(c, """
            INSERT INTO documents(key, json, updated_at) VALUES($k, $j, $u)
            ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at;
            """, ("$k", key), ("$j", json), ("$u", Iso(updatedAt)));
    }

    public bool DeleteDocument(string key)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM documents WHERE key = $k;", ("$k", key)) > 0;
    }

    public bool IsHealthy()
    {
        try
        {
            using var c = Open();
            return Convert.ToInt32(Scalar(c, "SELECT 1;"), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ utilitaires

    private static StoredMachine ReadMachine(SqliteDataReader reader) => new(
        reader.GetString(0),
        ParseIso(reader.GetString(1)),
        reader.GetInt32(4),
        ReadSummary(reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3), reader.GetString(2)),
        reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : ParseIso(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static MachineReport Deserialize(string json) => JsonSerializer.Deserialize<MachineReport>(json, Json)!;

    /// <summary>Rapport normalisé : forme compressée (3.2) si présente, sinon JSON en clair (base 3.1 pas encore migrée).</summary>
    internal static MachineReport ReadSummary(byte[]? gz, string? json) =>
        gz is { Length: > 0 } ? JsonSerializer.Deserialize<MachineReport>(DecompressBytes(gz), Json)! : Deserialize(json!);

    internal static byte[] CompressSummary(MachineReport report) => CompressBytes(JsonSerializer.SerializeToUtf8Bytes(report, Json));

    internal static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string s) =>
        // Format écrit par Iso() : lecture exacte (bien plus rapide), lecture tolérante sinon.
        DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact)
            ? exact
            : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Horodatage du rapport avec son décalage d'origine (l'API le renvoie tel que le poste l'a envoyé).</summary>
    internal static string TimestampText(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTimestamp(string s) =>
        DateTimeOffset.TryParseExact(s, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var exact)
            ? exact
            : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static int Execute(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Première ligne d'une requête (null si aucune), valeurs DBNull converties en null.</summary>
    private static object?[]? Row(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new object?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }

    private static byte[] Compress(string text) => CompressBytes(Encoding.UTF8.GetBytes(text));

    private static byte[] CompressBytes(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 6 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] DecompressBytes(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 6);
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string Decompress(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }
}
