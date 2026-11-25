using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Sources.Oem;

internal static class OemHelpers
{
    /// <summary>
    /// Associe un ID (éventuellement générique ou avec joker « * ») aux périphériques présents.
    /// ex. « PCI\VEN_8086&amp;DEV_A0F0 » correspond à « PCI\VEN_8086&amp;DEV_A0F0&amp;SUBSYS_...&amp;REV_20 ».
    /// </summary>
    public static IEnumerable<(DeviceInfo Device, int Rank)> MatchDevices(IEnumerable<DeviceInfo> devices, string id)
    {
        id = id.Trim();
        if (id.Length < 4) yield break;
        var wildcard = id.EndsWith('*');
        var core = wildcard ? id[..^1] : id;
        foreach (var device in devices)
        {
            if (!device.IsPresent) continue;
            int? best = null;
            for (var i = 0; i < device.HardwareIds.Count; i++)
            {
                var hw = device.HardwareIds[i];
                if (hw.Equals(core, StringComparison.OrdinalIgnoreCase) ||
                    hw.StartsWith(core + (wildcard ? "" : "&"), StringComparison.OrdinalIgnoreCase) ||
                    (wildcard && hw.StartsWith(core, StringComparison.OrdinalIgnoreCase)))
                {
                    best = i;
                    break;
                }
            }
            if (best is null)
            {
                var rank = HardwareIdMatcher.RankFor(device, core);
                if (rank is not null && !HardwareIdMatcher.IsGenericCompatibleId(core)) best = rank;
            }
            if (best is not null) yield return (device, best.Value);
        }
    }

    /// <summary>Le pilote proposé est-il plus récent que celui installé sur au moins un des périphériques ciblés ?</summary>
    public static bool IsNewerForAny(string? version, IEnumerable<DeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            if (d.HasProblem) return true;
            var cmp = DriverVersion.Compare(version, d.DriverVersion);
            if (cmp is null or > 0) return true;
        }
        return false;
    }

    public static void Attach(DriverCandidate candidate, IEnumerable<(DeviceInfo Device, int Rank)> matches)
    {
        foreach (var (device, rank) in matches)
        {
            if (candidate.TargetDeviceIds.Contains(device.InstanceId, StringComparer.OrdinalIgnoreCase)) continue;
            candidate.TargetDeviceIds.Add(device.InstanceId);
            candidate.TargetDeviceNames.Add(device.Name);
            candidate.MatchRank = Math.Min(candidate.MatchRank, rank);
            if (device.HasProblem) candidate.Kind = CandidateKind.MissingDriver;
        }
    }
}
