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
