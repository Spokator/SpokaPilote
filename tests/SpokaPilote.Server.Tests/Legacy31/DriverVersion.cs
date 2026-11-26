// Copie figée de src/SpokaPilote.Core/Models/DriverVersion.cs à l'étiquette v3.1.0 (git show v3.1.0:...), espace de noms renommé.
// Sert à prouver que la politique servie par la console 4.0 est comprise par un poste 3.1 : NE PAS MODIFIER.
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpokaPilote.Server.Tests.Legacy31.Models;

/// <summary>Comparaison tolérante des versions de pilotes (« 31.0.101.4502 », « 2.1.5.10 », « 1.2 A05 »...).</summary>
public static partial class DriverVersion
{
    [GeneratedRegex(@"\d+(\.\d+){0,3}")]
    private static partial Regex NumericVersion();

    public static Version? Parse(string? value)
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

    /// <summary>&gt; 0 si <paramref name="candidate"/> est plus récente que <paramref name="installed"/>, null si incomparable.</summary>
    public static int? Compare(string? candidate, string? installed)
    {
        var a = Parse(candidate);
        var b = Parse(installed);
        if (a is null || b is null) return null;
        return a.CompareTo(b);
    }
}
