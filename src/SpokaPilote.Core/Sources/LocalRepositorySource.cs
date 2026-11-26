using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;

namespace SpokaPilote.Core.Sources;

/// <summary>
/// Source prioritaire en entreprise : les dépôts validés par l'équipe IT (partage réseau, clé USB, export d'un
/// poste de référence). Fonctionne hors ligne : idéal pour déployer 100 postes identiques.
/// </summary>
public sealed class LocalRepositorySource : IDriverSource
{
    public string Name => "Dépôt local";
    public DriverSourceKind Kind => DriverSourceKind.LocalRepository;

    public bool IsEnabled(ScanContext context) => context.Settings.RepositoryPaths.Count > 0;

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var byInf = new Dictionary<string, DriverCandidate>(StringComparer.OrdinalIgnoreCase);
        var bestForDevice = new Dictionary<string, (DriverCandidate Candidate, int Rank, Version? Version)>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in context.Settings.RepositoryPaths)
        {
            context.Report(Name, $"Indexation du dépôt {root}...");
            var repo = new DriverRepository(root);
            await repo.IndexAsync(new Progress<string>(m => context.Report(Name, m)), ct).ConfigureAwait(false);

            // Index inverse ID → INF pour éviter un produit cartésien périphériques × INF.
            var byId = new Dictionary<string, List<IndexedInf>>(StringComparer.OrdinalIgnoreCase);
            foreach (var inf in repo.Infs)
            {
                foreach (var model in inf.Models)
                {
                    if (!InfParser.DecorationApplies(model.Decoration, context.System.Architecture, context.System.OsVersion)) continue;
                    if (!byId.TryGetValue(model.HardwareId, out var list)) byId[model.HardwareId] = list = new();
                    if (!list.Contains(inf)) list.Add(inf);
                }
            }

            foreach (var device in context.Devices.Where(d => d.IsPresent))
            {
                var ids = device.HardwareIds.Concat(device.CompatibleIds.Where(c => !HardwareIdMatcher.IsGenericCompatibleId(c)));
                foreach (var id in ids)
                {
                    if (!byId.TryGetValue(id, out var infs)) continue;
                    var rank = HardwareIdMatcher.RankFor(device, id)!.Value;
                    foreach (var inf in infs)
                    {
                        var infVersion = DriverVersion.Parse(inf.Version);
                        if (!device.HasProblem)
                        {
                            var cmp = DriverVersion.Compare(inf.Version, device.DriverVersion);
                            if (cmp is null or <= 0) continue;
                        }

                        if (bestForDevice.TryGetValue(device.InstanceId, out var current) &&
                            (current.Rank < rank || (current.Rank == rank && (current.Version ?? new Version()) >= (infVersion ?? new Version()))))
                            continue;

                        if (!byInf.TryGetValue(inf.Path, out var candidate))
                        {
                            candidate = new DriverCandidate
                            {
                                Id = $"repo:{inf.Path}",
                                Source = DriverSourceKind.LocalRepository,
                                PackageKind = DriverPackageKind.InfPackage,
                                Title = $"{inf.Models.FirstOrDefault(m => m.HardwareId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Description ?? Path.GetFileName(inf.Path)}",
                                Manufacturer = inf.Provider,
                                Category = inf.Class,
                                Version = inf.Version,
                                Date = inf.Date,
                                LocalPath = inf.Path,
                                // Firmware UEFI (packs Surface, capsules constructeurs) : appliqué au redémarrage,
                                // installé seulement sur demande explicite (--include-firmware), comme chez Dell / HP.
                                Kind = InfParser.IsFirmwareClass(inf.Class) ? CandidateKind.Firmware
                                     : device.HasProblem ? CandidateKind.MissingDriver : CandidateKind.Update,
                            };
                            byInf[inf.Path] = candidate;
                        }
                        bestForDevice[device.InstanceId] = (candidate, rank, infVersion);
                    }
                }
            }
        }

        // Reconstitue les candidats à partir des meilleurs choix par périphérique.
        var results = new Dictionary<string, DriverCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceId, choice) in bestForDevice)
        {
            var device = context.Devices.First(d => d.InstanceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            var c = choice.Candidate;
            c.TargetDeviceIds.Add(device.InstanceId);
            c.TargetDeviceNames.Add(device.Name);
            if (choice.Rank < c.MatchRank)
            {
                c.MatchRank = choice.Rank;
                c.MatchedHardwareId = choice.Rank >= HardwareIdMatcher.CompatibleIdOffset
                    ? device.CompatibleIds[choice.Rank - HardwareIdMatcher.CompatibleIdOffset]
                    : device.HardwareIds[choice.Rank];
            }
            if (device.HasProblem && c.Kind != CandidateKind.Firmware) c.Kind = CandidateKind.MissingDriver;
            results[c.Id] = c;
        }
        Log.Info($"Dépôt local : {results.Count} paquet(s) applicable(s).");
        return results.Values.ToList();
    }
}
