using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources;

/// <summary>
/// Source n°2 : Microsoft Update Catalog, interrogé par ID matériel. Couvre les pilotes que Windows Update
/// ne propose pas automatiquement (pilotes « optionnels », anciens matériels, machines sans accès WU direct).
/// Les paquets (.cab) sont vérifiés à l'installation : signature + l'INF doit réellement déclarer l'ID matériel.
/// </summary>
public sealed class MicrosoftCatalogSource : IDriverSource
{
    public string Name => "Microsoft Update Catalog";
    public DriverSourceKind Kind => DriverSourceKind.MicrosoftCatalog;
    private const int MaxAlternatives = 6;

    public bool IsEnabled(ScanContext context) => context.Settings.UseMicrosoftCatalog;

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var targets = context.Devices
            .Where(d => d.IsPresent && d.HardwareIds.Count > 0 && !IsUnusableId(d.HardwareIds[0]))
            .Where(d => d.HasProblem || (context.Options.DeepScan && d.IsHardware))
            .ToList();

        // Un même composant peut apparaître plusieurs fois (ex. 4 ports USB identiques) : une seule recherche.
        var groups = targets.GroupBy(d => d.HardwareIds[0], StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<DriverCandidate>();
        var done = 0;

        await Parallel.ForEachAsync(groups, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(context.Settings.CatalogConcurrency, 1, 8),
            CancellationToken = ct,
        }, async (group, token) =>
        {
            var device = group.First();
            try
            {
                var candidate = await FindForDeviceAsync(device, context, token).ConfigureAwait(false);
                if (candidate is not null)
                {
                    foreach (var other in group.Skip(1))
                    {
                        candidate.TargetDeviceIds.Add(other.InstanceId);
                        candidate.TargetDeviceNames.Add(other.Name);
                    }
                    lock (results) results.Add(candidate);
                }
            }
            catch (CatalogRateLimitedException ex)
            {
                context.Warnings.TryAdd(Name, $"Résultats partiels : {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Catalogue Microsoft : échec pour {device.Name} : {ex.Message}");
            }
            var n = Interlocked.Increment(ref done);
            context.Report(Name, $"Catalogue Microsoft : {n}/{groups.Count} composant(s) analysé(s)", 100.0 * n / Math.Max(1, groups.Count));
        }).ConfigureAwait(false);

        return results;
    }

    private static bool IsUnusableId(string id) =>
        id.StartsWith("USB\\DEVICE_DESCRIPTOR_FAILURE", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("USB\\RESET_FAILURE", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("USB\\CONFIGURATION_DESCRIPTOR_VALIDATION_FAILURE", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

    private async Task<DriverCandidate?> FindForDeviceAsync(DeviceInfo device, ScanContext context, CancellationToken ct)
    {
        foreach (var query in HardwareIdMatcher.CatalogQueries(device))
        {
            var entries = await MicrosoftCatalogClient.SearchAsync(query, ct).ConfigureAwait(false);
            var ranked = entries
                .Where(e => e.Classification.StartsWith("Driver", StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entry: e, Os: MicrosoftCatalogClient.OsApplicability(e.Products, context.System.OsBuild)))
                .Where(x => x.Os > 0)
                // Exclut les variantes ARM64 sur x64 (et inversement) quand le titre l'indique.
                .Where(x => ArchitectureCompatible(x.Entry.Title, context.System.Architecture))
                .OrderByDescending(x => x.Os)
                .ThenByDescending(x => DriverVersion.Parse(x.Entry.Version) ?? new Version(0, 0))
                .ThenByDescending(x => x.Entry.LastUpdated)
                .Select(x => x.Entry)
                .ToList();

            // Pas de dédoublonnage par titre : le même titre recouvre souvent des variantes x64 et ARM64 distinctes.
            // Toutes restent disponibles comme alternatives ; l'installeur écarte celles dont l'INF ne convient pas.
            if (ranked.Count == 0) continue;

            if (!device.HasProblem)
            {
                // Mise à jour d'un composant qui fonctionne : prudence maximale.
                // 1) un pilote Windows intégré (fournisseur Microsoft) n'est jamais remplacé automatiquement ;
                // 2) on ne compare que des versions du même fournisseur (les numérotations diffèrent entre éditeurs).
                if (IsInboxDriver(device)) return null;
                ranked = ranked.Where(e => SameVendor(e.Title, device.DriverProvider)).ToList();
                if (ranked.Count == 0) return null;
                var cmp = DriverVersion.Compare(ranked[0].Version, device.DriverVersion);
                if (cmp is null or <= 0) return null; // déjà à jour (ou incomparable : on ne touche pas à ce qui fonctionne)
            }
            var best = ranked[0];

            var candidate = ToCandidate(best, device, query);
            foreach (var alt in ranked.Skip(1).Take(MaxAlternatives))
                candidate.Alternatives.Add(ToCandidate(alt, device, query));
            return candidate;
        }
        return null;
    }

    internal static bool IsInboxDriver(DeviceInfo device) =>
        string.Equals(device.DriverProvider, "Microsoft", StringComparison.OrdinalIgnoreCase) ||
        (device.InfName is not null && !device.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase));

    /// <summary>Le titre du catalogue (« Realtek Semiconductor Corp. MEDIA Driver Update ») et le fournisseur installé concordent-ils ?</summary>
    internal static bool SameVendor(string catalogTitle, string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        static string FirstWord(string s) => new string(s.TrimStart().TakeWhile(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var a = FirstWord(catalogTitle);
        var b = FirstWord(provider);
        return a.Length >= 2 && a == b;
    }

    private static bool ArchitectureCompatible(string title, string arch)
    {
        var t = title.ToUpperInvariant();
        return arch switch
        {
            "amd64" => !t.Contains("ARM64"),
            "arm64" => !t.Contains("X64") && !t.Contains("AMD64"),
            _ => true,
        };
    }

    private static DriverCandidate ToCandidate(CatalogEntry entry, DeviceInfo device, string query)
    {
        var candidate = new DriverCandidate
        {
            Id = $"catalog:{entry.UpdateId}",
            Source = DriverSourceKind.MicrosoftCatalog,
            PackageKind = DriverPackageKind.InfPackage,
            Title = entry.Title,
            Category = entry.Classification,
            Version = entry.Version,
            Date = entry.LastUpdated,
            SizeBytes = entry.SizeBytes,
            SourceItemId = entry.UpdateId,
            MatchedHardwareId = query,
            InfoUrl = $"https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid={entry.UpdateId}",
            Kind = device.HasProblem ? CandidateKind.MissingDriver : CandidateKind.Update,
        };
        candidate.TargetDeviceIds.Add(device.InstanceId);
        candidate.TargetDeviceNames.Add(device.Name);
        candidate.MatchRank = HardwareIdMatcher.RankFor(device, query) ?? 0x800;
        return candidate;
    }
}
