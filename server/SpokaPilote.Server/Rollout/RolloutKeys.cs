using System.Globalization;
using System.Text.RegularExpressions;

namespace SpokaPilote.Server.Rollout;

/// <summary>
/// Identité d'une version de pilote sur le parc et de sa famille, calculée sans dépendre de SpokaPilote.Core (qui cible
/// Windows) mais avec les mêmes conventions : versions comparées numériquement (4 composantes), noms de sources du poste.
/// </summary>
public static partial class RolloutKeys
{
    /// <summary>Libellés de source des rapports (DriverCandidate.SourceLabel) → nom de source du poste (critère « source » des règles).</summary>
    private static readonly Dictionary<string, string> SourceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows Update"] = "WindowsUpdate",
        ["Microsoft Update Catalog"] = "MicrosoftCatalog",
        ["Dell (catalogue officiel)"] = "Dell",
        ["HP (catalogue officiel)"] = "HP",
        ["Lenovo (catalogue officiel)"] = "Lenovo",
        ["Dépôt local"] = "LocalRepository",
        ["NVIDIA (site officiel)"] = "Nvidia",
        ["Intel (site officiel)"] = "Intel",
        ["AMD (site officiel)"] = "Amd",
    };

    private static readonly HashSet<string> KindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsUpdate", "MicrosoftCatalog", "Dell", "HP", "Lenovo", "LocalRepository", "Nvidia", "Intel", "Amd",
    };

    /// <summary>Libellés de type des rapports (DriverCandidate.KindLabel) → nom de type du poste.</summary>
    private static readonly Dictionary<string, string> CandidateKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pilote manquant"] = "MissingDriver", ["MissingDriver"] = "MissingDriver",
        ["Mise à jour"] = "Update", ["Update"] = "Update",
        ["Optionnel"] = "Optional", ["Optional"] = "Optional",
        ["BIOS / Firmware"] = "Firmware", ["Firmware"] = "Firmware",
    };

    /// <summary>Nom de source du poste (WindowsUpdate, Dell...), null si le libellé est inconnu.</summary>
    public static string? SourceKind(string? label)
    {
        var s = label?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (SourceKinds.TryGetValue(s, out var kind)) return kind;
        return KindNames.TryGetValue(s, out var name) ? name : null;
    }

    /// <summary>Source normalisée stockée : nom de source du poste si connu, sinon le libellé tel quel.</summary>
    public static string SourceKey(string? label) => SourceKind(label) ?? label?.Trim() ?? "";

    public static string? CandidateKind(string? label) =>
        label is not null && CandidateKinds.TryGetValue(label.Trim(), out var kind) ? kind : null;

    /// <summary>Libellé court d'une source pour l'affichage.</summary>
    public static string SourceLabel(string source) => source switch
    {
        "WindowsUpdate" => "Windows Update",
        "MicrosoftCatalog" => "Catalogue Microsoft",
        "LocalRepository" => "Dépôt local",
        "Nvidia" => "NVIDIA",
        "Amd" => "AMD",
        _ => source,
    };

    [GeneratedRegex(@"\d+(\.\d+){0,3}", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersion();

    /// <summary>Numéros de version dans un titre (« 31.0.101.5590 », « 566.36 »), remplacés par « * » pour la famille.</summary>
    [GeneratedRegex(@"\d+(?:\.\d+)+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"\*(\s*\*)+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedStars();

    /// <summary>Même lecture que SpokaPilote.Core (DriverVersion.Parse) : première suite numérique, complétée à 4 composantes.</summary>
    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = NumericVersion().Match(value);
        if (!m.Success) return null;
        var parts = m.Value.Split('.')
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToList();
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// Une proposition est suivie (et soumise aux anneaux) si elle a un titre et une version lisible — ou, sans version lisible,
    /// une identité stable envoyée par le poste 4.0.1 (<paramref name="itemId"/> : UpdateID, identifiant du paquet...).
    /// </summary>
    public static bool IsTrackable(string? title, string? version, string? itemId = null) =>
        !string.IsNullOrWhiteSpace(title) && title.Length <= 400 &&
        (ParseVersion(version) is not null ? version!.Length <= 100 : ItemKey(itemId) is not null);

    /// <summary>Longueur maximale d'une identité de proposition sans version (comme SpokaPilote.Core, DriverIdentity).</summary>
    public const int MaxItemKeyLength = 200;

    /// <summary>Identité normalisée d'une proposition sans version lisible (« update:&lt;UpdateID&gt; »...), null si absente.</summary>
    public static string? ItemKey(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var s = new string(itemId.Trim().Where(c => !char.IsControl(c)).ToArray()).ToLowerInvariant();
        return s.Length == 0 ? null : s.Length <= MaxItemKeyLength ? s : s[..MaxItemKeyLength];
    }

    /// <summary>Constructeur de la puce dans un ID matériel : bus + VEN_xxxx ou VID_xxxx (même lecture que SpokaPilote.Core, DriverIdentity).</summary>
    [GeneratedRegex(@"^(?<bus>[A-Z0-9_]+)\\(?:[^\\]*?&)?(?<token>(?:VEN|VID)_[0-9A-Z]{3,4})(?=&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorToken();

    /// <summary>« PCI\VEN_8086 » pour « PCI\VEN_8086&amp;DEV_9A49&amp;SUBSYS_... », « HDAUDIO\VEN_10EC » pour « HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0257 », null sinon.</summary>
    public static string? HardwareVendor(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var m = VendorToken().Match(hardwareId.Trim());
        return m.Success ? $"{m.Groups["bus"].Value}\\{m.Groups["token"].Value}".ToUpperInvariant() : null;
    }

    /// <summary>Constructeur envoyé par un poste (au format « BUS\VEN_xxxx ») : revalidé, null s'il n'a pas cette forme.</summary>
    public static string? NormalizeVendor(string? vendor) => vendor is { Length: <= 40 } v ? HardwareVendor(v) : null;

    /// <summary>Classe de périphérique (« Display ») : 60 caractères au plus, sans caractère de contrôle.</summary>
    public static string? NormalizeClass(string? deviceClass)
    {
        if (string.IsNullOrWhiteSpace(deviceClass)) return null;
        var s = new string(deviceClass.Trim().Where(c => !char.IsControl(c)).ToArray());
        return s.Length is 0 or > 60 ? null : s;
    }

    /// <summary>Motifs « hardwareId » d'une règle qui visent tout le matériel de ce constructeur sur ce bus (format 1, postes 3.x).</summary>
    public static string[] VendorPatterns(string vendor)
    {
        var i = vendor.IndexOf('\\');
        var bus = vendor[..i];
        var token = vendor[(i + 1)..];
        return [$"{bus}\\*{token}&*", $"{bus}\\*{token}"];
    }

    /// <summary>Puces graphiques NVIDIA : le site NVIDIA publie « 566.36 », Windows Update et les constructeurs « 32.0.15.6636 ».</summary>
    public const string NvidiaVendor = @"PCI\VEN_10DE";

    /// <summary>
    /// Version comparée d'une source à l'autre (comme SpokaPilote.Core, DriverIdentity.CanonicalVersion) : la version numérique,
    /// sauf pour les puces NVIDIA dont la version Windows (« 32.0.15.6636 ») est ramenée au numéro commercial (« 566.36 »).
    /// </summary>
    public static Version? CanonicalVersion(string? vendor, string? version)
    {
        if (ParseVersion(version) is not { } v) return null;
        if (!string.Equals(vendor, NvidiaVendor, StringComparison.OrdinalIgnoreCase) || v.Major >= 100 || v.Build < 10 || v.Revision > 9999) return v;
        var digits = $"{v.Build}{v.Revision:D4}"[^5..];
        return new Version(int.Parse(digits[..3], CultureInfo.InvariantCulture), int.Parse(digits[3..], CultureInfo.InvariantCulture), 0, 0);
    }

    /// <summary>
    /// Versions d'une règle « même pilote, toutes sources » (postes 3.x : comparaison exacte des versions) : la version, et pour
    /// une puce NVIDIA ses deux écritures (numéro commercial « 566.36 », versions Windows « 30.0.15.6636 » à « 33.0.15.6636 »).
    /// </summary>
    public static IEnumerable<string> RuleVersions(string? vendor, string version)
    {
        if (ParseVersion(version) is not { } v) yield break;
        yield return Text(v);
        if (!string.Equals(vendor, NvidiaVendor, StringComparison.OrdinalIgnoreCase) || CanonicalVersion(vendor, version) is not { } c) yield break;
        if (c != v) { yield return Text(c); yield break; }
        if (v.Major is < 100 or > 999 || v.Minor is < 0 or > 99 || v.Build != 0 || v.Revision != 0) yield break;
        var digits = $"{v.Major:D3}{v.Minor:D2}";
        for (var wddm = 30; wddm <= 33; wddm++) yield return $"{wddm}.0.{10 + (digits[0] - '0')}.{digits[1..]}";
    }

    /// <summary>Même pilote d'une source à l'autre : même constructeur, même version, classe identique quand les deux sont connues.</summary>
    public static bool SameDriverClass(string? a, string? b) => a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Famille d'un pilote : titre dont les numéros de version (suites « 31.0.101.5590 », « 566.36 ») sont remplacés par
    /// « * » — « Intel - Display - 31.0.101.5590 » → « Intel - Display - * ». Si le reste ne contient pas au moins
    /// trois lettres (motif trop général), la famille est le titre exact.
    /// </summary>
    public static string FamilyPattern(string title)
    {
        var t = title.Trim();
        var p = RepeatedStars().Replace(VersionToken().Replace(t, "*"), "*");
        return p.Count(char.IsLetter) >= 3 ? p : t;
    }

    /// <summary>« Intel - Display - 31.0.101.5590 » (titre contenant déjà la version) ou « Intel UHD Graphics 31.0.101.5590 ».</summary>
    public static string DisplayName(string title, string version) =>
        string.IsNullOrEmpty(version) || title.Contains(version, StringComparison.OrdinalIgnoreCase) ? title : $"{title} {version}";

    /// <summary>Même version (comparaison numérique : « 10.30.00.5714 » = « 10.30.0.5714 »).</summary>
    public static bool SameVersion(string? a, string? b) => ParseVersion(a) is { } x && ParseVersion(b) is { } y && x == y;

    /// <summary>Texte de version d'une règle (4 composantes).</summary>
    public static string Text(Version v) => v.ToString(4);
}
