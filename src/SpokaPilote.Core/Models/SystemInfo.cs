namespace SpokaPilote.Core.Models;

public sealed class SystemInfo
{
    public string ComputerName { get; init; } = Environment.MachineName;
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Nom commercial (Lenovo : Win32_ComputerSystemProduct.Version, ex. « ThinkPad T14 Gen 2 »).</summary>
    public string? FriendlyModel { get; init; }
    /// <summary>SKU système (Dell : identifiant du modèle utilisé par le catalogue, ex. « 0A6B »).</summary>
    public string? SystemSku { get; init; }
    /// <summary>Produit de la carte mère (HP : identifiant de plateforme, ex. « 8549 »).</summary>
    public string? BaseBoardProduct { get; init; }
    public string? BaseBoardManufacturer { get; init; }
    public string? BiosVersion { get; init; }
    public string? SerialNumber { get; init; }
    public string OsCaption { get; init; } = "";
    public Version OsVersion { get; init; } = Environment.OSVersion.Version;
    /// <summary>Version d'affichage Windows (ex. « 24H2 »).</summary>
    public string? OsDisplayVersion { get; init; }
    /// <summary>amd64, arm64 ou x86 (nomenclature des INF).</summary>
    public string Architecture { get; init; } = "amd64";

    public int OsBuild => OsVersion.Build;
    public bool IsWindows11 => OsBuild >= 22000;

    public OemVendor Vendor => OemVendorDetector.Detect(Manufacturer);

    public string DisplayModel => string.IsNullOrWhiteSpace(FriendlyModel) ? Model : $"{FriendlyModel} ({Model})";
}

public enum OemVendor { Unknown, Dell, HP, Lenovo, Microsoft, Other }

public static class OemVendorDetector
{
    public static OemVendor Detect(string? manufacturer)
    {
        var m = (manufacturer ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return OemVendor.Unknown;
        if (m.StartsWith("dell") || m.Contains("alienware")) return OemVendor.Dell;
        if (m is "hp" or "hpe" || m.StartsWith("hewlett") || m.StartsWith("hp ") || m.StartsWith("hp inc")) return OemVendor.HP;
        if (m.StartsWith("lenovo")) return OemVendor.Lenovo;
        if (m.StartsWith("microsoft")) return OemVendor.Microsoft;
        return OemVendor.Other;
    }
}
