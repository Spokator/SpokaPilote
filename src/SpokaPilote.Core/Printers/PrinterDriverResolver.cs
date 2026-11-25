using System.Management;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Printers;

/// <summary>
/// Trouve le bon pilote pour une imprimante, du plus précis au plus universel :
/// pilote déjà présent → dépôt interne → Microsoft Update Catalog (modèle exact puis universel constructeur)
/// → pilote de classe IPP Windows → page officielle du constructeur.
/// </summary>
public static class PrinterDriverResolver
{
    public const string IppClassDriver = "Microsoft IPP Class Driver";

    public static async Task<List<PrinterDriverOption>> ResolveAsync(DiscoveredPrinter printer, Settings settings, CancellationToken ct = default)
    {
        var options = new List<PrinterDriverOption>();
        var model = printer.Model ?? printer.Description ?? "";
        var vendor = PrinterVendorCatalog.FindVendor(printer.Manufacturer) ?? PrinterVendorCatalog.FindVendor(model);

        // 1. Pilotes déjà installés sur le poste.
        foreach (var driver in InstalledPrinterDrivers())
        {
            var score = PrinterVendorCatalog.ModelScore(driver, model);
            var universal = vendor?.UniversalDrivers.Any(u => driver.StartsWith(u, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (score >= 70)
                options.Add(new PrinterDriverOption { Kind = PrinterDriverOptionKind.Installed, DriverName = driver, Label = driver, Score = 300 + score });
            else if (universal)
                options.Add(new PrinterDriverOption { Kind = PrinterDriverOptionKind.Installed, DriverName = driver, Label = driver, Score = 200, IsUniversal = true });
        }

        // 2. Dépôts internes (INF de classe Printer).
        foreach (var root in settings.RepositoryPaths)
        {
            var repo = new DriverRepository(root);
            await repo.IndexAsync(ct: ct).ConfigureAwait(false);
            foreach (var inf in repo.Infs.Where(i => string.Equals(i.Class, "Printer", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var name in inf.Models.Select(m => m.Description).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var score = PrinterVendorCatalog.ModelScore(name, model);
                    var universal = vendor?.UniversalDrivers.Any(u => name.StartsWith(u, StringComparison.OrdinalIgnoreCase)) ?? false;
                    if (score < 70 && !universal) continue;
                    options.Add(new PrinterDriverOption
                    {
                        Kind = PrinterDriverOptionKind.LocalRepository,
                        DriverName = name,
                        Label = $"{name} ({Path.GetFileName(inf.Path)}, v{inf.Version})",
                        InfPath = inf.Path,
                        Score = universal && score < 70 ? 190 : 280 + score,
                        IsUniversal = universal && score < 70,
                    });
                }
            }
        }

        // 3. Microsoft Update Catalog : modèle exact, puis pilote universel du constructeur.
        if (settings.UseMicrosoftCatalog && model.Length > 0)
        {
            var queries = new List<(string Query, bool Universal)>();
            var shortModel = vendor is null ? model : StripVendor(model, vendor);
            queries.Add((shortModel, false));
            if (!shortModel.Equals(model, StringComparison.OrdinalIgnoreCase)) queries.Add((model, false));
            if (vendor is not null) queries.AddRange(vendor.CatalogQueries.Select(q => (q, true)));

            var exactFound = false;
            foreach (var (query, universal) in queries)
            {
                if (!universal && exactFound) continue; // modèle exact déjà trouvé : inutile d'essayer la variante longue
                List<CatalogEntry> entries;
                try
                {
                    entries = await MicrosoftCatalogClient.SearchAsync(query, ct).ConfigureAwait(false);
                }
                catch (CatalogRateLimitedException ex)
                {
                    Log.Warn(ex.Message);
                    break;
                }
                var build = Environment.OSVersion.Version.Build;
                var ranked = entries
                    .Where(e => e.Classification.Contains("Printer", StringComparison.OrdinalIgnoreCase) || e.Title.Contains("Printer", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.Title.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
                    .Where(e => MicrosoftCatalogClient.OsApplicability(e.Products, build) > 0)
                    .OrderByDescending(e => MicrosoftCatalogClient.OsApplicability(e.Products, build))
                    .ThenByDescending(e => DriverVersion.Parse(e.Version) ?? new Version(0, 0))
                    .ThenByDescending(e => e.LastUpdated)
                    .ToList();
                // Le même pilote est publié une fois par version de Windows et par architecture (titre identique) :
                // une seule option affichée, toutes les variantes conservées pour l'installation.
                var groups = ranked.GroupBy(e => (e.Title, e.Version)).Take(universal ? 1 : 2).ToList();
                foreach (var g in groups)
                {
                    var e = g.First();
                    if (options.Any(o => o.CatalogUpdateId == e.UpdateId || o.Label.StartsWith(e.Title, StringComparison.OrdinalIgnoreCase))) continue;
                    options.Add(new PrinterDriverOption
                    {
                        Kind = PrinterDriverOptionKind.MicrosoftCatalog,
                        DriverName = null, // déterminé après extraction, par correspondance avec le modèle
                        Label = $"{e.Title}{(e.Version is null || e.Title.Contains(e.Version) ? "" : $" v{e.Version}")}",
                        CatalogUpdateId = e.UpdateId,
                        CatalogVariantIds = g.Select(x => x.UpdateId).ToList(),
                        Score = universal ? 150 : 250,
                        IsUniversal = universal,
                    });
                }
                var best = groups;
                if (!universal && best.Count > 0) exactFound = true;
            }
        }

        // 4. Pilote de classe IPP (intégré à Windows, compatible IPP Everywhere / Mopria).
        if (printer.SupportsIpp)
        {
            options.Add(new PrinterDriverOption
            {
                Kind = PrinterDriverOptionKind.IppClass,
                DriverName = IppClassDriver,
                Label = "Pilote IPP universel Windows (aucun téléchargement, fonctions de base)",
                Score = 100,
                IsUniversal = true,
                Url = printer.IppUri?.ToString(),
            });
        }

        // 5. Lien officiel en dernier recours.
        if (vendor?.SupportUrl is not null)
        {
            options.Add(new PrinterDriverOption
            {
                Kind = PrinterDriverOptionKind.VendorPage,
                Label = $"Page officielle {vendor.Name} (téléchargement manuel puis import dans le dépôt)",
                Url = vendor.SupportUrl,
                Score = 0,
            });
        }

        return options.OrderByDescending(o => o.Score).ToList();
    }

    private static string StripVendor(string model, PrinterVendor vendor)
    {
        foreach (var m in vendor.Match.OrderByDescending(x => x.Length))
            if (model.StartsWith(m + " ", StringComparison.OrdinalIgnoreCase)) return model[(m.Length + 1)..].Trim();
        return model;
    }

    public static List<string> InstalledPrinterDrivers()
    {
        var list = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterDriver");
            foreach (var o in s.Get())
                using (o)
                {
                    // Format WMI : « HP Universal Printing PCL 6,3,Windows x64 »
                    var name = o["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) list.Add(name.Split(',')[0]);
                }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des pilotes d'impression impossible : {ex.Message}");
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
