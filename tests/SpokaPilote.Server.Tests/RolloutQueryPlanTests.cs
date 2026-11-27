using Microsoft.Data.Sqlite;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Tests;

/// <summary>Index utilisés par la diffusion par anneaux (grands parcs).</summary>
public sealed class RolloutQueryPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    /// <summary>
    /// Requêtes exécutées à chaque demande d'un poste (anneau du poste) et à chaque rapport (versions, installations,
    /// disjoncteur, régressions) : aucune ne parcourt une grande table (postes, groupes, versions, installations).
    /// </summary>
    [Theory]
    [InlineData("SELECT id FROM machines WHERE machine_id = 'x';")]
    [InlineData("SELECT id FROM machines WHERE name = 'x';")]
    [InlineData("""
        SELECT r.id FROM rollout_ring_groups rg JOIN rollout_rings r ON r.id = rg.ring_id
        WHERE rg.group_id IN (SELECT group_id FROM group_members WHERE machine_ref = 1 UNION SELECT group_id FROM group_auto_members WHERE machine_ref = 1)
        ORDER BY r.position DESC, r.id DESC LIMIT 1;
        """)]
    [InlineData("SELECT id, last_seen FROM rollout_versions WHERE source = 's' AND title = 't' AND version = 'v';")]
    [InlineData("SELECT id FROM rollout_versions WHERE title = 't' AND version = 'v' LIMIT 2;")]
    [InlineData("SELECT last_attempt FROM rollout_outcomes WHERE version_id = 1 AND ring_id = 1 AND machine_ref = 1;")]
    [InlineData("SELECT ring_id, COUNT(*) FROM rollout_outcomes WHERE version_id = 1 AND last_attempt >= 'x' GROUP BY ring_id;")]
    [InlineData("SELECT o.version_id FROM rollout_outcomes o JOIN rollout_versions v ON v.id = o.version_id WHERE o.machine_ref = 1 AND o.last_success >= 'x';")]
    public void Per_request_queries_use_an_index(string sql)
    {
        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        using var c = new SqliteConnection($"Data Source={store.DatabasePath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var r = cmd.ExecuteReader();
        var plan = new List<string>();
        while (r.Read()) plan.Add(r.GetString(3));
        string[] large = ["machines", "group_members", "group_auto_members", "rollout_versions", "rollout_outcomes", "o", "v"];
        Assert.DoesNotContain(plan, p => large.Any(t => p == $"SCAN {t}" || p.StartsWith($"SCAN {t} ", StringComparison.Ordinal)));
    }
}
