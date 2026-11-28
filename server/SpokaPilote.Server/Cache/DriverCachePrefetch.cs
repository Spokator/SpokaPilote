using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Cache;

/// <summary>État du dernier préchargement (page Paramètres de la console).</summary>
public sealed record PrefetchStatus(bool Running, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string By, string Scope,
    int Models, int Catalogs, int Files, long Bytes, int Failed, string Message);

/// <summary>
/// Préchargement du cache (sites isolés, parc qui ne doit pas sortir sur Internet aux heures de bureau) : pour les modèles
/// demandés (ou tous ceux du parc), catalogues constructeur que leurs postes consultent (Dell CatalogPC et
/// DriverPackCatalog, référence HP de la plateforme et de la version de Windows, catalogue Lenovo du type de machine) et
/// paquets proposés dans les derniers rapports de leurs postes. Les adresses passent par les mêmes contrôles que les
/// demandes des postes (domaines officiels) ; les postes revérifieront chaque fichier comme d'habitude.
/// </summary>
public sealed class DriverCachePrefetch(ServerStore store, DriverCacheStore cache, TimeProvider clock, ILogger<DriverCachePrefetch> log)
{
    /// <summary>Postes lus par modèle (les plus récents) pour connaître les paquets proposés.</summary>
    internal const int MachinesPerModel = 25;
    private readonly Lock _lock = new();
    private PrefetchStatus? _status;

    public PrefetchStatus? Status
    {
        get { lock (_lock) return _status; }
    }

    public bool TryStart(IReadOnlyList<string> models, bool all, bool packages, string by)
    {
        lock (_lock)
        {
            if (_status is { Running: true }) return false;
            _status = new PrefetchStatus(true, clock.GetUtcNow(), null, by, all ? "tous les modèles" : string.Join(", ", models.Take(5)), 0, 0, 0, 0, 0,
                "Préchargement en cours…");
        }
        _ = Task.Run(() => RunAsync(models, all, packages));
        return true;
    }

    /// <summary>Exécution (tests : appel direct, attendu).</summary>
    internal async Task<PrefetchStatus> RunAsync(IReadOnlyList<string> models, bool all, bool packages)
    {
        int catalogs = 0, files = 0, failed = 0;
        long bytes = 0;
        var modelCount = 0;
        try
        {
            var wanted = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
            var machines = store.GetMachineSummaries()
                .Where(m => all || wanted.Contains(m.System.Model ?? "") || wanted.Contains(m.DisplayModel))
                .GroupBy(m => m.System.Model ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            modelCount = machines.Count;

            var catalogUrls = new HashSet<string>(StringComparer.Ordinal);
            var packageUrls = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var group in machines)
            foreach (var machine in group.OrderByDescending(m => m.LastReport).Take(MachinesPerModel))
            {
                if (store.GetRawReport(machine.Name) is not { } raw) continue;
                try
                {
                    Collect(raw, catalogUrls, packages ? packageUrls : null);
                }
                catch (JsonException)
                {
                    // Rapport illisible : ignoré.
                }
            }

            var settings = cache.Settings;
            foreach (var url in catalogUrls)
            {
                var uri = new Uri(url);
                var result = await cache.GetCatalogAsync(new DriverCacheStore.CatalogRequest("catalog:" + DriverCacheStore.Normalize(uri), uri,
                    u => new HttpRequestMessage(HttpMethod.Get, u)), CancellationToken.None).ConfigureAwait(false);
                if (result.Status == 200) catalogs++;
                else if (result.Status != 404) failed++;
                Update(catalogs, files, bytes, failed, modelCount);
            }

            var budget = settings.MaxBytes * 8 / 10;
            foreach (var (url, size) in packageUrls)
            {
                if (bytes + (size ?? 0) > budget) break;
                var lookup = cache.BeginFile(new Uri(url), ExpectedHashes.None, size);
                switch (lookup.Kind)
                {
                    case DriverCacheStore.LookupKind.Hit:
                        files++;
                        bytes += lookup.Size;
                        break;
                    case DriverCacheStore.LookupKind.Flight:
                        var path = await lookup.Flight!.Done.Task.ConfigureAwait(false);
                        if (path is null) failed++;
                        else
                        {
                            files++;
                            bytes += new FileInfo(path).Length;
                        }
                        break;
                    default:
                        failed++;
                        break;
                }
                Update(catalogs, files, bytes, failed, modelCount);
            }
            return Finish(catalogs, files, bytes, failed, modelCount,
                $"Terminé : {catalogs} catalogue(s), {files} paquet(s) ({bytes / 1048576.0:0} Mo) pour {modelCount} modèle(s)" +
                (failed > 0 ? $", {failed} échec(s)." : "."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Préchargement du cache de pilotes interrompu.");
            return Finish(catalogs, files, bytes, failed, modelCount, $"Interrompu : {ex.Message}");
        }
    }

    /// <summary>Catalogues consultés par ce poste et paquets proposés dans son rapport (adresses officielles seulement).</summary>
    internal static void Collect(string rawReport, HashSet<string> catalogs, Dictionary<string, long?>? packages)
    {
        using var doc = JsonDocument.Parse(rawReport);
        var root = doc.RootElement;
        if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Object)
        {
            string? S(string name) => system.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
            var manufacturer = S("manufacturer") ?? "";
            var model = S("model") ?? "";
            var win11 = system.TryGetProperty("osBuild", out var b) && b.TryGetInt32(out var build) ? build >= 22000 :
                (S("osCaption") ?? "").Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
            if (manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
            {
                catalogs.Add("https://downloads.dell.com/catalog/CatalogPC.cab");
                catalogs.Add("https://downloads.dell.com/catalog/DriverPackCatalog.cab");
            }
            else if (manufacturer.StartsWith("HP", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase))
            {
                catalogs.Add("https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab");
                var platform = S("baseBoardProduct")?.ToUpperInvariant();
                var release = S("osDisplayVersion")?.ToLowerInvariant();
                if (platform is { Length: 4 } && platform.All(char.IsAsciiLetterOrDigit) && release is { Length: 4 } && release.All(char.IsAsciiLetterOrDigit))
                    catalogs.Add($"https://hpia.hpcloud.hp.com/ref/{platform}/{platform}_64_{(win11 ? "11" : "10")}.0.{release}.cab");
            }
            // Lenovo : catalogues XML non signés, jamais relayés (4.0.1) : les postes les lisent directement chez Lenovo.
            // Seuls les paquets proposés (ci-dessous, vérifiés par empreinte et signature sur le poste) sont préchargés.
        }
        if (packages is null || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return;
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.TryGetProperty("blockedReason", out var blocked) && blocked.ValueKind == JsonValueKind.String) continue;
            if (!c.TryGetProperty("downloadUrl", out var u) || u.ValueKind != JsonValueKind.String) continue;
            if (!Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) || !OfficialHosts.IsOfficialDownload(uri)) continue;
            long? size = c.TryGetProperty("sizeBytes", out var s) && s.TryGetInt64(out var sv) && sv > 0 ? sv : null;
            packages.TryAdd(DriverCacheStore.Normalize(uri), size);
        }
    }

    private void Update(int catalogs, int files, long bytes, int failed, int models)
    {
        lock (_lock)
            if (_status is { } s)
                _status = s with { Catalogs = catalogs, Files = files, Bytes = bytes, Failed = failed, Models = models };
    }

    private PrefetchStatus Finish(int catalogs, int files, long bytes, int failed, int models, string message)
    {
        lock (_lock)
        {
            var s = _status ?? new PrefetchStatus(true, clock.GetUtcNow(), null, "", "", 0, 0, 0, 0, 0, "");
            _status = s with
            {
                Running = false, FinishedAt = clock.GetUtcNow(), Catalogs = catalogs, Files = files, Bytes = bytes, Failed = failed, Models = models,
                Message = message,
            };
            log.LogInformation("Préchargement du cache de pilotes — {Message}", message);
            return _status;
        }
    }
}
