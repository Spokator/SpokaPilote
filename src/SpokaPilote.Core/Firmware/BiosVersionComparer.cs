using System.Globalization;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Firmware;

/// <summary>
/// Version de BIOS normalisée. Deux clés ne se comparent que si elles suivent le même schéma et la même famille :
/// <list type="bullet">
/// <item><c>dotted</c> : « 1.23.0 » (Dell), « 01.12.00 » (HP), « 1.29 » (Lenovo, entre parenthèses dans le SMBIOS) ;</item>
/// <item><c>letter</c> : « A12 » (anciens BIOS Dell), « F.60 » (HP grand public) — famille = la lettre ;</item>
/// <item><c>lenovo-build</c> : « N2XET39W » — famille = « N2XET », numéro = 39.</item>
/// </list>
/// </summary>
public sealed record BiosVersionKey(string Scheme, string Family, IReadOnlyList<int> Parts) : IComparable<BiosVersionKey>
{
    public const string Dotted = "dotted";
    public const string Letter = "letter";
    public const string LenovoBuild = "lenovo-build";

    public bool IsComparableWith(BiosVersionKey other) =>
        Scheme == other.Scheme && string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase);

    /// <summary>Comparaison numérique partie par partie (« 1.9.0 » &lt; « 1.23.0 », « 01.15.00 » = « 1.15.0 »).</summary>
    public int CompareTo(BiosVersionKey? other)
    {
        if (other is null) return 1;
        var n = Math.Max(Parts.Count, other.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var a = i < Parts.Count ? Parts[i] : 0;
            var b = i < other.Parts.Count ? other.Parts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    public override string ToString() => $"{Scheme}:{Family}:{string.Join('.', Parts)}";
}

/// <summary>
/// Comparaison prudente des versions de BIOS selon le schéma de chaque constructeur. En cas de doute (formats
/// différents, famille de BIOS différente), aucune conclusion n'est tirée : <see cref="TryCompare"/> renvoie false.
/// </summary>
public static partial class BiosVersionComparer
{
    [GeneratedRegex(@"^\d{1,4}(\.\d{1,4}){1,3}$")]
    private static partial Regex DottedRx();

    /// <summary>Révision de SoftPaq HP (« 01.15.00 Rev.A », « 01.15.00 A 1 ») : même BIOS, autre emballage.</summary>
    [GeneratedRegex(@"\s+(?:Rev\.?\s*[A-Z0-9]{1,3}|[A-Z]\s+\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HpRevisionRx();

    [GeneratedRegex(@"^(?<l>[A-Z])\.?(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex LetterRx();

    /// <summary>Identifiant de build Lenovo : 4 caractères de famille + « T » + 2 chiffres + 1 lettre (N2XET39W, M3AKT56A).</summary>
    [GeneratedRegex(@"^(?<f>[A-Z0-9]{4}T)(?<n>\d{2})[A-Z]$", RegexOptions.IgnoreCase)]
    private static partial Regex LenovoBuildRx();

    /// <summary>SMBIOS Lenovo : « N2XET39W (1.29 ) ».</summary>
    [GeneratedRegex(@"^(?<b>[A-Z0-9]{8})\s*\(\s*(?<v>\d+\.\d+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex LenovoSmbiosRx();

    /// <summary>SMBIOS HP : « T37 Ver. 01.15.00 », « 68CDD Ver. F.60 ».</summary>
    [GeneratedRegex(@"^(?<f>\S+)\s+Ver\.?\s*(?<v>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HpSmbiosRx();

    /// <summary>Famille HP dans l'intitulé du SoftPaq : « System BIOS (T37) ».</summary>
    [GeneratedRegex(@"\((?<f>[A-Z][A-Z0-9]{2,4})\)")]
    private static partial Regex HpTitleFamilyRx();

    private static BiosVersionKey? ParseSimple(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = HpRevisionRx().Replace(value.Trim(), "");
        if (DottedRx().IsMatch(v))
            return new BiosVersionKey(BiosVersionKey.Dotted, "", v.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray());
        if (LetterRx().Match(v) is { Success: true } m)
            return new BiosVersionKey(BiosVersionKey.Letter, m.Groups["l"].Value.ToUpperInvariant(), new[] { int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) });
        return null;
    }

    private static BiosVersionKey? ParseLenovoBuild(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = LenovoBuildRx().Match(value.Trim().TrimEnd('^').Trim());
        return m.Success
            ? new BiosVersionKey(BiosVersionKey.LenovoBuild, m.Groups["f"].Value.ToUpperInvariant(), new[] { int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) })
            : null;
    }

    /// <summary>Famille HP du BIOS installé (« T37 » dans « T37 Ver. 01.15.00 »), null si absente.</summary>
    public static string? HpInstalledFamily(string? smbios) =>
        smbios is not null && HpSmbiosRx().Match(smbios.Trim()) is { Success: true } m ? m.Groups["f"].Value.ToUpperInvariant() : null;

    /// <summary>Famille HP indiquée dans l'intitulé d'un SoftPaq BIOS (« (T37) »), null si absente.</summary>
    public static string? HpTitleFamily(string? title) =>
        title is not null && HpTitleFamilyRx().Match(title) is { Success: true } m ? m.Groups["f"].Value.ToUpperInvariant() : null;

    /// <summary>Clés du BIOS installé (SMBIOSBIOSVersion), par ordre de préférence.</summary>
    public static IReadOnlyList<BiosVersionKey> ParseInstalled(OemVendor vendor, string? smbios)
    {
        if (string.IsNullOrWhiteSpace(smbios)) return Array.Empty<BiosVersionKey>();
        var value = smbios.Trim();
        var keys = new List<BiosVersionKey>();
        switch (vendor)
        {
            case OemVendor.Lenovo:
                if (LenovoSmbiosRx().Match(value) is { Success: true } lm)
                {
                    if (ParseLenovoBuild(lm.Groups["b"].Value) is { } build) keys.Add(build);
                    if (ParseSimple(lm.Groups["v"].Value) is { } dotted) keys.Add(dotted);
                }
                else if (ParseLenovoBuild(value) is { } build) keys.Add(build);
                break;
            case OemVendor.HP:
                var hm = HpSmbiosRx().Match(value);
                if (ParseSimple(hm.Success ? hm.Groups["v"].Value : value) is { } hp) keys.Add(hp);
                break;
            default:
                if (ParseSimple(value) is { } simple) keys.Add(simple);
                break;
        }
        return keys;
    }

    /// <summary>Clés d'une entrée de catalogue : identifiants de build Lenovo d'abord, puis la version.</summary>
    public static IReadOnlyList<BiosVersionKey> ParseCatalog(string? version, IEnumerable<string>? buildIds = null)
    {
        var keys = new List<BiosVersionKey>();
        void Add(BiosVersionKey key)
        {
            if (!keys.Any(k => k.ToString() == key.ToString())) keys.Add(key);
        }
        foreach (var id in buildIds ?? Enumerable.Empty<string>())
            if (ParseLenovoBuild(id) is { } build) Add(build);
        if (ParseLenovoBuild(version) is { } versionBuild) Add(versionBuild);
        else if (ParseSimple(version) is { } simple) Add(simple);
        return keys;
    }

    /// <summary>
    /// Associe la version installée et une entrée du catalogue. Renvoie false si la comparaison n'est pas fiable :
    /// format inconnu, schémas différents, famille de BIOS différente (identifiant de build Lenovo, famille HP).
    /// </summary>
    public static bool TryMatch(OemVendor vendor, string? installed, string? catalogVersion, IEnumerable<string>? buildIds,
        string? catalogTitle, out BiosVersionKey installedKey, out BiosVersionKey catalogKey)
    {
        installedKey = catalogKey = null!;
        var mine = ParseInstalled(vendor, installed);
        var theirs = ParseCatalog(catalogVersion, buildIds);
        if (mine.Count == 0 || theirs.Count == 0) return false;

        if (vendor == OemVendor.HP)
        {
            // La plateforme HP partage parfois une référence entre familles de BIOS : on ne compare que la même famille.
            var a = HpInstalledFamily(installed);
            var b = HpTitleFamily(catalogTitle);
            if (a is not null && b is not null && a != b) return false;
        }

        // Lenovo : si les deux côtés ont un identifiant de build, il fait foi (même famille obligatoire).
        var myBuild = mine.FirstOrDefault(k => k.Scheme == BiosVersionKey.LenovoBuild);
        var theirBuilds = theirs.Where(k => k.Scheme == BiosVersionKey.LenovoBuild).ToList();
        if (myBuild is not null && theirBuilds.Count > 0)
        {
            var same = theirBuilds.Where(myBuild.IsComparableWith).OrderByDescending(k => k).FirstOrDefault();
            if (same is null) return false;
            (installedKey, catalogKey) = (myBuild, same);
            return true;
        }

        foreach (var m in mine)
        {
            var match = theirs.FirstOrDefault(m.IsComparableWith);
            if (match is null) continue;
            (installedKey, catalogKey) = (m, match);
            return true;
        }
        return false;
    }

    /// <summary>&gt; 0 si le catalogue est plus récent, 0 si identique, &lt; 0 si le BIOS installé est plus récent ; null si incomparable.</summary>
    public static int? Compare(OemVendor vendor, string? installed, string? catalogVersion, IEnumerable<string>? buildIds = null, string? catalogTitle = null) =>
        TryMatch(vendor, installed, catalogVersion, buildIds, catalogTitle, out var a, out var b) ? b.CompareTo(a) : null;
}
