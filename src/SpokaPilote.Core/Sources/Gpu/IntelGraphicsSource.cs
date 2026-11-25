using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Gpu;

/// <summary>
/// Pilotes graphiques Intel (Arc, Iris Xe, UHD/HD). Intel ne publie pas de catalogue lisible par machine :
/// les pages du centre de téléchargement sont protégées contre les clients automatisés (HTTP 403 hors navigateur)
/// et le service de l'Assistant Intel (Intel Driver &amp; Support Assistant) n'est pas documenté. Plutôt que d'aspirer
/// des pages fragiles, SpokaPilote propose un candidat INFORMATIF : lien vers la page officielle adaptée à la carte,
/// non coché et jamais installé automatiquement. Les pilotes qu'Intel publie sur Windows Update et le catalogue
/// Microsoft restent installés automatiquement par ces sources.
/// </summary>
public sealed partial class IntelGraphicsSource : IDriverSource
{
    internal const string ArcPage = "https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html";
    internal const string LegacyPage = "https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html";
    internal const string AssistantPage = "https://www.intel.com/content/www/us/en/support/detect.html";

    public string Name => "Intel (graphique)";
    public DriverSourceKind Kind => DriverSourceKind.Intel;

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseGpuVendorSources && GpuAdapters.Find(context.Devices, GpuVendorIds.Intel).Count > 0;

    public Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var adapters = GpuAdapters.Find(context.Devices, GpuVendorIds.Intel);
        IReadOnlyList<DriverCandidate> result = BuildCandidates(adapters, context.System);
        Log.Info($"Intel : {adapters.Count} adaptateur(s) graphique(s), {result.Count} lien(s) officiel(s) proposé(s) (vérification manuelle).");
        return Task.FromResult(result);
    }

    internal enum IntelFamily { Current, Legacy, Unknown }

    [GeneratedRegex(@"\bArc\b|Iris\(R\) Xe|Iris Xe|UHD Graphics 7\d\d|^Intel\(R\) Graphics$|Core\(TM\) Ultra", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentNames();

    [GeneratedRegex(@"\bHD Graphics\b|UHD Graphics 6\d\d|Iris\(R\) Plus|Iris\(R\) Pro|Iris\(R\) Graphics", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyNames();

    /// <summary>
    /// Famille de pilote d'après le nom commercial : branche actuelle (Arc, Iris Xe, UHD 7xx, Core Ultra : 11e génération
    /// et suivantes) ou branche héritée (HD / UHD 6xx / Iris Plus / Iris Pro : 7e à 10e génération). En cas de doute
    /// (ex. « UHD Graphics » sans numéro), l'Assistant Intel identifie la carte.
    /// </summary>
    internal static IntelFamily Classify(string? adapterName)
    {
        var name = adapterName ?? "";
        if (CurrentNames().IsMatch(name)) return IntelFamily.Current;
        if (LegacyNames().IsMatch(name)) return IntelFamily.Legacy;
        return IntelFamily.Unknown;
    }

    internal static List<DriverCandidate> BuildCandidates(IReadOnlyList<GpuAdapter> adapters, SystemInfo system)
    {
        var results = new List<DriverCandidate>();
        if (system.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)) return results; // plus de pilotes 32 bits
        foreach (var family in adapters.GroupBy(a => Classify(a.Device.Name)))
        {
            var (page, label) = family.Key switch
            {
                IntelFamily.Current => (ArcPage, "Intel Arc / Iris Xe / UHD (11e génération et suivantes)"),
                IntelFamily.Legacy => (LegacyPage, "Intel HD / UHD / Iris Plus (7e à 10e génération)"),
                _ => (AssistantPage, "Intel (identification par l'Assistant Intel)"),
            };
            results.Add(new DriverCandidate
            {
                Id = $"intel-gfx:{family.Key.ToString().ToLowerInvariant()}",
                Source = DriverSourceKind.Intel,
                PackageKind = DriverPackageKind.VendorInstaller,
                IsInformational = true,
                Title = $"Pilote graphique {label} — à vérifier sur intel.com : {GpuAdapters.Describe(family)}",
                Manufacturer = "Intel",
                Category = "Carte graphique",
                InfoUrl = page,
                // Informatif : non coché, non installable, et sans périphérique ciblé pour ne pas masquer
                // les pilotes installables proposés par Windows Update ou le catalogue Microsoft.
                Kind = CandidateKind.Optional,
            });
        }
        return results;
    }
}
