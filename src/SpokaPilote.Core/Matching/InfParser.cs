using System.Globalization;
using System.Text;

namespace SpokaPilote.Core.Matching;

public sealed record InfModel(string Description, string HardwareId, string Manufacturer, string Decoration);

public sealed class InfInfo
{
    public required string Path { get; init; }
    public string? Class { get; init; }
    public string? ClassGuid { get; init; }
    public string? Provider { get; init; }
    public DateTime? DriverDate { get; init; }
    public string? DriverVersion { get; init; }
    public string? CatalogFile { get; init; }
    public List<InfModel> Models { get; } = new();
}

/// <summary>
/// Analyseur de fichiers INF (sous-ensemble utile : [Version], [Manufacturer], sections de modèles, [Strings]).
/// Gère l'UTF-16, les commentaires, les continuations de ligne et les décorations de plateforme (NTamd64.10.0...).
/// </summary>
public static class InfParser
{
    public static InfInfo Parse(string path)
    {
        var text = ReadText(path);
        return ParseText(text, path);
    }

    public static InfInfo ParseText(string text, string path = "inline.inf")
    {
        var sections = ReadSections(text);
        var strings = ReadStrings(sections);

        string? Get(string section, string key)
        {
            if (!sections.TryGetValue(section, out var lines)) return null;
            foreach (var line in lines)
            {
                var (k, v) = SplitKeyValue(line);
                if (k is not null && k.Equals(key, StringComparison.OrdinalIgnoreCase)) return Expand(v, strings);
            }
            return null;
        }

        DateTime? date = null;
        string? version = null;
        var driverVer = Get("Version", "DriverVer");
        if (driverVer is not null)
        {
            var parts = SplitValues(driverVer);
            if (parts.Count > 0 && DateTime.TryParseExact(parts[0], new[] { "MM/dd/yyyy", "M/d/yyyy", "MM/d/yyyy", "M/dd/yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                date = d;
            if (parts.Count > 1) version = parts[1];
        }

        var info = new InfInfo
        {
            Path = path,
            Class = Get("Version", "Class"),
            ClassGuid = Get("Version", "ClassGUID"),
            Provider = Get("Version", "Provider"),
            DriverDate = date,
            DriverVersion = version,
            CatalogFile = Get("Version", "CatalogFile") ?? Get("Version", "CatalogFile.NTamd64") ?? Get("Version", "CatalogFile.NT"),
        };

        if (!sections.TryGetValue("Manufacturer", out var mfgLines)) return info;

        foreach (var line in mfgLines)
        {
            var (key, value) = SplitKeyValue(line);
            var manufacturerName = key is null ? "" : Expand(key, strings);
            var values = SplitValues(key is null ? line : value);
            if (values.Count == 0) continue;
            var baseSection = values[0];
            var decorations = values.Skip(1).ToList();

            // Section non décorée + sections décorées (ex. Intel.NTamd64.10.0...16299).
            var sectionNames = new List<(string Section, string Decoration)> { (baseSection, "") };
            sectionNames.AddRange(decorations.Select(dec => ($"{baseSection}.{dec}", dec)));

            foreach (var (sectionName, decoration) in sectionNames)
            {
                if (!sections.TryGetValue(sectionName, out var modelLines)) continue;
                foreach (var modelLine in modelLines)
                {
                    var (desc, rest) = SplitKeyValue(modelLine);
                    if (desc is null) continue;
                    var ids = SplitValues(rest);
                    // ids[0] = section d'installation, puis ID matériel, puis IDs compatibles.
                    for (var i = 1; i < ids.Count; i++)
                    {
                        var hwid = Expand(ids[i], strings).Trim();
                        if (hwid.Length == 0) continue;
                        info.Models.Add(new InfModel(Expand(desc, strings), hwid, manufacturerName, decoration));
                    }
                }
            }
        }

        return info;
    }

    /// <summary>
    /// Indique si une décoration de section de modèles s'applique à l'architecture/OS cible.
    /// Format : NT[Arch][.Major[.Minor[.ProductType[.SuiteMask[.Build]]]]].
    /// Sur x64/ARM64, Windows exige une décoration d'architecture : une section non décorée ne s'applique pas.
    /// </summary>
    public static bool DecorationApplies(string decoration, string architecture, Version osVersion)
    {
        if (string.IsNullOrEmpty(decoration))
            return architecture == "x86";

        var parts = decoration.Split('.');
        var platform = parts[0].ToUpperInvariant();
        if (!platform.StartsWith("NT")) return false;
        var arch = platform[2..].ToLowerInvariant();
        if (arch.Length == 0)
        {
            if (architecture != "x86") return false;
        }
        else if (arch != architecture)
        {
            return false;
        }

        int ReadInt(int index) =>
            parts.Length > index && int.TryParse(parts[index], out var n) ? n : -1;

        var major = ReadInt(1);
        var minor = ReadInt(2);
        var build = ReadInt(5);
        if (major >= 0)
        {
            if (major > osVersion.Major) return false;
            if (major == osVersion.Major && minor > osVersion.Minor) return false;
        }
        if (build > 0 && build > osVersion.Build) return false;
        return true;
    }

    // ---------------------------------------------------------------- lecture bas niveau

    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 LE sans BOM : un octet nul sur deux.
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.Latin1.GetString(bytes);
    }

    private static Dictionary<string, List<string>> ReadSections(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        var pending = new StringBuilder();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine.TrimEnd('\r')).Trim();
            if (line.EndsWith('\\'))
            {
                pending.Append(line[..^1]).Append(' ');
                continue;
            }
            if (pending.Length > 0)
            {
                line = pending.Append(line).ToString().Trim();
                pending.Clear();
            }
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.Contains(']'))
            {
                var name = line[1..line.IndexOf(']')].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = new List<string>();
                    sections[name] = current;
                }
                continue;
            }
            current?.Add(line);
        }
        return sections;
    }

    private static Dictionary<string, string> ReadStrings(Dictionary<string, List<string>> sections)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // [Strings] d'abord, puis les variantes localisées (anglais, français) ne remplacent pas les valeurs de base.
        foreach (var name in sections.Keys.Where(k => k.Equals("Strings", StringComparison.OrdinalIgnoreCase))
                     .Concat(sections.Keys.Where(k => k.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var line in sections[name])
            {
                var (k, v) = SplitKeyValue(line);
                if (k is null) continue;
                strings.TryAdd(k.Trim(), Unquote(v.Trim()));
            }
        }
        return strings;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static (string? Key, string Value) SplitKeyValue(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == '=' && !inQuotes) return (line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return (null, line.Trim());
    }

    private static List<string> SplitValues(string value)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in value)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || result.Count > 0) result.Add(sb.ToString().Trim());
        return result.Where(v => v.Length > 0 || result.Count == 1).ToList();
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static string Expand(string value, Dictionary<string, string> strings)
    {
        value = Unquote(value.Trim());
        if (!value.Contains('%')) return value;
        var sb = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            if (value[i] == '%')
            {
                var end = value.IndexOf('%', i + 1);
                if (end > i)
                {
                    var key = value[(i + 1)..end];
                    if (key.Length == 0) sb.Append('%');
                    else sb.Append(strings.TryGetValue(key, out var s) ? s : $"%{key}%");
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(value[i]);
            i++;
        }
        return sb.ToString();
    }
}
