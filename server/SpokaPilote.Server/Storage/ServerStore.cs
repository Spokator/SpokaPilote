using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Storage;

/// <summary>Entrée de l'historique d'un poste (indicateurs du rapport, sans le détail).</summary>
public sealed record HistoryEntry(long Id, DateTimeOffset Timestamp, DateTimeOffset ReceivedAt, string? AppVersion,
    int DriverProblems, int HardwareFaults, int Unresolved, int Proposed, int Blocked, int Installed, int Failed, bool AnalysisOnly, bool Healthy);

/// <summary>Document JSON brut (politique de pilotes, profil d'imprimantes) et sa date de mise à jour.</summary>
public sealed record StoredDocument(string Json, DateTimeOffset UpdatedAt);

/// <summary>
/// Stockage SQLite (un seul fichier dans le volume de données) : postes, historique des rapports, documents.
/// Une connexion par opération (le pool de Microsoft.Data.Sqlite les réutilise) ; journal WAL pour les lectures concurrentes.
/// </summary>
public sealed class ServerStore
{
    public const string PolicyKey = "policy";
    public const string PrinterProfileKey = "printer-profile";
    private const int SchemaVersion = 1;

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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion) return;

        Execute(c, """
            CREATE TABLE IF NOT EXISTS machines (
                name          TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                last_seen     TEXT NOT NULL,
                last_report   TEXT NOT NULL,
                summary_json  TEXT NOT NULL,
                raw_report    BLOB
            );
            CREATE TABLE IF NOT EXISTS reports (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                machine       TEXT NOT NULL COLLATE NOCASE REFERENCES machines(name) ON DELETE CASCADE,
                report_at     TEXT NOT NULL,
                received_at   TEXT NOT NULL,
                summary_json  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reports_machine ON reports(machine, report_at DESC);
            CREATE TABLE IF NOT EXISTS documents (
                key           TEXT NOT NULL PRIMARY KEY,
                json          TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """);
    }

    // ------------------------------------------------------------------ rapports

    /// <summary>
    /// Enregistre un rapport. Le « dernier rapport » d'un poste est celui dont l'horodatage est le plus récent :
    /// renvoyer un ancien rapport (reprise d'un partage \\srv\rapports) l'ajoute à l'historique sans écraser l'état actuel.
    /// </summary>
    public void SaveReport(MachineReport report, string rawJson, DateTimeOffset receivedAt)
    {
        var summary = JsonSerializer.Serialize(report, Json);
        // Dernier contact : date du rapport, bornée par la date de réception (horloge du poste en avance).
        var seen = report.Timestamp < receivedAt ? report.Timestamp : receivedAt;

        using var c = Open();
        using var tx = c.BeginTransaction();
        var current = Scalar(c, "SELECT last_report FROM machines WHERE name = $n;", ("$n", report.Name)) as string;
        if (current is null)
        {
            Execute(c, """
                INSERT INTO machines(name, last_seen, last_report, summary_json, raw_report) VALUES($n, $seen, $at, $s, $raw);
                """, ("$n", report.Name), ("$seen", Iso(seen)), ("$at", Iso(report.Timestamp)), ("$s", summary), ("$raw", Compress(rawJson)));
        }
        else
        {
            if (string.CompareOrdinal(Iso(report.Timestamp), current) >= 0)
                Execute(c, "UPDATE machines SET last_report = $at, summary_json = $s, raw_report = $raw WHERE name = $n;",
                    ("$n", report.Name), ("$at", Iso(report.Timestamp)), ("$s", summary), ("$raw", Compress(rawJson)));
            Execute(c, "UPDATE machines SET last_seen = MAX(last_seen, $seen) WHERE name = $n;", ("$n", report.Name), ("$seen", Iso(seen)));
        }

        Execute(c, "INSERT INTO reports(machine, report_at, received_at, summary_json) VALUES($n, $at, $rcv, $s);",
            ("$n", report.Name), ("$at", Iso(report.Timestamp)), ("$rcv", Iso(receivedAt)), ("$s", summary));
        // Ne garde que les N rapports les plus récents du poste.
        Execute(c, """
            DELETE FROM reports WHERE machine = $n AND id NOT IN
              (SELECT id FROM reports WHERE machine = $n ORDER BY report_at DESC, id DESC LIMIT $keep);
            """, ("$n", report.Name), ("$keep", _historyPerMachine));
        tx.Commit();
    }

    public List<StoredMachine> GetMachines()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.name, m.last_seen, m.summary_json, (SELECT COUNT(*) FROM reports r WHERE r.machine = m.name)
            FROM machines m ORDER BY m.name;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredMachine>();
        while (reader.Read()) list.Add(ReadMachine(reader));
        return list;
    }

    public StoredMachine? GetMachine(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.name, m.last_seen, m.summary_json, (SELECT COUNT(*) FROM reports r WHERE r.machine = m.name)
            FROM machines m WHERE m.name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMachine(reader) : null;
    }

    public List<HistoryEntry> GetHistory(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, received_at, summary_json FROM reports WHERE machine = $n ORDER BY report_at DESC, id DESC;";
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (reader.Read())
        {
            var r = JsonSerializer.Deserialize<MachineReport>(reader.GetString(2), Json)!;
            list.Add(new HistoryEntry(reader.GetInt64(0), r.Timestamp, ParseIso(reader.GetString(1)), r.AppVersion,
                r.DriverProblems, r.HardwareFaults, r.Unresolved, r.Proposed, r.Blocked, r.Installed, r.Failed, r.AnalysisOnly, r.Healthy));
        }
        return list;
    }

    public MachineReport? GetHistoricReport(string name, long id)
    {
        using var c = Open();
        return Scalar(c, "SELECT summary_json FROM reports WHERE machine = $n AND id = $id;", ("$n", name), ("$id", id)) is string json
            ? JsonSerializer.Deserialize<MachineReport>(json, Json)
            : null;
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

    // ------------------------------------------------------------------ documents (politique, profil d'imprimantes)

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
        JsonSerializer.Deserialize<MachineReport>(reader.GetString(2), Json)!);

    private static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

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
