using System.Globalization;
using System.Xml.Linq;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Oem;

/// <summary>
/// Fichiers de référence HP Image Assistant (hpia.hpcloud.hp.com/ref/{plateforme}/...) : la liste officielle des
/// SoftPaqs par plateforme et version de Windows. Paquets signés « HP Inc. », contrôle SHA-256.
/// </summary>
public sealed class HpSource : IDriverSource
{
    public string Name => "HP";
    public DriverSourceKind Kind => DriverSourceKind.HP;

    private static readonly string[] Win11Releases = { "25h2", "24h2", "23h2", "22h2", "21h2" };
    private static readonly string[] Win10Releases = { "22h2", "21h2", "21h1", "2009", "2004", "1909" };

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseOemCatalogs && context.System.Vendor == OemVendor.HP &&
        !string.IsNullOrWhiteSpace(context.System.BaseBoardProduct) && context.System.BaseBoardProduct!.Trim().Length == 4;

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var platform = context.System.BaseBoardProduct!.Trim().ToUpperInvariant();
        var osMajor = context.System.IsWindows11 ? "11" : "10";
        var current = (context.System.OsDisplayVersion ?? "").ToLowerInvariant();
        var releases = (context.System.IsWindows11 ? Win11Releases : Win10Releases).ToList();
        // On commence par la version installée puis on redescend (HP publie parfois la référence avec du retard).
        var start = releases.IndexOf(current);
        if (start > 0) releases = releases.Skip(start).ToList();

        context.Report(Name, $"Recherche de la référence HP pour la plateforme {platform}...");
        foreach (var rel in releases)
        {
            var url = $"https://hpia.hpcloud.hp.com/ref/{platform}/{platform}_64_{osMajor}.0.{rel}.cab";
            string cab;
            try
            {
                cab = await Http.GetCachedFileAsync(url, $"hp-{platform}-{osMajor}-{rel}.cab", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            var xml = await Archive.ExtractSingleFileCabAsync(cab, $"hp-{platform}-{osMajor}-{rel}", ct).ConfigureAwait(false);
            Log.Info($"Référence HP utilisée : {url}");
            return await Task.Run(() => Parse(xml, context), ct).ConfigureAwait(false);
        }
        Log.Warn($"Aucune référence HP trouvée pour la plateforme {platform}.");
        return Array.Empty<DriverCandidate>();
    }

    internal static List<DriverCandidate> Parse(string xmlPath, ScanContext context)
    {
        var doc = XDocument.Load(xmlPath);
        var root = doc.Root!;

        // Table SoftPaq → IDs matériels, depuis la section <Devices> de la référence.
        var deviceIdsBySoftpaq = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in root.Element("Devices")?.Elements("Device") ?? Enumerable.Empty<XElement>())
        {
            var id = dev.Element("DeviceId")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            foreach (var r in dev.Descendants("UpdateInfo"))
            {
                var sp = r.Attribute("IdRef")?.Value;
                if (sp is null) continue;
                if (!deviceIdsBySoftpaq.TryGetValue(sp, out var list)) deviceIdsBySoftpaq[sp] = list = new();
                list.Add(id);
            }
        }

        var includeFirmware = context.Settings.IncludeBiosAndFirmware;
        var results = new List<DriverCandidate>();
        foreach (var u in root.Element("Solutions")?.Elements("UpdateInfo") ?? Enumerable.Empty<XElement>())
        {
            string? V(string name) => u.Element(name)?.Value?.Trim();
            var id = V("Id");
            var category = V("Category") ?? "";
            var url = V("Url");
            if (id is null || url is null) continue;

            var isDriver = category.StartsWith("Driver", StringComparison.OrdinalIgnoreCase) || category.Contains("Driver", StringComparison.OrdinalIgnoreCase);
            var isBios = category.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase);
            var isFirmware = isBios || category.Contains("Firmware", StringComparison.OrdinalIgnoreCase);

            // État du BIOS et firmwares d'accessoires : toujours évalués (information), même sans --include-firmware.
            var (importance, importanceLabel) = ReleaseType(u);
            if (isBios && !isDriver)
            {
                context.BiosEntries.Add(new BiosCatalogEntry
                {
                    Title = V("Name") ?? id,
                    Version = V("Version"),
                    Date = DateTime.TryParse(V("DateReleased"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd) ? bd : null,
                    Importance = importance,
                    ImportanceLabel = importanceLabel,
                    DownloadUrl = TrustPolicy.NormalizeUrl(url).ToString(),
                    InfoUrl = V("ReleaseNotesUrl") is { } biosNotes ? TrustPolicy.NormalizeUrl(biosNotes).ToString() : null,
                    CandidateId = $"hp:{id}",
                });
            }
            else if (isFirmware && !isDriver && BiosEvaluator.AccessoryKind(V("Name"), category) is { } accessory)
            {
                BiosEvaluator.AddComponent(context, accessory, V("Name") ?? id, V("Version"),
                    DateTime.TryParse(V("DateReleased"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ad) ? ad : null,
                    importance, importanceLabel, TrustPolicy.NormalizeUrl(url).ToString(),
                    V("ReleaseNotesUrl") is { } accNotes ? TrustPolicy.NormalizeUrl(accNotes).ToString() : null,
                    deviceIdsBySoftpaq.TryGetValue(id, out var accIds) ? accIds : new List<string>());
            }
            if (!isDriver && !(isFirmware && includeFirmware)) continue;

            var candidate = new DriverCandidate
            {
                Id = $"hp:{id}",
                Source = DriverSourceKind.HP,
                PackageKind = DriverPackageKind.VendorInstaller,
                Title = V("Name") ?? id,
                Manufacturer = V("Vendor") ?? "HP",
                Category = category,
                Version = V("Version"),
                Date = DateTime.TryParse(V("DateReleased"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                SizeBytes = long.TryParse(V("Size"), out var size) ? size : null,
                DownloadUrl = TrustPolicy.NormalizeUrl(url).ToString(),
                Sha256 = V("SHA256"),
                Md5 = V("SHA256") is null ? V("MD5") : null,
                ExtractCommand = "\"%FILE%\" /s /e /f \"%PACKAGEPATH%\"",
                SilentCommand = V("SilentInstall"),
                InfoUrl = V("ReleaseNotesUrl") is { } notes ? TrustPolicy.NormalizeUrl(notes).ToString() : null,
                SourceItemId = id,
                Kind = isFirmware && !isDriver ? CandidateKind.Firmware : CandidateKind.Update,
            };
            if (string.IsNullOrWhiteSpace(candidate.SilentCommand)) continue;
            // Ligne de commande exécutée en SYSTEM : même contrôle de forme que pour Lenovo (référence HP signée, mais
            // défense en profondeur) ; un SoftPaq à la commande inattendue est écarté.
            if ((VendorCommand.Validate(candidate.ExtractCommand, Path.GetFileName(new Uri(candidate.DownloadUrl).AbsolutePath)) ??
                 VendorCommand.Validate(candidate.SilentCommand)) is { } problem)
            {
                Log.Warn($"SoftPaq HP écarté ({id}) : commande refusée ({problem}) : {candidate.SilentCommand}");
                continue;
            }

            if (deviceIdsBySoftpaq.TryGetValue(id, out var hwIds))
            {
                var matches = hwIds.SelectMany(h => OemHelpers.MatchDevices(context.Devices, h)).ToList();
                if (matches.Count == 0) continue;
                if (!OemHelpers.IsNewerForAny(candidate.Version, matches.Select(m => m.Device))) continue;
                OemHelpers.Attach(candidate, matches);
            }
            else if (candidate.Kind != CandidateKind.Firmware)
            {
                candidate.Kind = CandidateKind.Optional;
            }
            results.Add(candidate);
        }
        BiosEvaluator.Finish(context, OemVendor.HP, results);
        Log.Info($"Référence HP : {results.Count} SoftPaq(s) applicable(s).");
        return results;
    }

    /// <summary>Type de publication du SoftPaq (« Critical », « Recommended », « Routine »).</summary>
    private static (VendorImportance Importance, string? Label) ReleaseType(XElement u)
    {
        var text = (u.Element("ReleaseType") ?? u.Element("Criticality") ?? u.Element("Severity"))?.Value?.Trim();
        return string.IsNullOrEmpty(text) ? (VendorImportance.Unknown, null) : (BiosEvaluator.ImportanceFromText(text), text);
    }
}
