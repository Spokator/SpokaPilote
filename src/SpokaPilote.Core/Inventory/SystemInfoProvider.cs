using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Inventory;

public static class SystemInfoProvider
{
    public static SystemInfo Collect()
    {
        var cs = FirstOrDefault("SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem");
        var csp = FirstOrDefault("SELECT Version, IdentifyingNumber FROM Win32_ComputerSystemProduct");
        var bb = FirstOrDefault("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var bios = FirstOrDefault("SELECT SMBIOSBIOSVersion, SerialNumber, ReleaseDate FROM Win32_BIOS");
        var os = FirstOrDefault("SELECT Caption, Version FROM Win32_OperatingSystem");

        string? displayVersion = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
        }
        catch { /* facultatif */ }

        var manufacturer = Str(cs, "Manufacturer");
        var model = Str(cs, "Model");
        string? friendly = null;
        if (OemVendorDetector.Detect(manufacturer) == OemVendor.Lenovo)
        {
            // Sur Lenovo, Model = type-machine (MTM, ex. 20XWCTO1WW) et Version = nom commercial.
            friendly = Str(csp, "Version");
        }

        Version osVersion = Environment.OSVersion.Version;
        if (Version.TryParse(Str(os, "Version"), out var v)) osVersion = v;

        return new SystemInfo
        {
            MachineId = ReadMachineId(),
            Manufacturer = manufacturer.Trim(),
            Model = model.Trim(),
            FriendlyModel = string.IsNullOrWhiteSpace(friendly) ? null : friendly.Trim(),
            SystemSku = NullIfJunk(Str(cs, "SystemSKUNumber")),
            BaseBoardProduct = NullIfJunk(Str(bb, "Product")),
            BaseBoardManufacturer = NullIfJunk(Str(bb, "Manufacturer")),
            BiosVersion = NullIfJunk(Str(bios, "SMBIOSBIOSVersion")),
            BiosReleaseDate = ParseCimDate(Str(bios, "ReleaseDate")),
            SerialNumber = NullIfJunk(Str(bios, "SerialNumber")) ?? NullIfJunk(Str(csp, "IdentifyingNumber")),
            OsCaption = Str(os, "Caption").Trim(),
            OsVersion = osVersion,
            OsDisplayVersion = displayVersion,
            Architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "amd64",
            },
        };
    }

    /// <summary>
    /// MachineGuid de Windows (vue 64 bits du registre : la valeur n'existe pas dans la vue 32 bits), normalisé.
    /// Lisible sans droits administrateur. Null si absent ou illisible.
    /// </summary>
    public static string? ReadMachineId()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return NormalizeMachineId(key?.GetValue("MachineGuid") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Identifiant machine (MachineGuid) illisible : {ex.Message}");
            return null;
        }
    }

    /// <summary>GUID en minuscules au format « d » ; toute autre valeur (vide, GUID nul, texte) donne null.</summary>
    public static string? NormalizeMachineId(string? value) =>
        Guid.TryParse(value?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : null;

    /// <summary>Date CIM (« 20230622000000.000000+000 ») → date seule ; null si vide ou invalide.</summary>
    internal static DateTime? ParseCimDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8) return null;
        return DateTime.TryParseExact(value[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) && d.Year >= 1990 ? d : null;
    }

    private static string? NullIfJunk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var lower = v.ToLowerInvariant();
        if (lower is "default string" or "to be filled by o.e.m." or "system serial number" or "none" or "not applicable" or "0")
            return null;
        return v;
    }

    private static ManagementBaseObject? FirstOrDefault(string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var o in searcher.Get()) return o;
        }
        catch (Exception ex)
        {
            Log.Warn($"Requête WMI échouée ({query}) : {ex.Message}");
        }
        return null;
    }

    private static string Str(ManagementBaseObject? o, string prop)
    {
        try { return o?[prop]?.ToString() ?? ""; }
        catch { return ""; }
    }
}
