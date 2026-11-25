using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources;

internal sealed record WindowsUpdatePayload(object Session, object Update, bool BypassWsus);

/// <summary>
/// Source n°1 : l'agent Windows Update (API COM officielle). Pilotes WHQL signés par Microsoft,
/// ciblés sur le matériel exact de la machine.
/// </summary>
public sealed partial class WindowsUpdateSource : IDriverSource
{
    public string Name => "Windows Update";
    public DriverSourceKind Kind => DriverSourceKind.WindowsUpdate;

    public bool IsEnabled(ScanContext context) => context.Settings.UseWindowsUpdate;

    public Task<IReadOnlyList<DriverCandidate>> FindAsync(ScanContext context, CancellationToken ct)
        => Task.Run(() => Search(context), ct);

    // « Intel - Net - 2.1.5.10 » ou « ASIX Net Driver Update (4.20.2.0) »
    [GeneratedRegex(@"(\d+\.\d+(\.\d+){0,2})\)?\s*$")]
    private static partial Regex TrailingVersion();

    private IReadOnlyList<DriverCandidate> Search(ScanContext context)
    {
        var results = new List<DriverCandidate>();
        context.Report(Name, "Interrogation de Windows Update (cela peut prendre une minute)...");

        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null)
        {
            Log.Warn("Agent Windows Update indisponible sur cette machine.");
            return results;
        }

        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "SpokaPilote";
        dynamic searcher = session.CreateUpdateSearcher();
        var bypass = context.Settings.WindowsUpdateBypassWsus;
        if (bypass) searcher.ServerSelection = 2; // ssWindowsUpdate : ignore WSUS (qui n'approuve souvent pas les pilotes)
        searcher.Online = true;

        dynamic searchResult;
        try
        {
            searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        }
        catch (COMException ex)
        {
            Log.Warn($"Windows Update a refusé la recherche (0x{ex.HResult:X8}). Stratégie d'entreprise ou service arrêté ?");
            return results;
        }

        int count = searchResult.Updates.Count;
        Log.Info($"Windows Update : {count} pilote(s) proposé(s).");
        for (var i = 0; i < count; i++)
        {
            dynamic update = searchResult.Updates.Item(i);
            try
            {
                string title = update.Title;
                string? hwid = SafeGet(() => (string)update.DriverHardwareID);
                string? manufacturer = SafeGet(() => (string)update.DriverManufacturer);
                string? driverClass = SafeGet(() => (string)update.DriverClass);
                DateTime? verDate = SafeGet(() => (DateTime?)(DateTime)update.DriverVerDate);
                string updateId = update.Identity.UpdateID;
                bool browseOnly = SafeGet(() => (bool)update.BrowseOnly);
                long size = SafeGet(() => Convert.ToInt64(update.MaxDownloadSize));
                int rebootBehavior = SafeGet(() => (int)update.InstallationBehavior.RebootBehavior);

                var versionMatch = TrailingVersion().Match(title);
                var candidate = new DriverCandidate
                {
                    Id = $"wu:{updateId}",
                    Source = DriverSourceKind.WindowsUpdate,
                    PackageKind = DriverPackageKind.WindowsUpdate,
                    Title = title,
                    Manufacturer = manufacturer,
                    Category = driverClass,
                    Version = versionMatch.Success ? versionMatch.Groups[1].Value : null,
                    Date = verDate,
                    SizeBytes = size > 0 ? size : null,
                    RebootRequired = rebootBehavior != 0,
                    SourceItemId = updateId,
                    MatchedHardwareId = hwid,
                    Payload = new WindowsUpdatePayload(session, update, bypass),
                    Kind = browseOnly ? CandidateKind.Optional : CandidateKind.Update,
                };

                if (!string.IsNullOrEmpty(hwid))
                {
                    foreach (var device in context.Devices)
                    {
                        var rank = HardwareIdMatcher.RankFor(device, hwid);
                        if (rank is null) continue;
                        candidate.TargetDeviceIds.Add(device.InstanceId);
                        candidate.TargetDeviceNames.Add(device.Name);
                        candidate.MatchRank = Math.Min(candidate.MatchRank, rank.Value);
                        if (device.HasProblem) candidate.Kind = CandidateKind.MissingDriver;
                    }
                }

                if (candidate.Kind == CandidateKind.Optional && !context.Options.IncludeOptional) continue;
                results.Add(candidate);
            }
            catch (Exception ex)
            {
                Log.Debug($"Mise à jour Windows ignorée : {ex.Message}");
            }
        }
        return results;
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
