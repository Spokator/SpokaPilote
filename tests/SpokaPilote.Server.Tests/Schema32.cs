namespace SpokaPilote.Server.Tests;

/// <summary>
/// Schéma exact d'une base créée par la console 3.2 (schéma 4) : copie figée de sqlite_master d'une base neuve ouverte par
/// le serveur à l'étiquette précédant la 4.0 (tables, index, triggers). Sert au test de migration vers le schéma 5 : NE PAS
/// MODIFIER (la base de test doit rester celle d'un serveur 3.2).
/// </summary>
internal static class Schema32
{
    public const string Sql = """
        CREATE TABLE reports (

            id            INTEGER PRIMARY KEY AUTOINCREMENT,

            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,

            report_at     TEXT NOT NULL,

            received_at   TEXT NOT NULL,

            summary_json  TEXT NOT NULL

        , summary_gz BLOB, report_ts TEXT, app_version TEXT, driver_problems INTEGER, hardware_faults INTEGER, unresolved INTEGER, proposed INTEGER, blocked INTEGER, installed INTEGER, failed INTEGER, analysis_only INTEGER, healthy INTEGER, health_score INTEGER);
        CREATE INDEX ix_reports_machine ON reports(machine_ref, report_at DESC);
        CREATE TABLE documents (

            key           TEXT NOT NULL PRIMARY KEY,

            json          TEXT NOT NULL,

            updated_at    TEXT NOT NULL

        );
        CREATE TABLE jobs (

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

        , not_after TEXT, schedule_id INTEGER);
        CREATE INDEX ix_jobs_machine ON jobs(machine_ref, state);
        CREATE INDEX ix_jobs_state ON jobs(state, created_at);
        CREATE TABLE alert_log (

            subject       TEXT NOT NULL,

            reason        TEXT NOT NULL,

            last_sent     TEXT NOT NULL,

            PRIMARY KEY (subject, reason)

        );
        CREATE TABLE alert_events (

            id            INTEGER PRIMARY KEY AUTOINCREMENT,

            created_at    TEXT NOT NULL,

            machine       TEXT,

            reason        TEXT NOT NULL,

            title         TEXT NOT NULL,

            status        TEXT NOT NULL,

            detail        TEXT

        );
        CREATE TABLE machine_groups (

            id            INTEGER PRIMARY KEY AUTOINCREMENT,

            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,

            description   TEXT,

            rules_json    TEXT NOT NULL,

            created_at    TEXT NOT NULL,

            updated_at    TEXT NOT NULL

        );
        CREATE TABLE group_members (

            group_id      INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,

            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,

            PRIMARY KEY (group_id, machine_ref)

        );
        CREATE INDEX ix_group_members_machine ON group_members(machine_ref);
        CREATE TABLE schedules (

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
        CREATE TABLE users (

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
        CREATE TABLE sessions (

            id_hash       TEXT NOT NULL PRIMARY KEY,

            user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,

            csrf          TEXT NOT NULL,

            created_at    TEXT NOT NULL,

            last_seen     TEXT NOT NULL,

            expires_at    TEXT NOT NULL,

            ip            TEXT

        );
        CREATE INDEX ix_sessions_user ON sessions(user_id);
        CREATE TABLE audit (

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
        CREATE INDEX ix_audit_at ON audit(at);
        CREATE TABLE meta (

            key           TEXT NOT NULL PRIMARY KEY,

            value         INTEGER NOT NULL

        );
        CREATE TABLE machine_data (

            machine_ref   INTEGER PRIMARY KEY REFERENCES machines(id) ON DELETE CASCADE,

            summary_gz    BLOB,

            raw_report    BLOB

        );
        CREATE TABLE machine_missing (

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
        CREATE TABLE group_auto_members (

            group_id      INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,

            machine_ref   INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,

            PRIMARY KEY (group_id, machine_ref)

        ) WITHOUT ROWID;
        CREATE INDEX ix_group_auto_members_machine ON group_auto_members(machine_ref);
        CREATE INDEX ix_jobs_state_finished ON jobs(state, finished_at);
        CREATE INDEX ix_jobs_pending_window ON jobs(not_after) WHERE state = 'pending' AND not_after IS NOT NULL;
        CREATE INDEX ix_reports_at ON reports(report_at);
        CREATE INDEX ix_reports_legacy ON reports(id) WHERE report_ts IS NULL;
        CREATE TABLE "machines" (

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
        CREATE INDEX ix_machines_last_seen ON machines(last_seen);
        CREATE INDEX ix_machines_status ON machines(healthy, failed DESC, driver_problems DESC);
        CREATE INDEX ix_machines_rev ON machines(rev);
        CREATE TRIGGER trg_rev_machines_insert AFTER INSERT ON machines

            BEGIN

                UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev';

                UPDATE machines SET rev = (SELECT value FROM meta WHERE key = 'fleet_rev') WHERE id = NEW.id;

            END;
        CREATE TRIGGER trg_rev_machines_delete AFTER DELETE ON machines

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_machines_update

            AFTER UPDATE OF name, machine_id, last_seen, last_report, report_ts, last_ip, report_count, healthy ON machines

            BEGIN

                UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev';

                UPDATE machines SET rev = (SELECT value FROM meta WHERE key = 'fleet_rev') WHERE id = NEW.id;

            END;
        CREATE TRIGGER trg_rev_groups_insert AFTER INSERT ON machine_groups

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_groups_update AFTER UPDATE ON machine_groups

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_groups_delete AFTER DELETE ON machine_groups

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_members_insert AFTER INSERT ON group_members

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_members_delete AFTER DELETE ON group_members

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_auto_insert AFTER INSERT ON group_auto_members

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        CREATE TRIGGER trg_rev_auto_delete AFTER DELETE ON group_auto_members

            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'fleet_rev'; END;
        """;
}
