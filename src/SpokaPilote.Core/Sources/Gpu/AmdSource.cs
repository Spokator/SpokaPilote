using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Gpu;

/// <summary>
/// Pilotes graphiques AMD Radeon. AMD ne publie aucun service ni catalogue lisible par machine : le sélecteur de
/// pilotes d'amd.com est une application web, et drivers.amd.com refuse les téléchargements qui ne proviennent pas
/// de ses pages (contrôle du référent). Plutôt que du scraping fragile, SpokaPilote propose un candidat INFORMATIF :
/// lien officiel, non coché, jamais installé automatiquement. Les pilotes qu'AMD publie sur Windows Update et le
/// catalogue Microsoft restent installés automatiquement par ces sources.
/// </summary>
public sealed class AmdSource : IDriverSource
{
    internal const string DriversPage = "https://www.amd.com/fr/support/download/drivers.html";
    internal const string ProPage = "https://www.amd.com/fr/products/software/pro-edition.html";

    public string Name => "AMD (graphique)";
    public DriverSourceKind Kind => DriverSourceKind.Amd;

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseGpuVendorSources && GpuAdapters.Find(context.Devices, GpuVendorIds.Amd).Count > 0;

    public Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var adapters = GpuAdapters.Find(context.Devices, GpuVendorIds.Amd);
        IReadOnlyList<DriverCandidate> result = BuildCandidates(adapters, context.System);
        Log.Info($"AMD : {adapters.Count} adaptateur(s) graphique(s), lien officiel proposé (vérification manuelle).");
        return Task.FromResult(result);
    }

    internal static List<DriverCandidate> BuildCandidates(IReadOnlyList<GpuAdapter> adapters, SystemInfo system)
    {
        var results = new List<DriverCandidate>();
        if (adapters.Count == 0 || !system.Architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase)) return results;

        // Cartes professionnelles (Radeon PRO / FirePro) : édition « AMD Software: PRO Edition », la branche
        // certifiée pour les postes de travail ; sinon « AMD Software: Adrenalin Edition ».
        foreach (var group in adapters.GroupBy(a => IsProCard(a.Device.Name)))
        {
            var edition = group.Key ? "AMD Software: PRO Edition" : "AMD Software: Adrenalin Edition";
            results.Add(new DriverCandidate
            {
                Id = group.Key ? "amd-gfx:pro" : "amd-gfx:adrenalin",
                Source = DriverSourceKind.Amd,
                PackageKind = DriverPackageKind.VendorInstaller,
                IsInformational = true,
                Title = $"Pilote graphique AMD ({edition}) — à vérifier sur amd.com : {GpuAdapters.Describe(group)}",
                Manufacturer = "AMD",
                Category = "Carte graphique",
                InfoUrl = group.Key ? ProPage : DriversPage,
                // Informatif : non coché, non installable, sans périphérique ciblé (n'écarte pas Windows Update).
                Kind = CandidateKind.Optional,
            });
        }
        return results;
    }

    internal static bool IsProCard(string? name) =>
        (name ?? "").Contains("Radeon PRO", StringComparison.OrdinalIgnoreCase) ||
        (name ?? "").Contains("Radeon(TM) PRO", StringComparison.OrdinalIgnoreCase) ||
        (name ?? "").Contains("FirePro", StringComparison.OrdinalIgnoreCase);
}
