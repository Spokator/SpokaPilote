using System.Management;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Inventory;

/// <summary>Inventaire de tous les périphériques Plug and Play et de leur pilote actuel (WMI).</summary>
public static class DeviceInventory
{
    public static List<DeviceInfo> Collect()
    {
        var drivers = CollectSignedDrivers();
        var devices = new List<DeviceInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, PNPClass, ClassGuid, Manufacturer, HardwareID, CompatibleID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity");
        foreach (var o in searcher.Get())
        {
            try
            {
                var id = o["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;

                drivers.TryGetValue(id, out var drv);
                devices.Add(new DeviceInfo
                {
                    InstanceId = id,
                    Name = o["Name"]?.ToString() ?? drv?.Description ?? "(périphérique inconnu)",
                    ClassName = o["PNPClass"]?.ToString(),
                    ClassGuid = o["ClassGuid"]?.ToString(),
                    Manufacturer = o["Manufacturer"]?.ToString(),
                    HardwareIds = (o["HardwareID"] as string[]) ?? Array.Empty<string>(),
                    CompatibleIds = (o["CompatibleID"] as string[]) ?? Array.Empty<string>(),
                    ErrorCode = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0),
                    IsPresent = o["Present"] is not bool present || present,
                    DriverVersion = drv?.Version,
                    DriverDate = drv?.Date,
                    DriverProvider = drv?.Provider,
                    InfName = drv?.InfName,
                    IsSigned = drv?.IsSigned,
                });
            }
            catch (Exception ex)
            {
                Log.Debug($"Périphérique ignoré : {ex.Message}");
            }
            finally
            {
                o.Dispose();
            }
        }

        return devices
            .OrderBy(d => d.HasProblem ? 0 : 1)
            .ThenBy(d => d.ClassName)
            .ThenBy(d => d.Name)
            .ToList();
    }

    private sealed record SignedDriver(string? Version, DateTime? Date, string? Provider, string? InfName, bool? IsSigned, string? Description);

    private static Dictionary<string, SignedDriver> CollectSignedDrivers()
    {
        var result = new Dictionary<string, SignedDriver>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned, Description FROM Win32_PnPSignedDriver");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    var id = o["DeviceID"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    DateTime? date = null;
                    var raw = o["DriverDate"]?.ToString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        try { date = ManagementDateTimeConverter.ToDateTime(raw).Date; } catch { /* format inattendu */ }
                    }
                    result[id] = new SignedDriver(
                        o["DriverVersion"]?.ToString(),
                        date,
                        o["DriverProviderName"]?.ToString(),
                        o["InfName"]?.ToString(),
                        o["IsSigned"] as bool?,
                        o["Description"]?.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des pilotes signés impossible : {ex.Message}");
        }
        return result;
    }
}
