using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Tests;

public sealed class BackupRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "backups-" + Guid.NewGuid().ToString("N"));

    public BackupRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DataProtection.DeleteTree(_root);

    private string Backup(string name, int bytes)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllBytes(Path.Combine(dir, "pilote.sys"), new byte[bytes]);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    [Fact]
    public void Keeps_the_newest_backups_and_those_still_needed_for_a_rollback()
    {
        var b1 = Backup("20261101-100000", 10);
        var b2 = Backup("20261108-100000", 10);
        var b3 = Backup("20261115-100000", 10);
        var b4 = Backup("20261122-100000", 10);
        var b5 = Backup("20261128-100000", 10);
        var cleaner = Backup("driverstore-20261001-100000", 10); // nettoyage du magasin : hors rétention

        var removed = SystemProtection.PruneBackups(_root, new BackupRetention(2, 0), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b2 });

        Assert.Equal([b3, b1], removed);
        Assert.True(Directory.Exists(b5));
        Assert.True(Directory.Exists(b4));
        Assert.True(Directory.Exists(b2)); // intervention annulable la plus récente
        Assert.True(Directory.Exists(cleaner));
    }

    [Fact]
    public void Size_cap_removes_the_oldest_but_never_the_newest()
    {
        var b1 = Backup("20261101-100000", 4000);
        var b2 = Backup("20261108-100000", 4000);
        var b3 = Backup("20261115-100000", 4000);

        var removed = SystemProtection.PruneBackups(_root, new BackupRetention(3, 9000), new HashSet<string>());
        Assert.Equal([b1], removed);
        Assert.True(Directory.Exists(b2) && Directory.Exists(b3));

        removed = SystemProtection.PruneBackups(_root, new BackupRetention(3, 1000), new HashSet<string>());
        Assert.Equal([b2], removed);
        Assert.True(Directory.Exists(b3)); // seule restante : gardée même au-delà de la limite
    }

    [Fact]
    public void Unchanged_driver_store_reuses_the_last_complete_backup()
    {
        var inf = Directory.CreateDirectory(Path.Combine(_root, "INF")).FullName;
        File.WriteAllText(Path.Combine(inf, "oem1.inf"), "[Version]");
        File.WriteAllText(Path.Combine(inf, "machine.inf"), "[Version]"); // INF Windows : hors empreinte
        var before = SystemProtection.DriverStoreFingerprint(inf)!;

        var backups = Directory.CreateDirectory(Path.Combine(_root, "Backups")).FullName;
        var dir = Directory.CreateDirectory(Path.Combine(backups, "20261128-100000")).FullName;
        File.WriteAllText(Path.Combine(dir, SystemProtection.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(new SystemProtection.BackupManifest { Complete = true, StoreFingerprint = before, SizeBytes = 1 }));
        Assert.Equal(dir, SystemProtection.FindReusableBackup(backups, before));

        File.WriteAllText(Path.Combine(inf, "machine.inf"), "[Version] modifié");
        Assert.Equal(before, SystemProtection.DriverStoreFingerprint(inf));
        File.WriteAllText(Path.Combine(inf, "oem2.inf"), "[Version]"); // nouveau paquet tiers
        var after = SystemProtection.DriverStoreFingerprint(inf)!;
        Assert.NotEqual(before, after);
        Assert.Null(SystemProtection.FindReusableBackup(backups, after));

        // Sauvegarde incomplète : jamais réutilisée.
        File.WriteAllText(Path.Combine(dir, SystemProtection.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(new SystemProtection.BackupManifest { Complete = false, StoreFingerprint = before }));
        Assert.Null(SystemProtection.FindReusableBackup(backups, before));
    }

    [Fact]
    public void Retention_settings_have_safe_defaults()
    {
        var retention = BackupRetention.From(new Settings());
        Assert.Equal(3, retention.KeepCount);
        Assert.Equal(10240L * 1024 * 1024, retention.MaxTotalBytes);
        Assert.Equal(1, BackupRetention.From(new Settings { BackupRetentionCount = 0 }).KeepCount);
    }
}
