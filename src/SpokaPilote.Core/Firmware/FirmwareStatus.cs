using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Firmware;

/// <summary>
/// État du BIOS par rapport au catalogue officiel du constructeur. Sérialisé en camelCase dans le rapport
/// (« upToDate », « outdated », « installedNewer », « unknown », « notInCatalog »).
/// </summary>
public enum BiosState
{
    /// <summary>Le BIOS installé est la dernière version du catalogue.</summary>
    UpToDate,
    /// <summary>Le catalogue propose une version plus récente.</summary>
    Outdated,
    /// <summary>Le BIOS installé est plus récent que le catalogue (catalogue en retard, BIOS bêta...).</summary>
    InstalledNewer,
    /// <summary>Versions impossibles à comparer de façon fiable : aucune conclusion.</summary>
    Unknown,
    /// <summary>Aucun BIOS pour ce modèle dans le catalogue du constructeur.</summary>
    NotInCatalog,
}

/// <summary>Importance déclarée par le constructeur (Dell « Urgent », HP « Critical », Lenovo « Critical »...).</summary>
public enum VendorImportance { Unknown, Optional, Recommended, Critical }

internal sealed class BiosStateJsonConverter() : JsonStringEnumConverter<BiosState>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
internal sealed class VendorImportanceJsonConverter() : JsonStringEnumConverter<VendorImportance>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>
/// BIOS du poste comparé à la dernière version publiée pour ce modèle exact. Information seulement : le BIOS n'est
/// jamais installé sans « --include-firmware » (réglage IncludeBiosAndFirmware), comme avant.
/// </summary>
public sealed class BiosStatus
{
    /// <summary>Constructeur dont le catalogue a été lu (« Dell », « HP », « Lenovo »).</summary>
    public string Vendor { get; set; } = "";
    [JsonConverter(typeof(BiosStateJsonConverter))]
    public BiosState State { get; set; } = BiosState.Unknown;
    public string? InstalledVersion { get; set; }
    public DateTime? InstalledDate { get; set; }
    /// <summary>Dernière version du catalogue pour ce modèle (null si aucune).</summary>
    public string? LatestVersion { get; set; }
    public DateTime? LatestDate { get; set; }
    /// <summary>Intitulé du paquet BIOS dans le catalogue.</summary>
    public string? Title { get; set; }
    /// <summary>Importance la plus haute parmi les versions plus récentes que le BIOS installé (sinon celle de la dernière).</summary>
    [JsonConverter(typeof(VendorImportanceJsonConverter))]
    public VendorImportance Importance { get; set; }
    /// <summary>Libellé brut du constructeur (« Urgent », « Critical », « Recommended »...).</summary>
    public string? ImportanceLabel { get; set; }
    /// <summary>Paquet officiel (HTTPS, hôte de téléchargement du constructeur).</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>Page officielle de détails ou notes de version (HTTPS, domaine du constructeur).</summary>
    public string? InfoUrl { get; set; }
    /// <summary>Explication (versions incomparables, catalogue sans BIOS...).</summary>
    public string? Reason { get; set; }

    /// <summary>Candidats « BIOS / Firmware » issus des entrées BIOS du catalogue (si --include-firmware) : évite un double constat.</summary>
    [JsonIgnore] public HashSet<string> CandidateIds { get; } = new(StringComparer.Ordinal);

    [JsonIgnore] public bool IsCritical => Importance == VendorImportance.Critical;
    [JsonIgnore] public string? Link => InfoUrl ?? DownloadUrl;

    [JsonIgnore]
    public string ImportanceText => Importance switch
    {
        VendorImportance.Critical => "critique",
        VendorImportance.Recommended => "recommandée",
        VendorImportance.Optional => "facultative",
        _ => ImportanceLabel is { Length: > 0 } raw ? raw : "importance non précisée",
    };

    /// <summary>
    /// Ligne de synthèse, ex. « BIOS : 1.12.0 installé, 1.18.0 disponible (critique) → https://... ».
    /// </summary>
    [JsonIgnore]
    public string Summary => State switch
    {
        BiosState.Outdated => $"BIOS : {InstalledVersion} installé, {LatestVersion} disponible ({ImportanceText}){(Link is null ? "" : " → " + Link)}",
        BiosState.UpToDate => $"BIOS : {InstalledVersion} installé, à jour (dernière version {Vendor} : {LatestVersion})",
        BiosState.InstalledNewer => $"BIOS : {InstalledVersion} installé, plus récent que le catalogue {Vendor} ({LatestVersion})",
        BiosState.NotInCatalog => $"BIOS : {InstalledVersion ?? "version inconnue"} installé — aucun BIOS pour ce modèle dans le catalogue {Vendor}",
        _ => $"BIOS : {InstalledVersion ?? "version inconnue"} installé — impossible de comparer avec le catalogue {Vendor}" +
             (LatestVersion is null ? "" : $" ({LatestVersion})") + (Reason is null ? "" : $" : {Reason}"),
    };

    /// <summary>Précautions avant une mise à jour de BIOS (SpokaPilote ne suspend pas BitLocker lui-même).</summary>
    public const string UpdateWarning =
        "Avant de mettre à jour le BIOS : secteur branché (batterie chargée), BitLocker suspendu " +
        "(Suspend-BitLocker -MountPoint C: -RebootCount 1, sinon la clé de récupération sera demandée au redémarrage), " +
        "ne pas éteindre pendant l'opération. SpokaPilote ne l'installe jamais sans --include-firmware.";
}

/// <summary>
/// Firmware d'accessoire (station d'accueil, contrôleur Thunderbolt) trouvé au catalogue pour un matériel présent.
/// Information seulement : jamais installé par cette fonction, la version en place n'étant pas lisible de façon fiable.
/// </summary>
public sealed class FirmwareComponentInfo
{
    /// <summary>« dock » ou « thunderbolt ».</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public DateTime? Date { get; set; }
    [JsonConverter(typeof(VendorImportanceJsonConverter))]
    public VendorImportance Importance { get; set; }
    public string? ImportanceLabel { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InfoUrl { get; set; }
    /// <summary>Périphériques présents correspondant aux identifiants matériels du catalogue.</summary>
    public List<string> Devices { get; set; } = new();
    public List<string> DeviceIds { get; set; } = new();

    [JsonIgnore] public string KindLabel => Kind == "thunderbolt" ? "Thunderbolt" : "Station d'accueil";
}

/// <summary>
/// État du BIOS et des firmwares (objet facultatif « firmware » du rapport d'analyse, absent des rapports antérieurs) :
/// <c>{ "bios": { ... }, "components": [ ... ], "notes": [ ... ] }</c>.
/// </summary>
public sealed class FirmwareStatus
{
    public BiosStatus? Bios { get; set; }
    /// <summary>Firmwares de stations d'accueil / Thunderbolt disponibles pour du matériel détecté (information).</summary>
    public List<FirmwareComponentInfo> Components { get; set; } = new();
    /// <summary>Remarques (entrées ignorées faute d'identification matérielle fiable...).</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// Ligne « BIOS : ... » pour l'affichage. Sans catalogue lu (constructeur non pris en charge, source non
    /// interrogée), la version installée est rappelée et la comparaison déclarée non faite.
    /// </summary>
    public static string Describe(FirmwareStatus? status, SystemInfo system)
    {
        if (status?.Bios is { } bios) return bios.Summary;
        var installed = system.BiosVersion ?? "version inconnue";
        return system.Vendor is OemVendor.Dell or OemVendor.HP or OemVendor.Lenovo
            ? $"BIOS : {installed} installé — non vérifié (catalogue {system.Vendor} non interrogé ou indisponible)"
            : $"BIOS : {installed} installé — non vérifié (comparaison disponible pour Dell, HP et Lenovo)";
    }
}
