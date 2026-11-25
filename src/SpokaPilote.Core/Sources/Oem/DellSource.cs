using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Oem;

/// <summary>
/// Catalogue officiel Dell (downloads.dell.com/catalog/CatalogPC.cab, celui utilisé par Dell Command | Update).
/// Filtré par l'identifiant système (SKU) de la machine ; paquets DUP signés « Dell Inc », contrôle MD5.
/// </summary>
public sealed class DellSource : IDriverSource
{
    private const string CatalogUrl = "https://downloads.dell.com/catalog/CatalogPC.cab";
    public string Name => "Dell";
    public DriverSourceKind Kind => DriverSourceKind.Dell;

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseOemCatalogs && context.System.Vendor == OemVendor.Dell && !string.IsNullOrWhiteSpace(context.System.SystemSku);

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        context.Report(Name, "Téléchargement du catalogue Dell...");
        var cab = await Http.GetCachedFileAsync(CatalogUrl, "dell-CatalogPC.cab", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var xmlPath = await Archive.ExtractSingleFileCabAsync(cab, "dell-CatalogPC", ct).ConfigureAwait(false);
        context.Report(Name, $"Analyse du catalogue Dell pour le système {context.System.SystemSku}...");
        return await Task.Run(() => Parse(xmlPath, context), ct).ConfigureAwait(false);
    }

    internal static List<DriverCandidate> Parse(string xmlPath, ScanContext context)
    {
        var sku = context.System.SystemSku!.Trim();
        var wantWin11 = context.System.IsWindows11;
        var wantArch = context.System.Architecture == "arm64" ? "arm64" : "x64";
        var includeFirmware = context.Settings.IncludeBiosAndFirmware;

        var latest = new Dictionary<string, (XElement El, DateTime Date)>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "SoftwareComponent")
            {
                reader.Read();
                continue;
            }
            if (XNode.ReadFrom(reader) is not XElement el) continue;

            var type = el.Element("ComponentType")?.Attribute("value")?.Value;
            var isFirmware = type is "BIOS" or "FRMW";
            if (type != "DRVR" && !(isFirmware && includeFirmware)) continue;

            var forThisModel = el.Element("SupportedSystems")?.Descendants("Model")
                .Any(m => string.Equals(m.Attribute("systemID")?.Value, sku, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!forThisModel) continue;

            var oses = el.Element("SupportedOperatingSystems")?.Elements("OperatingSystem").ToList() ?? new();
            var osOk = oses.Any(os =>
                string.Equals(os.Attribute("osArch")?.Value, wantArch, StringComparison.OrdinalIgnoreCase) &&
                (os.Element("Display")?.Value ?? "").Contains(wantWin11 ? "Windows 11" : "Windows 10", StringComparison.OrdinalIgnoreCase));
            if (!osOk) continue;

            var name = el.Element("Name")?.Element("Display")?.Value ?? "";
            var key = (name.Split(',')[0] + "|" + el.Element("Category")?.Attribute("value")?.Value).Trim();
            var date = ParseDate(el.Attribute("dateTime")?.Value) ?? DateTime.MinValue;
            if (!latest.TryGetValue(key, out var existing) || date > existing.Date) latest[key] = (el, date);
        }

        var results = new List<DriverCandidate>();
        foreach (var (el, date) in latest.Values)
        {
            var type = el.Element("ComponentType")?.Attribute("value")?.Value;
            var version = el.Attribute("vendorVersion")?.Value;
            var dellVersion = el.Attribute("dellVersion")?.Value;
            var path = el.Attribute("path")?.Value ?? "";
            var name = (el.Element("Name")?.Element("Display")?.Value ?? path).Split(',')[0].Trim();

            var candidate = new DriverCandidate
            {
                Id = $"dell:{el.Attribute("releaseID")?.Value ?? path}",
                Source = DriverSourceKind.Dell,
                PackageKind = DriverPackageKind.VendorInstaller,
                Title = name,
                Manufacturer = "Dell",
                Category = el.Element("Category")?.Element("Display")?.Value,
                Version = string.IsNullOrEmpty(dellVersion) ? version : $"{version} ({dellVersion})",
                Date = date == DateTime.MinValue ? null : date,
                SizeBytes = long.TryParse(el.Attribute("size")?.Value, out var size) ? size : null,
                DownloadUrl = $"https://downloads.dell.com/{path.TrimStart('/')}",
                Md5 = el.Attribute("hashMD5")?.Value,
                SilentCommand = "\"%FILE%\" /s /l=\"%PACKAGEPATH%\\dup.log\"",
                SuccessExitCodes = new[] { 0, 2, 6 },
                RebootExitCodes = new[] { 2, 6 },
                NotApplicableExitCodes = new[] { 3, 5 },
                RebootRequired = string.Equals(el.Attribute("rebootRequired")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                SourceItemId = el.Attribute("releaseID")?.Value,
                InfoUrl = $"https://www.dell.com/support/home/drivers/driversdetails?driverid={el.Attribute("releaseID")?.Value}",
                Kind = type is "BIOS" or "FRMW" ? CandidateKind.Firmware : CandidateKind.Update,
            };

            var pciInfos = el.Element("SupportedDevices")?.Descendants("PCIInfo").ToList() ?? new();
            if (pciInfos.Count > 0)
            {
                var matches = pciInfos
                    .Select(p => $"PCI\\VEN_{p.Attribute("vendorID")?.Value}&DEV_{p.Attribute("deviceID")?.Value}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .SelectMany(id => OemHelpers.MatchDevices(context.Devices, id))
                    .ToList();
                if (matches.Count == 0) continue; // composant destiné à une autre variante matérielle du modèle
                if (!OemHelpers.IsNewerForAny(version, matches.Select(m => m.Device))) continue;
                OemHelpers.Attach(candidate, matches);
            }
            else if (candidate.Kind != CandidateKind.Firmware)
            {
                // Pas d'identification matérielle dans le catalogue : proposé mais non coché par défaut.
                candidate.Kind = CandidateKind.Optional;
            }
            else if (candidate.Kind == CandidateKind.Firmware && type == "BIOS" &&
                     string.Equals(version, context.System.BiosVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue; // BIOS déjà à jour
            }

            results.Add(candidate);
        }
        Log.Info($"Catalogue Dell : {results.Count} paquet(s) pour le système {sku}.");
        return results;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
