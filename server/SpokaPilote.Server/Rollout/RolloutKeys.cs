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

    /// <summary>Une proposition n'est suivie (et soumise aux anneaux) que si elle a un titre et une version lisible.</summary>
    public static bool IsTrackable(string? title, string? version) =>
        !string.IsNullOrWhiteSpace(title) && ParseVersion(version) is not null && title.Length <= 400 && version!.Length <= 100;

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
