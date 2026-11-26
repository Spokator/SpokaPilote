using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Oem;

/// <summary>
/// Catalogue officiel Lenovo (download.lenovo.com/catalog/{MTM}_Win11.xml, utilisé par Lenovo System Update /
/// Commercial Vantage). Chaque paquet est décrit par un XML signé ; contrôle SHA-256 du descripteur et de l'installeur.
/// </summary>
public sealed class LenovoSource : IDriverSource
{
    public string Name => "Lenovo";
    public DriverSourceKind Kind => DriverSourceKind.Lenovo;

    public bool IsEnabled(ScanContext context) =>
        context.Settings.UseOemCatalogs && context.System.Vendor == OemVendor.Lenovo && context.System.Model.Length >= 4;

    public async Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
    {
        var mtm = context.System.Model[..4].ToUpperInvariant();
        var os = context.System.IsWindows11 ? "Win11" : "Win10";
        var url = $"https://download.lenovo.com/catalog/{mtm}_{os}.xml";
        context.Report(Name, $"Téléchargement du catalogue Lenovo {mtm}...");

        string catalogPath;
        try
        {
            catalogPath = await Http.GetCachedFileAsync(url, $"lenovo-{mtm}-{os}.xml", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"Catalogue Lenovo indisponible pour {mtm} ({os}) : {ex.Message}");
            return Array.Empty<DriverCandidate>();
        }

        var packages = XDocument.Load(catalogPath).Root?.Elements("package")
            .Select(p => (Location: p.Element("location")?.Value?.Trim(), Category: p.Element("category")?.Value?.Trim(),
                          Sha256: p.Element("checksum")?.Attribute("type")?.Value == "sha256" ? p.Element("checksum")?.Value?.Trim() : null))
            .Where(p => !string.IsNullOrEmpty(p.Location))
            .ToList() ?? new();

        var results = new List<DriverCandidate>();
        var done = 0;
        await Parallel.ForEachAsync(packages, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (pkg, token) =>
        {
            try
            {
                var candidate = await ReadPackageAsync(pkg.Location!, pkg.Category, pkg.Sha256, context, token).ConfigureAwait(false);
                if (candidate is not null) lock (results) results.Add(candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"Paquet Lenovo ignoré ({pkg.Location}) : {ex.Message}");
            }
            var n = Interlocked.Increment(ref done);
            context.Report(Name, $"Catalogue Lenovo : {n}/{packages.Count} paquet(s) analysé(s)", 100.0 * n / Math.Max(1, packages.Count));
        }).ConfigureAwait(false);

        BiosEvaluator.Finish(context, OemVendor.Lenovo, results);
        Log.Info($"Catalogue Lenovo : {results.Count} paquet(s) applicable(s) pour {mtm}.");
        return results;
    }

    private static async Task<DriverCandidate?> ReadPackageAsync(string location, string? category, string? sha256, ScanContext context, CancellationToken ct)
    {
        var uri = TrustPolicy.NormalizeUrl(location);
        var cacheName = "lenovo-pkg-" + AppPaths.SafeName(Path.GetFileName(uri.AbsolutePath));
        var path = await Http.GetCachedFileAsync(uri.ToString(), cacheName, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (sha256 is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            Log.Warn($"Descripteur Lenovo altéré (SHA-256 invalide) : {location}");
            return null;
        }
        return ParseDescriptor(Encoding.UTF8.GetString(bytes).TrimStart('﻿'), uri, category, context);
    }

    internal static DriverCandidate? ParseDescriptor(string xml, Uri descriptorUri, string? category, ScanContext context)
    {
        var pkg = XDocument.Parse(xml).Root;
        if (pkg is null) return null;

        // PackageType : 1 = application, 2 = pilote, 3 = BIOS, 4 = firmware.
        var packageType = pkg.Element("PackageType")?.Attribute("type")?.Value;
        var isFirmware = packageType is "3" or "4";
        if (packageType != "2" && !isFirmware) return null;

        var installer = pkg.Element("Files")?.Element("Installer")?.Element("File");
        var fileName = installer?.Element("Name")?.Value?.Trim();
        if (string.IsNullOrEmpty(fileName)) return null;
        var baseUri = new Uri(descriptorUri, ".");

        // État du BIOS et firmwares d'accessoires : toujours évalués (information), même sans --include-firmware.
        if (isFirmware) RecordFirmwareInfo(pkg, packageType!, baseUri, fileName, category, context);
        if (isFirmware && !context.Settings.IncludeBiosAndFirmware) return null;

        var install = pkg.Element("Install");
        var installType = install?.Attribute("type")?.Value ?? "cmd";
        var cmdline = install?.Element("Cmdline")?.Value?.Trim();
        if (installType.Equals("INF", StringComparison.OrdinalIgnoreCase))
            cmdline = "@INF"; // signal pour le moteur : installer les INF extraits via pnputil
        if (string.IsNullOrEmpty(cmdline)) return null;

        var successCodes = (install?.Attribute("rc")?.Value ?? "0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => int.TryParse(c, out var n) ? n : 0).ToArray();

        var title = pkg.Element("Title")?.Elements("Desc").FirstOrDefault(d => d.Attribute("id")?.Value == "EN")?.Value
                    ?? pkg.Element("Title")?.Element("Desc")?.Value ?? pkg.Attribute("name")?.Value ?? fileName;
        var releaseDate = DateTime.TryParse(pkg.Element("ReleaseDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        var crc = installer?.Element("CRC")?.Value?.Trim();

        var candidate = new DriverCandidate
        {
            Id = $"lenovo:{pkg.Attribute("id")?.Value ?? fileName}",
            Source = DriverSourceKind.Lenovo,
            PackageKind = DriverPackageKind.VendorInstaller,
            Title = title.Trim(),
            Manufacturer = pkg.Element("Vendor")?.Value ?? "Lenovo",
            Category = category,
            Version = pkg.Attribute("version")?.Value,
            Date = releaseDate,
            SizeBytes = long.TryParse(installer?.Element("Size")?.Value, out var size) ? size : null,
            DownloadUrl = new Uri(baseUri, fileName).ToString(),
            FileName = fileName,
            Sha256 = crc is { Length: 64 } ? crc : null,
            ExtractCommand = pkg.Element("ExtractCommand")?.Value?.Trim() is { Length: > 0 } extract ? extract : null,
            SilentCommand = cmdline,
            SuccessExitCodes = successCodes.Length > 0 ? successCodes : new[] { 0 },
            RebootRequired = pkg.Element("Reboot")?.Attribute("type")?.Value is "1" or "3" or "4",
            SourceItemId = pkg.Attribute("id")?.Value,
            InfoUrl = pkg.Element("Files")?.Element("Readme")?.Element("File")?.Element("Name")?.Value is { } readme
                ? new Uri(baseUri, readme).ToString() : null,
            Kind = isFirmware ? CandidateKind.Firmware : CandidateKind.Update,
        };

        // Identification matérielle : _PnPID dans les dépendances, et version du pilote attendue dans DetectInstall.
        var pnpIds = pkg.Descendants("_PnPID").Select(e => e.Value.Trim())
            .Concat(pkg.Descendants("_Driver").Elements("HardwareID").Select(e => e.Value.Trim()))
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var detectVersion = pkg.Element("DetectInstall")?.Descendants("_Driver").Elements("Version").FirstOrDefault()?.Value?.TrimEnd('^').Trim();

        if (pnpIds.Count > 0)
        {
            var matches = pnpIds.SelectMany(id => OemHelpers.MatchDevices(context.Devices, id)).ToList();
            if (matches.Count == 0) return null;
            if (!OemHelpers.IsNewerForAny(detectVersion ?? candidate.Version, matches.Select(m => m.Device))) return null;
            OemHelpers.Attach(candidate, matches);
        }
        else if (!isFirmware)
        {
            candidate.Kind = CandidateKind.Optional;
        }
        return candidate;
    }

    private static string? EnglishTitle(XElement pkg) =>
        (pkg.Element("Title")?.Elements("Desc").FirstOrDefault(d => d.Attribute("id")?.Value == "EN")?.Value
         ?? pkg.Element("Title")?.Element("Desc")?.Value)?.Trim();

    private static string? ReadmeUrl(XElement pkg, Uri baseUri) =>
        pkg.Element("Files")?.Element("Readme")?.Element("File")?.Element("Name")?.Value?.Trim() is { Length: > 0 } readme
            ? new Uri(baseUri, readme).ToString() : null;

    /// <summary>
    /// BIOS (PackageType 3 avec contrôle « _Bios » ou intitulé « BIOS ») : entrée pour l'état du BIOS. Firmware de
    /// station d'accueil / Thunderbolt : listé si ses _PnPID correspondent à un périphérique présent.
    /// </summary>
    private static void RecordFirmwareInfo(XElement pkg, string packageType, Uri baseUri, string fileName, string? category, ScanContext context)
    {
        var title = EnglishTitle(pkg) ?? pkg.Attribute("name")?.Value ?? fileName;
        var severity = pkg.Element("Severity")?.Attribute("type")?.Value;
        var date = DateTime.TryParse(pkg.Element("ReleaseDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        var levels = pkg.Descendants("_Bios").Elements("Level").Select(l => l.Value.Trim().TrimEnd('^').Trim()).Where(l => l.Length > 0).ToList();
        var isBios = packageType == "3" && (levels.Count > 0 || title.Contains("BIOS", StringComparison.OrdinalIgnoreCase));
        if (isBios)
        {
            if (pkg.Attribute("name")?.Value is { Length: > 0 } name) levels.Add(name.Trim());
            context.BiosEntries.Add(new BiosCatalogEntry
            {
                Title = title,
                Version = pkg.Attribute("version")?.Value?.Trim(),
                BuildIds = levels.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Date = date,
                Importance = BiosEvaluator.ImportanceFromLenovoSeverity(severity),
                ImportanceLabel = BiosEvaluator.LenovoSeverityLabel(severity),
                DownloadUrl = new Uri(baseUri, fileName).ToString(),
                InfoUrl = ReadmeUrl(pkg, baseUri),
                CandidateId = $"lenovo:{pkg.Attribute("id")?.Value ?? fileName}",
            });
            return;
        }
        if (BiosEvaluator.AccessoryKind(title, category) is not { } kind) return;
        var pnpIds = pkg.Descendants("_PnPID").Select(e => e.Value.Trim())
            .Concat(pkg.Descendants("_Driver").Elements("HardwareID").Select(e => e.Value.Trim()))
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        BiosEvaluator.AddComponent(context, kind, title, pkg.Attribute("version")?.Value, date,
            BiosEvaluator.ImportanceFromLenovoSeverity(severity), BiosEvaluator.LenovoSeverityLabel(severity),
            new Uri(baseUri, fileName).ToString(), ReadmeUrl(pkg, baseUri), pnpIds);
    }
}
