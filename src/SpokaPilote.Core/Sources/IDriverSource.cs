using System.Collections.Concurrent;
using SpokaPilote.Core.Firmware;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources;

public sealed record ScanProgress(string Stage, string Message, double? Percent = null);

public sealed class ScanOptions
{
    /// <summary>Interroge aussi le catalogue Microsoft pour les périphériques qui fonctionnent déjà (plus lent).</summary>
    public bool DeepScan { get; init; }
    public bool IncludeOptional { get; init; } = true;
    public HashSet<DriverSourceKind>? OnlySources { get; init; }
}

public sealed class ScanContext
{
    public required SystemInfo System { get; init; }
    public required IReadOnlyList<DeviceInfo> Devices { get; init; }
    public required Settings Settings { get; init; }
    public required ScanOptions Options { get; init; }
    public IProgress<ScanProgress>? Progress { get; init; }

    /// <summary>Avertissements non bloquants remontés par les sources (ex. catalogue saturé, résultats partiels).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Warnings { get; } = new();

    /// <summary>État du BIOS évalué par la source du constructeur (Dell, HP, Lenovo) ; null si aucune n'a été interrogée.</summary>
    public BiosStatus? Bios { get; set; }
    /// <summary>Entrées BIOS du catalogue applicables au modèle (alimentées par la source, y compris sans --include-firmware).</summary>
    public ConcurrentBag<BiosCatalogEntry> BiosEntries { get; } = new();
    /// <summary>Firmwares de stations d'accueil / Thunderbolt pour du matériel présent (information seulement).</summary>
    public ConcurrentBag<FirmwareComponentInfo> FirmwareComponents { get; } = new();
    public ConcurrentBag<string> FirmwareNotes { get; } = new();
    internal int SkippedFirmwareComponentsField;
    /// <summary>Entrées station d'accueil / Thunderbolt ignorées faute d'identifiant matériel dans le catalogue.</summary>
    public int SkippedFirmwareComponents => Volatile.Read(ref SkippedFirmwareComponentsField);

    /// <summary>État du BIOS et des firmwares à publier dans le résultat (null si aucune source constructeur n'a répondu).</summary>
    public FirmwareStatus? BuildFirmwareStatus() =>
        Bios is null && FirmwareComponents.IsEmpty ? null : new FirmwareStatus
        {
            Bios = Bios,
            Components = FirmwareComponents.OrderBy(c => c.Kind).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Notes = FirmwareNotes.Distinct().ToList(),
        };

    public void Report(string stage, string message, double? percent = null)
        => Progress?.Report(new ScanProgress(stage, message, percent));
}

public interface IDriverSource
{
    string Name { get; }
    DriverSourceKind Kind { get; }
    bool IsEnabled(ScanContext context);
    Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct);
}
