using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Groups;

namespace SpokaPilote.Server.Storage;

public sealed partial class ServerStore
{
    public List<GroupRecord> GetGroups()
    {
        using var c = Open();
        var members = new Dictionary<long, HashSet<long>>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT group_id, machine_ref FROM group_members;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!members.TryGetValue(r.GetInt64(0), out var set)) members[r.GetInt64(0)] = set = new HashSet<long>();
                set.Add(r.GetInt64(1));
            }
        }
        using var list = c.CreateCommand();
        list.CommandText = "SELECT id, name, description, rules_json, created_at, updated_at FROM machine_groups ORDER BY name COLLATE NOCASE;";
        using var reader = list.ExecuteReader();
        var groups = new List<GroupRecord>();
        while (reader.Read())
            groups.Add(ReadGroup(reader, members.GetValueOrDefault(reader.GetInt64(0)) ?? new HashSet<long>()));
        return groups;
    }

    public GroupRecord? GetGroup(long id) => GetGroups().FirstOrDefault(g => g.Id == id);

    /// <summary>Crée un groupe ; null si le nom est déjà pris.</summary>
    public long? CreateGroup(string name, string? description, GroupRules rules, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        try
        {
            var id = Convert.ToInt64(Scalar(c, """
                INSERT INTO machine_groups(name, description, rules_json, created_at, updated_at) VALUES($n, $d, $r, $now, $now) RETURNING id;
                """, ("$n", name), ("$d", description), ("$r", JsonSerializer.Serialize(rules, Json)), ("$now", Iso(now))), CultureInfo.InvariantCulture);
            // Membres par règle calculés une fois pour tout le parc, puis tenus à jour à chaque rapport.
            RebuildAutoMembers(c, id, rules);
            tx.Commit();
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // contrainte UNIQUE
        {
            return null;
        }
    }

    /// <summary>Modifie un groupe. Renvoie false s'il n'existe pas ; lève InvalidOperationException si le nom est pris.</summary>
    public bool UpdateGroup(long id, string name, string? description, GroupRules rules, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        try
        {
            var updated = Execute(c, "UPDATE machine_groups SET name = $n, description = $d, rules_json = $r, updated_at = $now WHERE id = $id;",
                ("$id", id), ("$n", name), ("$d", description), ("$r", JsonSerializer.Serialize(rules, Json)), ("$now", Iso(now))) > 0;
            if (updated) RebuildAutoMembers(c, id, rules);
            tx.Commit();
            return updated;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Un groupe « {name} » existe déjà.");
        }
    }

    public bool DeleteGroup(long id)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM machine_groups WHERE id = $id;", ("$id", id)) > 0;
    }

    /// <summary>Ajoute et retire des membres manuels (clés internes des postes). Renvoie le nombre de changements.</summary>
    public int ChangeGroupMembers(long groupId, IEnumerable<long> add, IEnumerable<long> remove, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var changes = 0;
        foreach (var m in add.Distinct())
            changes += Execute(c, "INSERT OR IGNORE INTO group_members(group_id, machine_ref) SELECT $g, id FROM machines WHERE id = $m;", ("$g", groupId), ("$m", m));
        foreach (var m in remove.Distinct())
            changes += Execute(c, "DELETE FROM group_members WHERE group_id = $g AND machine_ref = $m;", ("$g", groupId), ("$m", m));
        if (changes > 0) Execute(c, "UPDATE machine_groups SET updated_at = $now WHERE id = $g;", ("$g", groupId), ("$now", Iso(now)));
        tx.Commit();
        return changes;
    }

    private static GroupRecord ReadGroup(SqliteDataReader r, HashSet<long> members)
    {
        GroupRules rules;
        try { rules = JsonSerializer.Deserialize<GroupRules>(r.GetString(3), Json) ?? new GroupRules(); }
        catch (JsonException) { rules = new GroupRules(); }
        return new GroupRecord(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), rules, members,
            ParseIso(r.GetString(4)), ParseIso(r.GetString(5)));
    }
}
