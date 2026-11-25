using System.Text.RegularExpressions;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Gpu;

/// <summary>Identifiants PCI des fabricants de processeurs graphiques.</summary>
public static class GpuVendorIds
{
    public const string Nvidia = "10DE";
    public const string Intel = "8086";
    public const string Amd = "1002";
}

/// <summary>Adaptateur d'affichage PCI repéré dans l'inventaire.</summary>
internal sealed record GpuAdapter(DeviceInfo Device, string VendorId, string DeviceId)
{
    /// <summary>
    /// Aucun pilote du fabricant n'est actif : périphérique en erreur, ou carte tenue par le pilote
    /// générique de Windows (« Carte graphique de base Microsoft », display.inf / basicdisplay).
    /// </summary>
    public bool NeedsDriver =>
        Device.HasProblem ||
        string.Equals(Device.InfName, "display.inf", StringComparison.OrdinalIgnoreCase) ||
        (Device.DriverProvider?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false) ||
        string.IsNullOrWhiteSpace(Device.DriverVersion);
}

internal static partial class GpuAdapters
{
    [GeneratedRegex(@"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciId();

    /// <summary>
    /// Adaptateurs d'affichage présents du fabricant <paramref name="vendorId"/> : classe « Display » ou,
    /// à défaut de pilote (classe inconnue), code de classe PCI 03 (contrôleur d'affichage).
    /// </summary>
    public static List<GpuAdapter> Find(IEnumerable<DeviceInfo> devices, string vendorId)
    {
        var result = new List<GpuAdapter>();
        foreach (var device in devices)
        {
            if (!device.IsPresent) continue;
            var isDisplay = string.Equals(device.ClassName, "Display", StringComparison.OrdinalIgnoreCase) ||
                            device.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_03", StringComparison.OrdinalIgnoreCase));
            if (!isDisplay) continue;
            foreach (var id in device.HardwareIds)
            {
                var m = PciId().Match(id);
                if (!m.Success) continue;
                if (!m.Groups[1].Value.Equals(vendorId, StringComparison.OrdinalIgnoreCase)) break;
                result.Add(new GpuAdapter(device, m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()));
                break;
            }
        }
        return result;
    }

    /// <summary>Résumé lisible des adaptateurs (nom + pilote installé) pour les candidats informatifs.</summary>
    public static string Describe(IEnumerable<GpuAdapter> adapters) =>
        string.Join(", ", adapters
            .Select(a => a.NeedsDriver || string.IsNullOrWhiteSpace(a.Device.DriverVersion)
                ? $"{a.Device.Name} (sans pilote du fabricant)"
                : $"{a.Device.Name}, pilote installé {a.Device.DriverVersion}")
            .Distinct());
}
