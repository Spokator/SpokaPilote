using System.Text.RegularExpressions;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Matching;

public sealed record DeviceMatch(DeviceInfo Device, string MatchedId, int Rank, InfModel? Model = null);

/// <summary>
/// Classement des correspondances inspiré de Windows : un ID matériel (le plus spécifique en premier) bat toujours
/// un ID compatible. Rang = index dans HardwareIds, ou 0x1000 + index dans CompatibleIds.
/// </summary>
public static partial class HardwareIdMatcher
{
    public const int CompatibleIdOffset = 0x1000;

    public static int? RankFor(DeviceInfo device, string id)
    {
        for (var i = 0; i < device.HardwareIds.Count; i++)
            if (string.Equals(device.HardwareIds[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < device.CompatibleIds.Count; i++)
            if (string.Equals(device.CompatibleIds[i], id, StringComparison.OrdinalIgnoreCase)) return CompatibleIdOffset + i;
        return null;
    }

    /// <summary>Meilleure correspondance d'un INF pour un périphérique (en tenant compte de l'architecture et de l'OS).</summary>
    public static DeviceMatch? BestMatch(DeviceInfo device, InfInfo inf, string architecture, Version osVersion)
    {
        DeviceMatch? best = null;
        foreach (var model in inf.Models)
        {
            if (!InfParser.DecorationApplies(model.Decoration, architecture, osVersion)) continue;
            var rank = RankFor(device, model.HardwareId);
            if (rank is null) continue;
            if (best is null || rank < best.Rank) best = new DeviceMatch(device, model.HardwareId, rank.Value, model);
        }
        return best;
    }

    /// <summary>Un ID compatible générique (ex. USB\Class_03) ne doit pas déclencher l'installation d'un pilote constructeur.</summary>
    public static bool IsGenericCompatibleId(string id)
    {
        var u = id.ToUpperInvariant();
        return u.StartsWith("USB\\CLASS_") || u.StartsWith("USB\\COMPOSITE") || u.StartsWith("PCI\\CC_")
            || u.StartsWith("HID_DEVICE") || u.StartsWith("HID\\") && !u.Contains("VID_")
            || u.StartsWith("*PNP") || u.StartsWith("GENERIC") || u.StartsWith("HDAUDIO\\FUNC_01&CTLR")
            || u.StartsWith("ACPI\\PNP") || u.StartsWith("USB\\DEVCLASS");
    }

    [GeneratedRegex(@"&REV_[0-9A-F]+", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionPart();

    [GeneratedRegex(@"^(?<bus>[A-Z0-9]+)\\(?:.*?)(?<core>(VEN|VID)_[0-9A-F]{4}&(DEV|PID)_[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorDevice();

    /// <summary>
    /// Requêtes de recherche à essayer dans le Microsoft Update Catalog, de la plus spécifique à la plus large.
    /// ex. PCI\VEN_8086&amp;DEV_15F3&amp;SUBSYS_86721043&amp;REV_03 → « PCI\VEN_8086&amp;DEV_15F3&amp;SUBSYS_86721043 », « VEN_8086&amp;DEV_15F3 ».
    /// </summary>
    public static List<string> CatalogQueries(DeviceInfo device)
    {
        var queries = new List<string>();
        void Add(string q)
        {
            q = q.Trim();
            if (q.Length >= 6 && !queries.Contains(q, StringComparer.OrdinalIgnoreCase)) queries.Add(q);
        }

        foreach (var hwid in device.HardwareIds.Take(2))
        {
            var noRev = RevisionPart().Replace(hwid, "");
            Add(noRev);
        }

        var first = device.HardwareIds.FirstOrDefault() ?? device.CompatibleIds.FirstOrDefault();
        if (first is not null)
        {
            var m = VendorDevice().Match(first);
            if (m.Success) Add(m.Groups["core"].Value);
            else if (first.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)) Add(first[5..]);
        }
        return queries.Take(3).ToList();
    }
}
