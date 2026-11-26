using System.Globalization;
using System.IO.Compression;
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
public sealed record SaveResult(long MachineRef, long ReportId, bool IsNewMachine, bool IsLatest, MachineReport? Previous, string? RenamedFrom);

/// <summary>
/// Stockage SQLite (un seul fichier dans le volume de données) : postes, historique des rapports, documents, tâches à
/// distance et alertes. Une connexion par opération (le pool de Microsoft.Data.Sqlite les réutilise) ; journal WAL
/// pour les lectures concurrentes.
/// </summary>
public sealed partial class ServerStore
{
    public const string PolicyKey = "policy";
    public const string PrinterProfileKey = "printer-profile";
    private const int SchemaVersion = 2;

    private readonly string _connectionString;
    private readonly int _historyPerMachine;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ServerStore(ServerOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _historyPerMachine = options.HistoryPerMachine;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open(bool foreignKeys = true)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA foreign_keys = {(foreignKeys ? "ON" : "OFF")}; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
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

    private void Migrate()
    {
        using var c = Open(foreignKeys: false);
        Execute(c, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
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
        else
        {
            Execute(c, SchemaV2);
        }
        Execute(c, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    // ------------------------------------------------------------------ rapports

    /// <summary>
    /// Enregistre un rapport. Le poste est reconnu d'abord par son identifiant machine (MachineGuid), sinon par son nom :
    /// un poste renommé reste le même poste ; un poste connu par son seul nom (rapports antérieurs à la 3.0) reçoit
    /// l'identifiant au premier rapport qui le contient. Le « dernier rapport » d'un poste est celui dont l'horodatage
    /// est le plus récent : renvoyer un ancien rapport (reprise d'un partage) l'ajoute à l'historique sans écraser l'état actuel.
    /// </summary>
    public SaveResult SaveReport(MachineReport report, string rawJson, DateTimeOffset receivedAt)
    {
        var summary = JsonSerializer.Serialize(report, Json);
        // Dernier contact : date du rapport, bornée par la date de réception (horloge du poste en avance).
        var seen = report.Timestamp < receivedAt ? report.Timestamp : receivedAt;
        var machineId = report.System.MachineId;

        using var c = Open();
        using var tx = c.BeginTransaction();
        var (machineRef, renamedFrom, previous) = ResolveForReport(c, report.Name, machineId, Iso(report.Timestamp));
        bool isLatest;
        var isNew = machineRef is null;
        if (machineRef is null)
        {
            machineRef = Convert.ToInt64(Scalar(c, """
                INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json, raw_report) VALUES($n, $mid, $seen, $at, $s, $raw)
                RETURNING id;
                """, ("$n", report.Name), ("$mid", machineId), ("$seen", Iso(seen)), ("$at", Iso(report.Timestamp)), ("$s", summary), ("$raw", Compress(rawJson))),
                CultureInfo.InvariantCulture);
            isLatest = true;
        }
        else
        {
            var current = (string)Scalar(c, "SELECT last_report FROM machines WHERE id = $id;", ("$id", machineRef))!;
            isLatest = string.CompareOrdinal(Iso(report.Timestamp), current) >= 0;
            if (isLatest)
                Execute(c, "UPDATE machines SET last_report = $at, summary_json = $s, raw_report = $raw WHERE id = $id;",
                    ("$id", machineRef), ("$at", Iso(report.Timestamp)), ("$s", summary), ("$raw", Compress(rawJson)));
            Execute(c, "UPDATE machines SET last_seen = MAX(last_seen, $seen) WHERE id = $id;", ("$id", machineRef), ("$seen", Iso(seen)));
        }

        var reportId = Convert.ToInt64(Scalar(c, """
            INSERT INTO reports(machine_ref, report_at, received_at, summary_json) VALUES($m, $at, $rcv, $s) RETURNING id;
            """, ("$m", machineRef), ("$at", Iso(report.Timestamp)), ("$rcv", Iso(receivedAt)), ("$s", summary)), CultureInfo.InvariantCulture);
        // Ne garde que les N rapports les plus récents du poste.
        Execute(c, """
            DELETE FROM reports WHERE machine_ref = $m AND id NOT IN
              (SELECT id FROM reports WHERE machine_ref = $m ORDER BY report_at DESC, id DESC LIMIT $keep);
            """, ("$m", machineRef), ("$keep", _historyPerMachine));
        tx.Commit();
        return new SaveResult(machineRef.Value, reportId, isNew, isLatest, previous, renamedFrom);
    }

    /// <summary>
    /// Retrouve le poste d'un rapport. Règles : 1) même identifiant machine → même poste (renommé si le rapport est le
    /// plus récent ; un ancien enregistrement qui portait déjà ce nom est mis de côté sous « NOM~id ») ; 2) même nom →
    /// même poste, qui adopte l'identifiant (poste antérieur à la 3.0, ou Windows réinstallé : nouvel identifiant) ;
    /// 3) sinon nouveau poste.
    /// </summary>
    private static (long? Id, string? RenamedFrom, MachineReport? Previous) ResolveForReport(SqliteConnection c, string name, string? machineId, string reportAt)
    {
        if (machineId is not null && Row(c, "SELECT id, name, last_report, summary_json FROM machines WHERE machine_id = $mid;", ("$mid", machineId)) is { } byId)
        {
            var (id, currentName, lastReport, json) = ((long)byId[0]!, (string)byId[1]!, (string)byId[2]!, (string)byId[3]!);
            string? renamed = null;
            if (!string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase) && string.CompareOrdinal(reportAt, lastReport) >= 0)
            {
                Execute(c, "UPDATE machines SET name = name || '~' || id WHERE name = $n AND id <> $id;", ("$n", name), ("$id", id));
                Execute(c, "UPDATE machines SET name = $n WHERE id = $id;", ("$n", name), ("$id", id));
                renamed = currentName;
            }
            return (id, renamed, Deserialize(json));
        }
        if (Row(c, "SELECT id, machine_id, summary_json FROM machines WHERE name = $n;", ("$n", name)) is { } byName)
        {
            var id = (long)byName[0]!;
            if (machineId is not null && !string.Equals(byName[1] as string, machineId, StringComparison.Ordinal))
                Execute(c, "UPDATE machines SET machine_id = $mid WHERE id = $id;", ("$mid", machineId), ("$id", id));
            return (id, null, Deserialize((string)byName[2]!));
        }
        return (null, null, null);
    }

    private const string MachineColumns = """
        m.name, m.last_seen, m.summary_json, (SELECT COUNT(*) FROM reports r WHERE r.machine_ref = m.id), m.id, m.machine_id, m.agent_seen
        """;

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
        cmd.CommandText = """
            SELECT r.id, r.received_at, r.summary_json FROM reports r JOIN machines m ON m.id = r.machine_ref
            WHERE m.name = $n ORDER BY r.report_at DESC, r.id DESC;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (reader.Read())
        {
            var r = Deserialize(reader.GetString(2));
            list.Add(new HistoryEntry(reader.GetInt64(0), r.Timestamp, ParseIso(reader.GetString(1)), r.AppVersion,
                r.DriverProblems, r.HardwareFaults, r.Unresolved, r.Proposed, r.Blocked, r.Installed, r.Failed, r.AnalysisOnly, r.Healthy,
                r.Health?.Score));
        }
        return list;
    }

    public MachineReport? GetHistoricReport(string name, long id)
    {
        using var c = Open();
        return Scalar(c, "SELECT r.summary_json FROM reports r JOIN machines m ON m.id = r.machine_ref WHERE m.name = $n AND r.id = $id;",
            ("$n", name), ("$id", id)) is string json ? Deserialize(json) : null;
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
        return Scalar(c, "SELECT raw_report FROM machines WHERE name = $n;", ("$n", name)) is byte[] raw ? Decompress(raw) : null;
    }

    public bool DeleteMachine(string name)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM machines WHERE name = $n;", ("$n", name)) > 0;
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
        reader.GetInt32(3),
        Deserialize(reader.GetString(2)),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : ParseIso(reader.GetString(6)));

    private static MachineReport Deserialize(string json) => JsonSerializer.Deserialize<MachineReport>(json, Json)!;

    internal static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

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

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip))
            writer.Write(text);
        return output.ToArray();
    }

    private static string Decompress(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }
}
