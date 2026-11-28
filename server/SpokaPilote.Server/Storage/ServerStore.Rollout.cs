using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Rollout;

namespace SpokaPilote.Server.Storage;

/// <summary>Ce que l'enregistrement d'un rapport a changé pour la diffusion par anneaux.</summary>
/// <param name="NewVersions">Versions vues pour la première fois sur le parc.</param>
/// <param name="StatsChanged">Compteurs d'installation modifiés (validation des anneaux).</param>
/// <param name="FailedVersions">Versions avec un nouvel échec (ou une régression) : à évaluer par le disjoncteur.</param>
public sealed record RolloutTrack(int NewVersions, bool StatsChanged, IReadOnlyList<long> FailedVersions)
{
    public static readonly RolloutTrack None = new(0, false, []);
    public bool Changed => NewVersions > 0 || StatsChanged;
}

/// <summary>
/// Diffusion par anneaux (console 4.0, schéma 5) : anneaux, versions vues sur le parc (première détection), installations
/// par anneau et compteurs, suspensions, historique. Tout est tenu à jour à la réception de chaque rapport, en coût
/// proportionnel au rapport (quelques requêtes indexées par proposition et par résultat d'installation) : rien n'est
/// recalculé sur l'ensemble des rapports.
/// </summary>
public sealed partial class ServerStore
{
    public const string RolloutSettingsKey = "rollout-settings";

    /// <summary>Tables de la version 5 (ajouts uniquement).</summary>
    private const string SchemaV5 = """
        CREATE TABLE IF NOT EXISTS rollout_rings (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            name               TEXT NOT NULL UNIQUE COLLATE NOCASE,
            position           INTEGER NOT NULL,
            delay_days         INTEGER NOT NULL,
            require_validation INTEGER NOT NULL DEFAULT 0,
            min_successes      INTEGER NOT NULL DEFAULT 1,
            max_failures       INTEGER NOT NULL DEFAULT 0,
            description        TEXT,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS rollout_ring_groups (
            ring_id       INTEGER NOT NULL REFERENCES rollout_rings(id) ON DELETE CASCADE,
            group_id      INTEGER NOT NULL REFERENCES machine_groups(id) ON DELETE CASCADE,
            PRIMARY KEY (ring_id, group_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_rollout_ring_groups_group ON rollout_ring_groups(group_id);
        CREATE TABLE IF NOT EXISTS rollout_versions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            source          TEXT NOT NULL,
            title           TEXT NOT NULL,
            version         TEXT NOT NULL,
            family          TEXT NOT NULL,
            kind            TEXT,
            category        TEXT,
            first_seen      TEXT NOT NULL,
            first_machine   TEXT,
            last_seen       TEXT NOT NULL,
            suspended       INTEGER NOT NULL DEFAULT 0,
            suspend_auto    INTEGER NOT NULL DEFAULT 0,
            suspend_reason  TEXT,
            suspended_at    TEXT,
            suspended_by    TEXT,
            acknowledged_at TEXT,
            acknowledged_by TEXT,
            resumed_at      TEXT,
            resumed_by      TEXT,
            approved_at     TEXT,
            approved_by     TEXT,
            UNIQUE (source, title, version)
        );
        CREATE INDEX IF NOT EXISTS ix_rollout_versions_title ON rollout_versions(title, version);
        CREATE TABLE IF NOT EXISTS rollout_outcomes (
            version_id      INTEGER NOT NULL REFERENCES rollout_versions(id) ON DELETE CASCADE,
            ring_id         INTEGER NOT NULL,
            machine_ref     INTEGER NOT NULL REFERENCES machines(id) ON DELETE CASCADE,
            last_attempt    TEXT NOT NULL,
            last_success    TEXT,
            last_failure    TEXT,
            last_regression TEXT,
            message         TEXT,
            PRIMARY KEY (version_id, ring_id, machine_ref)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_rollout_outcomes_machine ON rollout_outcomes(machine_ref, last_success);
        CREATE TABLE IF NOT EXISTS rollout_stats (
            version_id    INTEGER NOT NULL REFERENCES rollout_versions(id) ON DELETE CASCADE,
            ring_id       INTEGER NOT NULL,
            successes     INTEGER NOT NULL DEFAULT 0,
            failures      INTEGER NOT NULL DEFAULT 0,
            regressions   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (version_id, ring_id)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS rollout_events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            at            TEXT NOT NULL,
            version_id    INTEGER,
            driver        TEXT,
            action        TEXT NOT NULL,
            actor         TEXT NOT NULL,
            detail        TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_rollout_events_at ON rollout_events(at);
        -- Poste supprimé (suppression en cascade de ses installations) : il ne compte plus dans les compteurs des anneaux.
        CREATE TRIGGER IF NOT EXISTS trg_rollout_outcomes_delete AFTER DELETE ON rollout_outcomes
            BEGIN
                UPDATE rollout_stats SET
                    successes = successes - (OLD.last_success IS NOT NULL AND (OLD.last_failure IS NULL OR OLD.last_success > OLD.last_failure)),
                    failures = failures - (OLD.last_failure IS NOT NULL AND (OLD.last_success IS NULL OR OLD.last_failure >= OLD.last_success)),
                    regressions = regressions - (OLD.last_regression IS NOT NULL)
                WHERE version_id = OLD.version_id AND ring_id = OLD.ring_id;
            END;
        """;

    /// <summary>
    /// Migration 6 (console 4.0.1) : la table des versions gagne l'identité des propositions sans numéro de version
    /// (<c>item_key</c>, vide pour une version ordinaire, dans la clé unique) et le matériel visé (<c>hardware_vendor</c>,
    /// <c>device_class</c> : même pilote d'une source à l'autre). SQLite ne sait pas modifier une contrainte UNIQUE : la table
    /// est reconstruite avec les mêmes identifiants (clés étrangères désactivées pendant la migration : installations,
    /// compteurs et historique gardent leurs références), la séquence AUTOINCREMENT est conservée.
    /// </summary>
    private static void RebuildRolloutVersionsV6(SqliteConnection c)
    {
        var sequence = Scalar(c, "SELECT seq FROM sqlite_sequence WHERE name = 'rollout_versions';");
        Execute(c, """
            CREATE TABLE rollout_versions_v6 (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                source          TEXT NOT NULL,
                title           TEXT NOT NULL,
                version         TEXT NOT NULL,
                item_key        TEXT NOT NULL DEFAULT '',
                family          TEXT NOT NULL,
                kind            TEXT,
                category        TEXT,
                hardware_vendor TEXT,
                device_class    TEXT,
                first_seen      TEXT NOT NULL,
                first_machine   TEXT,
                last_seen       TEXT NOT NULL,
                suspended       INTEGER NOT NULL DEFAULT 0,
                suspend_auto    INTEGER NOT NULL DEFAULT 0,
                suspend_reason  TEXT,
                suspended_at    TEXT,
                suspended_by    TEXT,
                acknowledged_at TEXT,
                acknowledged_by TEXT,
                resumed_at      TEXT,
                resumed_by      TEXT,
                approved_at     TEXT,
                approved_by     TEXT,
                UNIQUE (source, title, version, item_key)
            );
            INSERT INTO rollout_versions_v6(id, source, title, version, family, kind, category, first_seen, first_machine, last_seen, suspended,
                suspend_auto, suspend_reason, suspended_at, suspended_by, acknowledged_at, acknowledged_by, resumed_at, resumed_by, approved_at, approved_by)
            SELECT id, source, title, version, family, kind, category, first_seen, first_machine, last_seen, suspended,
                suspend_auto, suspend_reason, suspended_at, suspended_by, acknowledged_at, acknowledged_by, resumed_at, resumed_by, approved_at, approved_by
            FROM rollout_versions ORDER BY id;
            DROP TABLE rollout_versions;
            ALTER TABLE rollout_versions_v6 RENAME TO rollout_versions;
            CREATE INDEX IF NOT EXISTS ix_rollout_versions_title ON rollout_versions(title, version);
            CREATE INDEX IF NOT EXISTS ix_rollout_versions_item ON rollout_versions(item_key) WHERE item_key <> '';
            """);
        if (sequence is not null)
            Execute(c, """
                UPDATE sqlite_sequence SET seq = MAX(seq, $s) WHERE name = 'rollout_versions';
                INSERT INTO sqlite_sequence(name, seq) SELECT 'rollout_versions', $s WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'rollout_versions');
                """, ("$s", sequence));
    }

    // ------------------------------------------------------------------ configuration (anneaux et réglages, en mémoire)

    private sealed record RolloutConfig(RolloutSettings Settings, IReadOnlyList<RingRecord> Rings);

    private volatile RolloutConfig? _rolloutConfig;

    private RolloutConfig Config()
    {
        if (_rolloutConfig is { } cached) return cached;
        using var c = Open();
        var config = new RolloutConfig(ReadRolloutSettings(c), ReadRings(c));
        _rolloutConfig = config;
        return config;
    }

    private void InvalidateRolloutConfig() => _rolloutConfig = null;

    public RolloutSettings GetRolloutSettings() => Config().Settings.Clone();

    public IReadOnlyList<RingRecord> GetRings() => Config().Rings;

    private static RolloutSettings ReadRolloutSettings(SqliteConnection c)
    {
        if (Scalar(c, "SELECT json FROM documents WHERE key = $k;", ("$k", RolloutSettingsKey)) is not string json) return new RolloutSettings();
        try { return JsonSerializer.Deserialize<RolloutSettings>(json, Json) ?? new RolloutSettings(); }
        catch (JsonException) { return new RolloutSettings(); }
    }

    public void SaveRolloutSettings(RolloutSettings settings, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        Execute(c, """
            INSERT INTO documents(key, json, updated_at) VALUES($k, $j, $u)
            ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at;
            """, ("$k", RolloutSettingsKey), ("$j", JsonSerializer.Serialize(settings, Json)), ("$u", Iso(now)));
        tx.Commit();
        InvalidateRolloutConfig();
    }

    private static List<RingRecord> ReadRings(SqliteConnection c)
    {
        var groups = new Dictionary<long, List<long>>();
        using (var g = c.CreateCommand())
        {
            g.CommandText = "SELECT ring_id, group_id FROM rollout_ring_groups ORDER BY group_id;";
            using var r = g.ExecuteReader();
            while (r.Read())
            {
                if (!groups.TryGetValue(r.GetInt64(0), out var list)) groups[r.GetInt64(0)] = list = new List<long>();
                list.Add(r.GetInt64(1));
            }
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, position, delay_days, require_validation, min_successes, max_failures, description, created_at, updated_at
            FROM rollout_rings ORDER BY position, id;
            """;
        using var reader = cmd.ExecuteReader();
        var rings = new List<RingRecord>();
        while (reader.Read())
            rings.Add(new RingRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4) != 0,
                reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                groups.GetValueOrDefault(reader.GetInt64(0)) ?? new List<long>(), ParseIso(reader.GetString(8)), ParseIso(reader.GetString(9))));
        return rings;
    }

    /// <summary>Données d'un anneau à créer ou modifier.</summary>
    public sealed record RingInput(string Name, int DelayDays, bool RequireValidation, int MinSuccesses, int MaxFailures, string? Description,
        IReadOnlyList<long> GroupIds);

    /// <summary>Crée un anneau (en dernière position) ; null si le nom est déjà pris.</summary>
    public long? CreateRing(RingInput input, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        try
        {
            var position = Convert.ToInt32(Scalar(c, "SELECT COALESCE(MAX(position), 0) + 1 FROM rollout_rings;"), CultureInfo.InvariantCulture);
            var id = Convert.ToInt64(Scalar(c, """
                INSERT INTO rollout_rings(name, position, delay_days, require_validation, min_successes, max_failures, description, created_at, updated_at)
                VALUES($n, $p, $d, $rv, $ms, $mf, $desc, $now, $now) RETURNING id;
                """, ("$n", input.Name), ("$p", position), ("$d", input.DelayDays), ("$rv", input.RequireValidation ? 1 : 0),
                ("$ms", input.MinSuccesses), ("$mf", input.MaxFailures), ("$desc", input.Description), ("$now", Iso(now))), CultureInfo.InvariantCulture);
            WriteRingGroups(c, id, input.GroupIds);
            tx.Commit();
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null;
        }
        finally
        {
            InvalidateRolloutConfig();
        }
    }

    /// <summary>Modifie un anneau ; false s'il n'existe pas ; InvalidOperationException si le nom est pris.</summary>
    public bool UpdateRing(long id, RingInput input, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        try
        {
            var updated = Execute(c, """
                UPDATE rollout_rings SET name = $n, delay_days = $d, require_validation = $rv, min_successes = $ms, max_failures = $mf,
                    description = $desc, updated_at = $now WHERE id = $id;
                """, ("$id", id), ("$n", input.Name), ("$d", input.DelayDays), ("$rv", input.RequireValidation ? 1 : 0), ("$ms", input.MinSuccesses),
                ("$mf", input.MaxFailures), ("$desc", input.Description), ("$now", Iso(now))) > 0;
            if (updated) WriteRingGroups(c, id, input.GroupIds);
            tx.Commit();
            return updated;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Un anneau « {input.Name} » existe déjà.");
        }
        finally
        {
            InvalidateRolloutConfig();
        }
    }

    private static void WriteRingGroups(SqliteConnection c, long ringId, IEnumerable<long> groupIds)
    {
        Execute(c, "DELETE FROM rollout_ring_groups WHERE ring_id = $r;", ("$r", ringId));
        foreach (var g in groupIds.Distinct())
            Execute(c, "INSERT OR IGNORE INTO rollout_ring_groups(ring_id, group_id) SELECT $r, id FROM machine_groups WHERE id = $g;", ("$r", ringId), ("$g", g));
    }

    public bool DeleteRing(long id)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var deleted = Execute(c, "DELETE FROM rollout_rings WHERE id = $id;", ("$id", id)) > 0;
        tx.Commit();
        InvalidateRolloutConfig();
        return deleted;
    }

    /// <summary>Nouvel ordre des anneaux (identifiants du premier au dernier ; tous les anneaux doivent être cités).</summary>
    public bool ReorderRings(IReadOnlyList<long> ids, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var existing = ReadRings(c).Select(r => r.Id).Order().ToList();
        if (!existing.SequenceEqual(ids.Distinct().Order())) return false;
        for (var i = 0; i < ids.Count; i++)
            Execute(c, "UPDATE rollout_rings SET position = $p, updated_at = $now WHERE id = $id;", ("$p", i + 1), ("$id", ids[i]), ("$now", Iso(now)));
        tx.Commit();
        InvalidateRolloutConfig();
        return true;
    }

    // ------------------------------------------------------------------ anneau d'un poste

    /// <summary>
    /// Anneau d'un poste : parmi les anneaux liés à ses groupes (manuels ou par règle), le plus TARDIF (position la plus
    /// grande) — règle prudente : un poste ajouté par erreur à un groupe pilote ne reçoit rien plus tôt s'il est aussi
    /// dans un groupe d'un anneau plus tardif. Sans groupe d'anneau : l'anneau par défaut. null si aucun anneau.
    /// </summary>
    private static long? ResolveRing(SqliteConnection c, long machineRef, RolloutConfig config)
    {
        if (config.Rings.Count == 0) return null;
        var id = Scalar(c, """
            SELECT r.id FROM rollout_ring_groups rg JOIN rollout_rings r ON r.id = rg.ring_id
            WHERE rg.group_id IN (SELECT group_id FROM group_members WHERE machine_ref = $m UNION SELECT group_id FROM group_auto_members WHERE machine_ref = $m)
            ORDER BY r.position DESC, r.id DESC LIMIT 1;
            """, ("$m", machineRef)) as long?;
        return id ?? (config.Rings.FirstOrDefault(r => r.Id == config.Settings.DefaultRingId) ?? config.Rings[^1]).Id;
    }

    /// <summary>
    /// Poste qui demande sa politique (identifiant machine d'abord, sinon nom) et son anneau.
    /// Known = false : poste inconnu de la console (aucun rapport) → anneau par défaut.
    /// </summary>
    public (bool Known, long? RingId) ResolveMachineRing(string? machineId, string? name)
    {
        var config = Config();
        using var c = Open();
        long? machineRef = null;
        if (ReportParser.NormalizeMachineId(machineId) is { } mid) machineRef = Scalar(c, "SELECT id FROM machines WHERE machine_id = $mid;", ("$mid", mid)) as long?;
        if (machineRef is null && ReportParser.IsValidMachineName(name?.Trim()))
            machineRef = Scalar(c, "SELECT id FROM machines WHERE name = $n;", ("$n", name!.Trim())) as long?;
        if (machineRef is null)
            return (false, config.Rings.Count == 0 ? null : (config.Rings.FirstOrDefault(r => r.Id == config.Settings.DefaultRingId) ?? config.Rings[^1]).Id);
        return (true, ResolveRing(c, machineRef.Value, config));
    }

    public long? GetMachineRing(long machineRef)
    {
        var config = Config();
        using var c = Open();
        return ResolveRing(c, machineRef, config);
    }

    /// <summary>Postes par anneau (résolution ci-dessus) et postes rattachés à plusieurs anneaux (conflits résolus par la règle prudente).</summary>
    public (Dictionary<long, int> PerRing, int Conflicts, int Total) GetRingMachineCounts()
    {
        var config = Config();
        var perRing = config.Rings.ToDictionary(r => r.Id, _ => 0);
        using var c = Open();
        var total = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM machines;"), CultureInfo.InvariantCulture);
        if (config.Rings.Count == 0) return (perRing, 0, total);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            WITH mg AS (SELECT machine_ref, group_id FROM group_members UNION SELECT machine_ref, group_id FROM group_auto_members),
                 mr AS (SELECT mg.machine_ref, MAX(r.position) AS pos, COUNT(DISTINCT r.id) AS n
                        FROM mg JOIN rollout_ring_groups rg ON rg.group_id = mg.group_id JOIN rollout_rings r ON r.id = rg.ring_id
                        GROUP BY mg.machine_ref)
            SELECT pos, COUNT(*), SUM(n > 1) FROM mr GROUP BY pos;
            """;
        using var r = cmd.ExecuteReader();
        var assigned = 0;
        var conflicts = 0;
        while (r.Read())
        {
            var ring = config.Rings.Where(x => x.Position == r.GetInt32(0)).OrderByDescending(x => x.Id).FirstOrDefault();
            if (ring is null) continue;
            perRing[ring.Id] += r.GetInt32(1);
            assigned += r.GetInt32(1);
            conflicts += r.GetInt32(2);
        }
        var def = config.Rings.FirstOrDefault(x => x.Id == config.Settings.DefaultRingId) ?? config.Rings[^1];
        perRing[def.Id] += total - assigned;
        return (perRing, conflicts, total);
    }

    // ------------------------------------------------------------------ suivi à la réception d'un rapport

    /// <summary>Garde de taille : un rapport anormal ne fait pas exploser la table des versions.</summary>
    private const int MaxTrackedCandidates = 500;

    /// <summary>
    /// Première détection, installations et régressions d'un rapport (dans la transaction de son enregistrement).
    /// Première détection = date de RÉCEPTION par la console du premier rapport qui propose la version (même bloquée par
    /// la politique) : un ancien rapport renvoyé plus tard ne peut pas avancer la date (horloge du poste non utilisée).
    /// </summary>
    private RolloutTrack TrackRollout(SqliteConnection c, long machineRef, MachineReport report, DateTimeOffset receivedAt, bool isLatest)
    {
        var received = Iso(receivedAt);
        var day = received[..10];
        var newVersions = 0;
        var identityChanged = false;
        var byTitle = new Dictionary<(string Title, string Version), long>();
        foreach (var cand in report.Candidates.Take(MaxTrackedCandidates))
        {
            if (!RolloutKeys.IsTrackable(cand.Title, cand.Version, cand.ItemId)) continue;
            var title = cand.Title.Trim();
            // Sans version lisible (4.0.1) : version vide, identité stable de la proposition dans item_key.
            var item = RolloutKeys.ParseVersion(cand.Version) is null ? RolloutKeys.ItemKey(cand.ItemId)! : "";
            var version = item.Length > 0 ? "" : cand.Version!.Trim();
            var source = RolloutKeys.SourceKey(cand.Source);
            var vendor = cand.HardwareVendor;
            var row = Row(c, "SELECT id, last_seen, hardware_vendor FROM rollout_versions WHERE source = $s AND title = $t AND version = $v AND item_key = $i;",
                ("$s", source), ("$t", title), ("$v", version), ("$i", item));
            long id;
            if (row is null)
            {
                id = Convert.ToInt64(Scalar(c, """
                    INSERT INTO rollout_versions(source, title, version, item_key, family, kind, category, hardware_vendor, device_class, first_seen, first_machine, last_seen)
                    VALUES($s, $t, $v, $i, $f, $k, $cat, $hv, $dc, $at, $m, $at) RETURNING id;
                    """, ("$s", source), ("$t", title), ("$v", version), ("$i", item), ("$f", RolloutKeys.FamilyPattern(title)),
                    ("$k", RolloutKeys.CandidateKind(cand.Kind) ?? cand.Kind), ("$cat", cand.Category), ("$hv", vendor), ("$dc", vendor is null ? null : cand.DeviceClass),
                    ("$at", received), ("$m", report.Name)),
                    CultureInfo.InvariantCulture);
                newVersions++;
            }
            else
            {
                id = (long)row[0]!;
                // Dernière détection : au plus une écriture par version et par jour. Matériel visé appris d'un rapport ultérieur
                // (version vue d'abord sur un poste où le périphérique n'était pas identifié) : une écriture, une fois.
                if (row[2] is null && vendor is not null)
                {
                    Execute(c, "UPDATE rollout_versions SET hardware_vendor = $hv, device_class = $dc, last_seen = MAX(last_seen, $at) WHERE id = $id;",
                        ("$id", id), ("$hv", vendor), ("$dc", cand.DeviceClass), ("$at", received));
                    identityChanged = true; // l'identité d'une suspension peut s'élargir : état à recalculer
                }
                else if (string.CompareOrdinal((string)row[1]!, day) < 0)
                    Execute(c, "UPDATE rollout_versions SET last_seen = $at WHERE id = $id;", ("$id", id), ("$at", received));
            }
            byTitle.TryAdd((title.ToUpperInvariant(), item.Length > 0 ? "#" + item : version.ToUpperInvariant()), id);
        }

        var config = Config();
        var statsChanged = identityChanged;
        var failed = new List<long>();
        long? ring = null;
        var ringResolved = false;
        long RingId()
        {
            if (!ringResolved) { ring = ResolveRing(c, machineRef, config); ringResolved = true; }
            return ring ?? 0;
        }

        // Résultats d'installation : un poste compte une fois par version et par anneau (son état le plus récent).
        var at = Iso(report.Timestamp < receivedAt ? report.Timestamp : receivedAt);
        if (report.Installation is { } outcomes)
            foreach (var o in outcomes.Take(MaxTrackedCandidates))
            {
                var success = o.Status == "Installed";
                if (!success && o.Status != "Failed") continue; // déjà à jour, simulation, bloqué : pas une tentative
                var versionId = FindVersion(c, byTitle, o.Title, o.Version, o.ItemId);
                if (versionId is null) continue;
                if (RecordOutcome(c, versionId.Value, RingId(), machineRef, at, success, regression: false,
                        success ? null : Truncate(o.Message, 500)))
                    statsChanged = true;
                if (!success) failed.Add(versionId.Value);
            }

        // Régressions : un périphérique en erreur dont le pilote installé est une version installée avec succès sur ce poste
        // dans les 7 derniers jours (rapport suivant l'installation).
        if (isLatest && config.Settings.Breaker.CountRegressions &&
            report.ProblemDevices.Any(d => !d.IsHardwareFault && !string.IsNullOrWhiteSpace(d.DriverVersion)))
        {
            var recent = new List<(long Version, long Ring, string Text, string Success)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT o.version_id, o.ring_id, v.version, o.last_success FROM rollout_outcomes o JOIN rollout_versions v ON v.id = o.version_id
                    WHERE o.machine_ref = $m AND o.last_success >= $since AND o.last_success < $at
                      AND (o.last_regression IS NULL OR o.last_regression < o.last_success);
                    """;
                cmd.Parameters.AddWithValue("$m", machineRef);
                cmd.Parameters.AddWithValue("$since", Iso(receivedAt.AddDays(-7)));
                cmd.Parameters.AddWithValue("$at", at);
                using var r = cmd.ExecuteReader();
                while (r.Read()) recent.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
            }
            foreach (var (versionId, ringId, text, _) in recent)
            {
                var device = report.ProblemDevices.FirstOrDefault(d => !d.IsHardwareFault && RolloutKeys.SameVersion(d.DriverVersion, text));
                if (device is null) continue;
                RecordOutcome(c, versionId, ringId, machineRef, at, success: false, regression: true,
                    Truncate($"Régression : « {device.Name} » en erreur après l'installation ({device.StatusText ?? $"code {device.ErrorCode}"})", 500));
                statsChanged = true;
                failed.Add(versionId);
            }
        }
        return newVersions == 0 && !statsChanged && failed.Count == 0 ? RolloutTrack.None : new RolloutTrack(newVersions, statsChanged, failed.Distinct().ToList());
    }


    private static long? FindVersion(SqliteConnection c, Dictionary<(string, string), long> byTitle, string? title, string? version, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        // Pilote sans version lisible (4.0.1) : retrouvé par son identité stable.
        var item = RolloutKeys.ParseVersion(version) is null ? RolloutKeys.ItemKey(itemId) : null;
        if (item is null && string.IsNullOrWhiteSpace(version)) return null;
        if (byTitle.TryGetValue((title.Trim().ToUpperInvariant(), item is null ? version!.Trim().ToUpperInvariant() : "#" + item), out var id)) return id;
        // Résultat sans proposition correspondante dans le rapport : version connue sous ce titre (une seule source).
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM rollout_versions WHERE title = $t AND version = $v AND item_key = $i LIMIT 2;";
        cmd.Parameters.AddWithValue("$t", title.Trim());
        cmd.Parameters.AddWithValue("$v", item is null ? version!.Trim() : "");
        cmd.Parameters.AddWithValue("$i", item ?? "");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var first = r.GetInt64(0);
        return r.Read() ? null : first;
    }

    private enum OutcomeState { None, Ok, Failed }

    private static OutcomeState StateOf(string? success, string? failure) =>
        failure is not null && (success is null || string.CompareOrdinal(failure, success) >= 0) ? OutcomeState.Failed
        : success is not null ? OutcomeState.Ok : OutcomeState.None;

    /// <summary>Met à jour l'état d'un poste pour une version et un anneau, et les compteurs de l'anneau. true si les compteurs changent.</summary>
    private static bool RecordOutcome(SqliteConnection c, long versionId, long ringId, long machineRef, string at, bool success, bool regression, string? message)
    {
        var row = Row(c, "SELECT last_attempt, last_success, last_failure, last_regression FROM rollout_outcomes WHERE version_id = $v AND ring_id = $r AND machine_ref = $m;",
            ("$v", versionId), ("$r", ringId), ("$m", machineRef));
        static string? Max(string? a, string b) => a is null || string.CompareOrdinal(b, a) > 0 ? b : a;
        var (oldSuccess, oldFailure, oldRegression) = row is null ? (null, null, null) : (row[1] as string, row[2] as string, row[3] as string);
        var newSuccess = success ? Max(oldSuccess, at) : oldSuccess;
        var newFailure = success ? oldFailure : Max(oldFailure, at);
        var newRegression = regression ? Max(oldRegression, at) : oldRegression;
        var attempt = Max(row?[0] as string, at)!;
        if (row is not null && newSuccess == oldSuccess && newFailure == oldFailure && newRegression == oldRegression && attempt == (string)row[0]!)
            return false; // même rapport reçu deux fois
        Execute(c, """
            INSERT INTO rollout_outcomes(version_id, ring_id, machine_ref, last_attempt, last_success, last_failure, last_regression, message)
            VALUES($v, $r, $m, $a, $s, $f, $g, $msg)
            ON CONFLICT(version_id, ring_id, machine_ref) DO UPDATE SET last_attempt = excluded.last_attempt, last_success = excluded.last_success,
                last_failure = excluded.last_failure, last_regression = excluded.last_regression, message = COALESCE(excluded.message, message);
            """, ("$v", versionId), ("$r", ringId), ("$m", machineRef), ("$a", attempt), ("$s", newSuccess), ("$f", newFailure),
            ("$g", newRegression), ("$msg", success ? null : message));

        var before = StateOf(oldSuccess, oldFailure);
        var after = StateOf(newSuccess, newFailure);
        var ds = (after == OutcomeState.Ok ? 1 : 0) - (before == OutcomeState.Ok ? 1 : 0);
        var df = (after == OutcomeState.Failed ? 1 : 0) - (before == OutcomeState.Failed ? 1 : 0);
        var dr = (newRegression is not null ? 1 : 0) - (oldRegression is not null ? 1 : 0);
        if (ds == 0 && df == 0 && dr == 0) return false;
        Execute(c, """
            INSERT INTO rollout_stats(version_id, ring_id, successes, failures, regressions) VALUES($v, $r, $s, $f, $g)
            ON CONFLICT(version_id, ring_id) DO UPDATE SET successes = successes + excluded.successes, failures = failures + excluded.failures,
                regressions = regressions + excluded.regressions;
            """, ("$v", versionId), ("$r", ringId), ("$s", ds), ("$f", df), ("$g", dr));
        return true;
    }

    // ------------------------------------------------------------------ lecture

    private const string VersionColumns = """
        id, source, title, version, family, kind, category, first_seen, first_machine, last_seen, suspended, suspend_auto, suspend_reason,
        suspended_at, suspended_by, acknowledged_at, acknowledged_by, resumed_at, resumed_by, approved_at, approved_by,
        item_key, hardware_vendor, device_class
        """;

    private static RolloutVersion ReadVersion(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateTimeOffset? D(int i) => r.IsDBNull(i) ? null : ParseIso(r.GetString(i));
        return new RolloutVersion
        {
            Id = r.GetInt64(0), Source = r.GetString(1), Title = r.GetString(2), Version = r.GetString(3), Family = r.GetString(4),
            Kind = S(5), Category = S(6), FirstSeen = ParseIso(r.GetString(7)), FirstMachine = S(8), LastSeen = ParseIso(r.GetString(9)),
            Suspended = r.GetInt64(10) != 0, SuspendAuto = r.GetInt64(11) != 0, SuspendReason = S(12), SuspendedAt = D(13), SuspendedBy = S(14),
            AcknowledgedAt = D(15), AcknowledgedBy = S(16), ResumedAt = D(17), ResumedBy = S(18), ApprovedAt = D(19), ApprovedBy = S(20),
            ItemKey = S(21) ?? "", HardwareVendor = S(22), DeviceClass = S(23),
        };
    }

    /// <summary>Toutes les données de la diffusion (versions, compteurs, anneaux, réglages), lues en une fois.</summary>
    public RolloutSnapshot LoadRolloutSnapshot(DateTimeOffset now)
    {
        var config = Config();
        using var c = Open();
        var versions = new List<RolloutVersion>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {VersionColumns} FROM rollout_versions ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) versions.Add(ReadVersion(r));
        }
        var stats = new Dictionary<(long, long), RingStat>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT version_id, ring_id, successes, failures, regressions FROM rollout_stats;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) stats[(r.GetInt64(0), r.GetInt64(1))] = new RingStat(r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }
        return new RolloutSnapshot { Settings = config.Settings.Clone(), Rings = config.Rings, Versions = versions, Stats = stats, BuiltAt = now };
    }

    public RolloutVersion? GetRolloutVersion(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {VersionColumns} FROM rollout_versions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadVersion(r) : null;
    }

    /// <summary>Fenêtre du disjoncteur : par anneau, postes ayant tenté l'installation et postes en échec depuis <paramref name="cutoff"/>.</summary>
    public List<BreakerWindow> GetBreakerWindows(long versionId, DateTimeOffset cutoff)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT ring_id, COUNT(*), SUM(last_failure IS NOT NULL AND last_failure >= $cut) FROM rollout_outcomes
            WHERE version_id = $v AND last_attempt >= $cut GROUP BY ring_id ORDER BY ring_id;
            """;
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$cut", Iso(cutoff));
        using var r = cmd.ExecuteReader();
        var list = new List<BreakerWindow>();
        while (r.Read()) list.Add(new BreakerWindow(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Derniers échecs (et régressions) d'une version, du plus récent au plus ancien.</summary>
    public List<RolloutFailure> GetRolloutFailures(long versionId, int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.name, o.ring_id, o.last_failure, o.last_regression = o.last_failure, o.message FROM rollout_outcomes o
            JOIN machines m ON m.id = o.machine_ref
            WHERE o.version_id = $v AND o.last_failure IS NOT NULL ORDER BY o.last_failure DESC LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RolloutFailure>();
        while (r.Read())
            list.Add(new RolloutFailure(r.GetString(0), r.GetInt64(1), ParseIso(r.GetString(2)), !r.IsDBNull(3) && r.GetInt64(3) != 0,
                r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    // ------------------------------------------------------------------ suspension, reprise, approbation

    /// <summary>Suspend une version (si elle ne l'est pas déjà). true si l'état a changé.</summary>
    public bool SuspendVersion(long id, bool automatic, string reason, string actor, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var changed = Execute(c, """
            UPDATE rollout_versions SET suspended = 1, suspend_auto = $auto, suspend_reason = $reason, suspended_at = $now, suspended_by = $actor,
                acknowledged_at = NULL, acknowledged_by = NULL WHERE id = $id AND suspended = 0;
            """, ("$id", id), ("$auto", automatic ? 1 : 0), ("$reason", reason), ("$now", Iso(now)), ("$actor", actor)) > 0;
        if (changed) AddRolloutEvent(c, now, id, automatic ? "auto-suspend" : "suspend", actor, reason);
        tx.Commit();
        return changed;
    }

    /// <summary>Reprend la diffusion d'une version suspendue. Les échecs antérieurs à la reprise ne comptent plus pour le disjoncteur.</summary>
    public bool ResumeVersion(long id, string actor, string? comment, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var changed = Execute(c, """
            UPDATE rollout_versions SET suspended = 0, resumed_at = $now, resumed_by = $actor WHERE id = $id AND suspended = 1;
            """, ("$id", id), ("$now", Iso(now)), ("$actor", actor)) > 0;
        if (changed) AddRolloutEvent(c, now, id, "resume", actor, comment);
        tx.Commit();
        return changed;
    }

    /// <summary>« Maintenir le blocage » : la suspension automatique est examinée et reste en place.</summary>
    public bool AcknowledgeSuspension(long id, string actor, string? comment, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var changed = Execute(c, """
            UPDATE rollout_versions SET acknowledged_at = $now, acknowledged_by = $actor WHERE id = $id AND suspended = 1 AND acknowledged_at IS NULL;
            """, ("$id", id), ("$now", Iso(now)), ("$actor", actor)) > 0;
        if (changed) AddRolloutEvent(c, now, id, "acknowledge", actor, comment);
        tx.Commit();
        return changed;
    }

    /// <summary>« Diffuser partout maintenant » (approve = true) ou retour à la diffusion progressive (false).</summary>
    public bool ApproveVersion(long id, bool approve, string actor, string? comment, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var changed = approve
            ? Execute(c, "UPDATE rollout_versions SET approved_at = $now, approved_by = $actor WHERE id = $id AND approved_at IS NULL;",
                ("$id", id), ("$now", Iso(now)), ("$actor", actor)) > 0
            : Execute(c, "UPDATE rollout_versions SET approved_at = NULL, approved_by = NULL WHERE id = $id AND approved_at IS NOT NULL;", ("$id", id)) > 0;
        if (changed) AddRolloutEvent(c, now, id, approve ? "approve" : "unapprove", actor, comment);
        tx.Commit();
        return changed;
    }

    /// <summary>Événement d'historique hors version (anneaux, réglages).</summary>
    public void AddRolloutEvent(DateTimeOffset now, string action, string actor, string? detail)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        AddRolloutEvent(c, now, null, action, actor, detail);
        tx.Commit();
    }

    private static void AddRolloutEvent(SqliteConnection c, DateTimeOffset now, long? versionId, string action, string actor, string? detail)
    {
        Execute(c, """
            INSERT INTO rollout_events(at, version_id, driver, action, actor, detail)
            VALUES($at, $v, (SELECT CASE WHEN instr(title, version) > 0 THEN title ELSE title || ' ' || version END FROM rollout_versions WHERE id = $v),
                   $a, $actor, $d);
            DELETE FROM rollout_events WHERE id <= (SELECT MAX(id) FROM rollout_events) - 5000;
            """, ("$at", Iso(now)), ("$v", versionId), ("$a", action), ("$actor", actor), ("$d", detail));
    }

    public (int Total, List<RolloutEvent> Items) GetRolloutEvents(long? versionId, int offset, int limit)
    {
        using var c = Open();
        var filter = versionId is null ? "" : "WHERE version_id = $v";
        var total = Convert.ToInt32(Scalar(c, $"SELECT COUNT(*) FROM rollout_events {filter};", ("$v", versionId)), CultureInfo.InvariantCulture);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, at, version_id, driver, action, actor, detail FROM rollout_events {filter} ORDER BY id DESC LIMIT $l OFFSET $o;";
        cmd.Parameters.AddWithValue("$v", (object?)versionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        using var r = cmd.ExecuteReader();
        var list = new List<RolloutEvent>();
        while (r.Read())
            list.Add(new RolloutEvent(r.GetInt64(0), ParseIso(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return (total, list);
    }
}
