using System.Text;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Repository;

public sealed record PackedDriver(string Title, string? Version, string Folder, List<string> Devices, List<string> Infs);

public sealed record PackResult(string Folder, List<PackedDriver> Drivers, List<string> Missing)
{
    public string Summary => $"{Drivers.Count} pilote(s) préparé(s) dans {Folder}" +
                             (Missing.Count > 0 ? $" — {Missing.Count} périphérique(s) sans pilote trouvé" : "");
}

/// <summary>
/// Pack hors ligne : sur un poste connecté, prépare les pilotes dont un AUTRE poste a besoin, à partir de son
/// rapport JSON (typiquement un PC neuf sans pilote réseau, donc sans Internet).
/// 1. Sur le poste sans réseau : spokapilote scan --json E:\poste.json (depuis la clé USB)
/// 2. Sur un poste connecté    : spokapilote pack E:\poste.json E:\pack
/// 3. De retour sur le poste   : spokapilote install --repo E:\pack --yes
/// Chaque paquet est vérifié pour la machine cible (architecture, version de Windows, ID matériel, signature).
/// </summary>
public static class OfflinePackBuilder
{
    public static async Task<PackResult> BuildAsync(
        SystemInfo target,
        IReadOnlyList<DeviceInfo> devices,
        string outputFolder,
        bool allHardware,
        Settings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);
        var downloader = new Downloader(new TrustPolicy(settings));
        var context = new ScanContext
        {
            System = target,
            Devices = devices,
            Settings = settings,
            Options = new ScanOptions { DeepScan = allHardware },
            Progress = progress is null ? null : new Progress<ScanProgress>(p => progress.Report(p.Message)),
        };

        progress?.Report($"Recherche des pilotes pour {target.ComputerName} ({target.Manufacturer} {target.DisplayModel})...");
        var candidates = await new MicrosoftCatalogSource().FindAsync(context, ct).ConfigureAwait(false);

        var packed = new List<PackedDriver>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var candidate in candidates)
        {
            index++;
            var targets = devices.Where(d => candidate.TargetDeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var attempt in new[] { candidate }.Concat(candidate.Alternatives))
            {
                ct.ThrowIfCancellationRequested();
                // Le même paquet sert souvent plusieurs composants (ex. ports PCI Express du chipset) : une seule copie.
                var existing = packed.FirstOrDefault(p => p.Folder.EndsWith("_" + attempt.SourceItemId![..8], StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var recheck = PackageVerifier.SelectVerifiedInfs(Path.Combine(outputFolder, existing.Folder), null, targets, target.Architecture, target.OsVersion);
                    if (recheck.Infs.Count > 0)
                    {
                        existing.Devices.AddRange(targets.Select(t => t.Name).Where(n => !existing.Devices.Contains(n)));
                        foreach (var t in targets) covered.Add(t.InstanceId);
                        break;
                    }
                }
                try
                {
                    progress?.Report($"[{index}/{candidates.Count}] {attempt.Title} : téléchargement...");
                    var urls = await MicrosoftCatalogClient.GetDownloadUrlsAsync(attempt.SourceItemId!, ct).ConfigureAwait(false);
                    var url = urls.FirstOrDefault(u => u.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
                    if (url is null) continue;
                    var file = await downloader.DownloadAsync(url, sha1: MicrosoftCatalogClient.Sha1FromUrl(url), ct: ct).ConfigureAwait(false);
                    var temp = Path.Combine(AppPaths.Extracted, "pack-" + AppPaths.SafeName(attempt.SourceItemId!));
                    if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
                    await Archive.ExtractAsync(file, temp, ct).ConfigureAwait(false);

                    var verified = PackageVerifier.SelectVerifiedInfs(temp, null, targets, target.Architecture, target.OsVersion);
                    if (verified.Infs.Count == 0)
                    {
                        Log.Info($"Pack : {attempt.Title} écarté ({verified.Reason}).");
                        continue;
                    }

                    var name = AppPaths.SafeName(targets.FirstOrDefault()?.Name ?? attempt.Title, 50) + "_" + attempt.SourceItemId![..8];
                    var dest = Path.Combine(outputFolder, name);
                    CopyDirectory(temp, dest);
                    packed.Add(new PackedDriver(attempt.Title, attempt.Version, name, targets.Select(t => t.Name).Distinct().ToList(),
                        verified.Infs.Select(i => Path.GetRelativePath(temp, i)).ToList()));
                    foreach (var t in targets) covered.Add(t.InstanceId);
                    break;
                }
                catch (CatalogRateLimitedException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Pack : {attempt.Title} : {ex.Message}");
                }
            }
        }

        var missing = devices.Where(d => d.IsPresent && d.HasProblem && !covered.Contains(d.InstanceId))
            .Select(d => $"{d.Name} ({d.HardwareIds.FirstOrDefault() ?? d.InstanceId})").ToList();
        var result = new PackResult(outputFolder, packed, missing);
        await WriteManifestAsync(result, target, ct).ConfigureAwait(false);
        Log.Info(result.Summary);
        return result;
    }

    private static async Task WriteManifestAsync(PackResult result, SystemInfo target, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(result.Folder, "spokapilote-pack.json"), JsonSerializer.Serialize(new
        {
            createdBy = "SpokaPilote",
            created = DateTime.Now,
            target = new { target.ComputerName, target.Manufacturer, model = target.DisplayModel, os = $"{target.OsCaption} {target.OsDisplayVersion}", target.Architecture },
            drivers = result.Drivers,
            missing = result.Missing,
        }, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("PACK DE PILOTES HORS LIGNE — SpokaPilote");
        sb.AppendLine($"Préparé le {DateTime.Now:dd/MM/yyyy HH:mm} pour {target.ComputerName} — {target.Manufacturer} {target.DisplayModel}");
        sb.AppendLine($"Windows cible : {target.OsCaption} {target.OsDisplayVersion} ({target.Architecture})");
        sb.AppendLine();
        sb.AppendLine("INSTALLATION SUR LE POSTE CIBLE (console administrateur) :");
        sb.AppendLine("    spokapilote install --repo \"<ce dossier>\" --yes");
        sb.AppendLine("ou, dans l'interface : Dépôt de pilotes > Ajouter un dépôt > ce dossier, puis Analyser et Installer.");
        sb.AppendLine();
        sb.AppendLine("Tous les paquets proviennent du Microsoft Update Catalog et ont été vérifiés (signature, ID matériel, architecture).");
        sb.AppendLine();
        sb.AppendLine($"PILOTES INCLUS ({result.Drivers.Count}) :");
        foreach (var d in result.Drivers) sb.AppendLine($"  - {d.Title} {d.Version} -> {string.Join(", ", d.Devices)}");
        if (result.Missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"SANS PILOTE TROUVÉ ({result.Missing.Count}) :");
            foreach (var m in result.Missing) sb.AppendLine($"  - {m}");
        }
        await File.WriteAllTextAsync(Path.Combine(result.Folder, "LISEZ-MOI.txt"), sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
