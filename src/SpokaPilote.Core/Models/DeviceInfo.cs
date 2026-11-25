namespace SpokaPilote.Core.Models;

public enum DeviceHealth { Ok, MissingDriver, Error, Disabled }

public sealed class DeviceInfo
{
    public required string InstanceId { get; init; }
    public string Name { get; init; } = "";
    public string? ClassName { get; init; }
    public string? ClassGuid { get; init; }
    public string? Manufacturer { get; init; }
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();
    public int ErrorCode { get; init; }
    public bool IsPresent { get; init; } = true;

    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public string? DriverProvider { get; init; }
    public string? InfName { get; init; }
    public bool? IsSigned { get; init; }

    public string Enumerator => InstanceId.Split('\\')[0].ToUpperInvariant();

    public DeviceHealth Health => ErrorCode switch
    {
        0 => DeviceHealth.Ok,
        28 or 1 or 18 => DeviceHealth.MissingDriver,
        22 => DeviceHealth.Disabled,
        _ => DeviceHealth.Error,
    };

    public bool HasProblem => Health is DeviceHealth.MissingDriver or DeviceHealth.Error;

    private static readonly HashSet<string> HardwareEnumerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "PCI", "USB", "HDAUDIO", "ACPI", "HID", "BTH", "BTHENUM", "INTELAUDIO", "SD", "USBSTOR", "SCSI",
        "DISPLAY", "MONITOR", "UEFI", "ISAPNP", "IDE", "NVME", "THUNDERBOLT", "USB4", "SWC", "WPDBUSENUMROOT",
    };

    /// <summary>Périphérique physique pour lequel un pilote constructeur a du sens (exclut les périphériques logiciels/racine).</summary>
    public bool IsHardware => HardwareEnumerators.Contains(Enumerator);

    public string StatusText => ErrorCode switch
    {
        0 => "Fonctionne correctement",
        1 => "Mal configuré (pilote absent ou incorrect)",
        3 => "Pilote endommagé ou mémoire insuffisante",
        10 => "Ne peut pas démarrer",
        12 => "Ressources insuffisantes",
        14 => "Redémarrage nécessaire",
        18 => "Pilote à réinstaller",
        19 => "Configuration de registre incomplète",
        21 => "Suppression en cours",
        22 => "Désactivé",
        24 => "Absent ou mal installé",
        28 => "Aucun pilote installé",
        29 => "Désactivé par le firmware",
        31 => "Le pilote n'a pas pu être chargé",
        32 => "Service du pilote désactivé",
        37 => "Échec d'initialisation du pilote",
        39 => "Pilote corrompu ou manquant",
        43 => "Arrêté suite à un problème signalé",
        45 => "Non connecté",
        48 => "Bloqué (incompatibilité connue)",
        52 => "Signature du pilote invalide",
        _ => $"Erreur {ErrorCode}",
    };

    /// <summary>Panne matérielle (USB qui ne répond pas...) : aucun pilote ne peut la corriger.</summary>
    public bool IsHardwareFault
    {
        get
        {
            var id = HardwareIds.FirstOrDefault() ?? "";
            return HasProblem && (id.Contains("DESCRIPTOR_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                  id.Contains("RESET_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                  id.Contains("SET_ADDRESS_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                  id.Contains("DESCRIPTOR_VALIDATION_FAILURE", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Diagnostic lorsque le problème ne vient manifestement pas d'un pilote manquant.</summary>
    public string? Hint
    {
        get
        {
            var id = HardwareIds.FirstOrDefault() ?? "";
            if (id.Contains("DEVICE_DESCRIPTOR_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("RESET_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("SET_ADDRESS_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CONFIGURATION_DESCRIPTOR_VALIDATION_FAILURE", StringComparison.OrdinalIgnoreCase))
                return "Problème matériel USB (câble, port, alimentation ou périphérique défectueux) : aucun pilote ne peut le corriger. Débranchez/rebranchez sur un autre port.";
            return ErrorCode switch
            {
                22 => "Périphérique désactivé manuellement : réactivez-le dans le Gestionnaire de périphériques.",
                29 => "Désactivé dans le BIOS/UEFI.",
                45 => "Périphérique débranché.",
                12 => "Conflit de ressources : souvent résolu par une mise à jour du BIOS/chipset.",
                52 => "Signature invalide : le pilote installé n'est pas approuvé par Windows ; remplacez-le par un pilote signé.",
                _ => null,
            };
        }
    }

    public override string ToString() => $"{Name} [{InstanceId}]";
}
