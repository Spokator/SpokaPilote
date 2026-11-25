using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Printers;

public static partial class PrinterVendorCatalog
{
    private static List<PrinterVendor>? _vendors;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static IReadOnlyList<PrinterVendor> Vendors => _vendors ??= LoadEmbedded();

    private static List<PrinterVendor> LoadEmbedded()
    {
        using var stream = typeof(PrinterVendorCatalog).Assembly.GetManifestResourceStream("SpokaPilote.Core.Printers.printer-vendors.json");
        if (stream is null) return new();
        return JsonSerializer.Deserialize<PrinterVendorCatalogFile>(stream, Options)?.Vendors ?? new();
    }

    /// <summary>Fusionne un catalogue complémentaire publié par l'organisation (HTTPS obligatoire).</summary>
    public static async Task LoadOrganizationCatalogAsync(Settings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.PrinterCatalogUrl)) return;
        if (!settings.PrinterCatalogUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Le catalogue d'imprimantes de l'organisation doit être servi en HTTPS : ignoré.");
            return;
        }
        try
        {
            var json = await Http.GetStringAsync(settings.PrinterCatalogUrl, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var extra = JsonSerializer.Deserialize<PrinterVendorCatalogFile>(json, Options)?.Vendors ?? new();
            var merged = Vendors.ToList();
            foreach (var v in extra)
            {
                var existing = merged.FirstOrDefault(m => m.Id.Equals(v.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null) merged.Add(v);
                else
                {
                    existing.Match = existing.Match.Union(v.Match, StringComparer.OrdinalIgnoreCase).ToList();
                    existing.UniversalDrivers = v.UniversalDrivers.Union(existing.UniversalDrivers, StringComparer.OrdinalIgnoreCase).ToList();
                    existing.CatalogQueries = v.CatalogQueries.Union(existing.CatalogQueries, StringComparer.OrdinalIgnoreCase).ToList();
                    existing.SupportUrl = v.SupportUrl ?? existing.SupportUrl;
                }
            }
            _vendors = merged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Catalogue d'imprimantes de l'organisation indisponible : {ex.Message}");
        }
    }

    public static PrinterVendor? FindVendor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = " " + Normalize(text) + " ";
        return Vendors
            .SelectMany(v => v.Match.Select(m => (Vendor: v, Pattern: m)))
            .OrderByDescending(x => x.Pattern.Length)
            .FirstOrDefault(x => t.Contains(" " + Normalize(x.Pattern) + " ", StringComparison.Ordinal)).Vendor;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    public static string Normalize(string s) => NonAlnum().Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>
    /// Score de correspondance entre un nom de pilote (INF) et le modèle détecté.
    /// 100 = identique ; 80 = l'un contient l'autre ; 70 = tous les numéros de modèle présents ; sinon recouvrement.
    /// </summary>
    public static int ModelScore(string driverName, string model)
    {
        var a = StripNoise(Normalize(driverName));
        var b = StripNoise(Normalize(model));
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 100;
        if (a.Contains(b) || b.Contains(a)) return 80;

        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var numericTokens = tb.Where(t => t.Any(char.IsDigit)).ToList();
        if (numericTokens.Count > 0 && numericTokens.All(ta.Contains)) return 70;
        var overlap = tb.Count(ta.Contains);
        return (int)(50.0 * overlap / Math.Max(ta.Count, tb.Count));
    }

    private static readonly HashSet<string> NoiseTokens = new() { "series", "printer", "pcl6", "pcl5", "pcl5e", "pcl", "ps", "ps3", "kx", "class", "driver", "v4" };

    private static string StripNoise(string s)
    {
        s = " " + s.Replace("hewlett packard", "hp") + " ";
        s = s.Replace(" pcl 6 ", " pcl6 ").Replace(" pcl 5 ", " pcl5 ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !NoiseTokens.Contains(t)));
    }
}
