using System.IO.Compression;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

public static class Archive
{
    /// <summary>Extrait un .cab avec expand.exe (outil Windows intégré) en conservant l'arborescence.</summary>
    public static async Task ExtractCabAsync(string cabPath, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        var result = await ProcessRunner.RunAsync(
            ProcessRunner.SystemTool("expand.exe"),
            $"-F:* \"{cabPath}\" \"{destination}\"",
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Extraction du cab impossible ({result.ExitCode}) : {result.Combined.Trim()}");
    }

    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        switch (ext)
        {
            case ".cab":
                await ExtractCabAsync(archivePath, destination, ct).ConfigureAwait(false);
                break;
            case ".zip":
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true), ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Format d'archive non pris en charge : {ext}");
        }
    }

    /// <summary>Extrait un .cab de métadonnées contenant un seul fichier et renvoie son chemin.</summary>
    public static async Task<string> ExtractSingleFileCabAsync(string cabPath, string cacheName, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.Cache, cacheName + ".x");
        var stamp = Path.Combine(dir, ".stamp");
        var cabTime = File.GetLastWriteTimeUtc(cabPath).Ticks.ToString();
        if (File.Exists(stamp) && File.ReadAllText(stamp) == cabTime)
        {
            var existing = Directory.GetFiles(dir).FirstOrDefault(f => !f.EndsWith(".stamp"));
            if (existing is not null) return existing;
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        await ExtractCabAsync(cabPath, dir, ct).ConfigureAwait(false);
        var file = Directory.GetFiles(dir).OrderByDescending(f => new FileInfo(f).Length).First();
        await File.WriteAllTextAsync(stamp, cabTime, ct).ConfigureAwait(false);
        return file;
    }
}
