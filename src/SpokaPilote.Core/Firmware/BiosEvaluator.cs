using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Firmware;

/// <summary>Entrée BIOS d'un catalogue constructeur applicable au modèle exact du poste.</summary>
public sealed class BiosCatalogEntry
{
    public required string Title { get; init; }
    /// <summary>Version publiée (Dell « 1.23.0 », HP « 01.12.00 » / « F.60 », Lenovo « 1.29 »).</summary>
    public string? Version { get; init; }
    /// <summary>Lenovo : identifiants de build attendus (« N2XET39W »), lus dans DetectInstall / _Bios / Level.</summary>
    public IReadOnlyList<string> BuildIds { get; init; } = Array.Empty<string>();
    public DateTime? Date { get; init; }
    public VendorImportance Importance { get; init; }
    public string? ImportanceLabel { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoUrl { get; init; }
    /// <summary>Identifiant du candidat « BIOS / Firmware » produit pour cette entrée (si --include-firmware).</summary>
    public string? CandidateId { get; init; }

    public string DisplayVersion => Version ?? BuildIds.FirstOrDefault() ?? "?";
}

/// <summary>Évaluation de l'état du BIOS (lecture seule : rien n'est installé).</summary>
public static class BiosEvaluator
{
    /// <summary>
    /// Tolérance avant de juger incohérente une version « plus récente » au catalogue alors que le BIOS installé est
    /// daté après elle (la date du catalogue est celle de publication du paquet, souvent postérieure au BIOS).
    /// </summary>
    public const int DateToleranceDays = 60;

    public static string VendorName(OemVendor vendor) => vendor switch
    {
        OemVendor.Dell => "Dell",
        OemVendor.HP => "HP",
        OemVendor.Lenovo => "Lenovo",
        _ => vendor.ToString(),
    };

    /// <summary>Importance d'après le libellé du constructeur (Dell « Urgent-... », HP « Critical », « Routine »...).</summary>
    public static VendorImportance ImportanceFromText(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return VendorImportance.Unknown;
        if (t.StartsWith("urgent") || t.StartsWith("critical") || t.StartsWith("critique")) return VendorImportance.Critical;
        if (t.StartsWith("recommended") || t.StartsWith("recommand")) return VendorImportance.Recommended;
        if (t.StartsWith("optional") || t.StartsWith("routine") || t.StartsWith("facultati")) return VendorImportance.Optional;
        return VendorImportance.Unknown;
    }

    /// <summary>Lenovo &lt;Severity type="n"/&gt; : 1 = Critical, 2 = Recommended, 3 = Optional.</summary>
    public static VendorImportance ImportanceFromLenovoSeverity(string? type) => type?.Trim() switch
    {
        "1" => VendorImportance.Critical,
        "2" => VendorImportance.Recommended,
        "3" => VendorImportance.Optional,
        _ => VendorImportance.Unknown,
    };

    public static string? LenovoSeverityLabel(string? type) => type?.Trim() switch
    {
        "1" => "Critical",
        "2" => "Recommended",
        "3" => "Optional",
        _ => null,
    };

    /// <summary>Compare le BIOS installé aux entrées BIOS du catalogue pour ce modèle.</summary>
    public static BiosStatus Evaluate(SystemInfo system, OemVendor vendor, IReadOnlyCollection<BiosCatalogEntry> entries)
    {
        var status = new BiosStatus
        {
            Vendor = VendorName(vendor),
            InstalledVersion = system.BiosVersion,
            InstalledDate = system.BiosReleaseDate,
        };
        if (entries.Count == 0)
        {
            status.State = BiosState.NotInCatalog;
            status.Reason = "aucune entrée BIOS pour ce modèle dans le catalogue officiel";
            return status;
        }
        foreach (var e in entries.Where(e => e.CandidateId is not null)) status.CandidateIds.Add(e.CandidateId!);

        var matched = entries
            .Select(e => BiosVersionComparer.TryMatch(vendor, system.BiosVersion, e.Version, e.BuildIds, e.Title, out var mine, out var theirs)
                ? (Entry: e, Mine: mine, Theirs: theirs)
                : (Entry: e, Mine: (BiosVersionKey?)null, Theirs: (BiosVersionKey?)null))
            .ToList();
        var comparable = matched.Where(m => m.Mine is not null).ToList();

        if (comparable.Count == 0)
        {
            var newest = entries.OrderByDescending(e => e.Date ?? DateTime.MinValue).First();
            Fill(status, newest, newest.Importance, newest.ImportanceLabel);
            status.State = BiosState.Unknown;
            status.Reason = system.BiosVersion is null
                ? "version du BIOS installé illisible"
                : $"formats de version différents (installé « {system.BiosVersion} », catalogue « {newest.DisplayVersion} »)";
            return status;
        }

        var latest = comparable.OrderByDescending(m => m.Theirs!).ThenByDescending(m => m.Entry.Date ?? DateTime.MinValue).First();
        var installedKey = latest.Mine!;
        var cmp = latest.Theirs!.CompareTo(installedKey);

        // Importance : la plus haute parmi les versions plus récentes que celle installée (une version intermédiaire
        // « Urgent » reste urgente même si la dernière n'est que « Recommended »).
        var newer = comparable.Where(m => m.Theirs!.CompareTo(installedKey) > 0).Select(m => m.Entry).ToList();
        var mostImportant = (newer.Count > 0 ? newer : new List<BiosCatalogEntry> { latest.Entry }).OrderByDescending(e => e.Importance).First();
        Fill(status, latest.Entry, mostImportant.Importance, mostImportant.ImportanceLabel);

        if (cmp > 0 && system.BiosReleaseDate is { } installedDate && latest.Entry.Date is { } catalogDate &&
            installedDate.Date > catalogDate.Date.AddDays(DateToleranceDays))
        {
            status.State = BiosState.Unknown;
            status.Reason = $"versions et dates incohérentes (BIOS installé daté du {installedDate:dd/MM/yyyy}, version du catalogue du {catalogDate:dd/MM/yyyy})";
            return status;
        }
        status.State = cmp > 0 ? BiosState.Outdated : cmp == 0 ? BiosState.UpToDate : BiosState.InstalledNewer;
        return status;
    }

    private static void Fill(BiosStatus status, BiosCatalogEntry entry, VendorImportance importance, string? importanceLabel)
    {
        status.LatestVersion = entry.DisplayVersion;
        status.LatestDate = entry.Date;
        status.Title = entry.Title;
        status.Importance = importance;
        status.ImportanceLabel = importanceLabel;
        status.DownloadUrl = TrustPolicy.OfficialLink(entry.DownloadUrl);
        status.InfoUrl = TrustPolicy.OfficialLink(entry.InfoUrl);
    }

    /// <summary>
    /// Fin de lecture d'un catalogue constructeur : état du BIOS publié dans le contexte, et retrait du candidat
    /// « BIOS / Firmware » quand le BIOS installé est déjà à jour (ou plus récent). Si la comparaison est impossible,
    /// le candidat reste proposé comme avant (jamais coché ni installé sans --include-firmware).
    /// </summary>
    public static BiosStatus Finish(ScanContext context, OemVendor vendor, List<DriverCandidate> results)
    {
        var status = Evaluate(context.System, vendor, context.BiosEntries.ToList());
        context.Bios = status;
        if (status.State is BiosState.UpToDate or BiosState.InstalledNewer)
            results.RemoveAll(c => c.Kind == CandidateKind.Firmware && status.CandidateIds.Contains(c.Id));
        var skipped = context.SkippedFirmwareComponents;
        if (skipped > 0)
            context.FirmwareNotes.Add($"{skipped} firmware(s) de station d'accueil / Thunderbolt du catalogue {VendorName(vendor)} sans identifiant " +
                                      "matériel : non listé(s), la présence du matériel ne peut pas être vérifiée de façon fiable.");
        Log.Info(status.Summary);
        return status;
    }

    /// <summary>« dock », « thunderbolt » ou null d'après l'intitulé ou la catégorie d'une entrée de catalogue.</summary>
    public static string? AccessoryKind(params string?[] texts)
    {
        var all = string.Join(" ", texts.Where(t => t is not null)).ToLowerInvariant();
        if (all.Contains("dock")) return "dock";
        if (all.Contains("thunderbolt")) return "thunderbolt";
        return null;
    }

    /// <summary>
    /// Firmware de station d'accueil / Thunderbolt : listé seulement si les identifiants matériels du catalogue
    /// correspondent à un périphérique présent ; sans identifiant, l'entrée est ignorée (pas de devinette sur le nom).
    /// </summary>
    internal static void AddComponent(ScanContext context, string kind, string title, string? version, DateTime? date,
        VendorImportance importance, string? importanceLabel, string? downloadUrl, string? infoUrl, IReadOnlyList<string> hardwareIds)
    {
        if (hardwareIds.Count == 0)
        {
            Interlocked.Increment(ref context.SkippedFirmwareComponentsField);
            return;
        }
        var matches = hardwareIds.SelectMany(id => Sources.Oem.OemHelpers.MatchDevices(context.Devices, id))
            .Select(m => m.Device).DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        if (matches.Count == 0) return;
        context.FirmwareComponents.Add(new FirmwareComponentInfo
        {
            Kind = kind,
            Title = title,
            Version = version,
            Date = date,
            Importance = importance,
            ImportanceLabel = importanceLabel,
            DownloadUrl = TrustPolicy.OfficialLink(downloadUrl),
            InfoUrl = TrustPolicy.OfficialLink(infoUrl),
            Devices = matches.Select(d => d.Name).Distinct().ToList(),
            DeviceIds = matches.Select(d => d.InstanceId).ToList(),
        });
    }
}
