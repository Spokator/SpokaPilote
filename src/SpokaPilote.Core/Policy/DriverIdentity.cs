using System.Text.RegularExpressions;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Policy;

/// <summary>
/// Identité d'un pilote proposé, au-delà de « source + titre + version » (diffusion par anneaux, console 4.0.1) :
/// <list type="bullet">
/// <item><see cref="ItemKey"/> : identité stable d'une proposition SANS numéro de version lisible (UpdateID de Windows Update
///   ou du catalogue Microsoft — le même identifiant pour les deux —, identifiant du paquet constructeur, à défaut date et
///   fournisseur du pilote) : elle permet à la console de la retenir, de la suspendre et de compter ses échecs ;</item>
/// <item><see cref="HardwareVendor"/> et la classe du périphérique visé : le même pilote proposé par plusieurs sources
///   (Dell, catalogue Microsoft, Windows Update...) sous des titres différents vise le même matériel (constructeur de la
///   puce dans l'ID matériel, classe Windows du périphérique) avec le même numéro de version — une suspension le suit
///   d'une source à l'autre.</item>
/// </list>
/// Mêmes conventions que la console (SpokaPilote.Server, RolloutKeys) : à modifier ensemble.
/// </summary>
public static partial class DriverIdentity
{
    /// <summary>Longueur maximale d'une identité (au-delà : tronquée, comme la console).</summary>
    public const int MaxItemKeyLength = 200;

    /// <summary>Constructeur de la puce dans un ID matériel : bus + VEN_xxxx ou VID_xxxx (PCI, HDAUDIO, USB, ACPI...).</summary>
    [GeneratedRegex(@"^(?<bus>[A-Z0-9_]+)\\(?:[^\\]*?&)?(?<token>(?:VEN|VID)_[0-9A-Z]{3,4})(?=&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorToken();

    /// <summary>« PCI\VEN_8086 » pour « PCI\VEN_8086&amp;DEV_9A49&amp;SUBSYS_... », « HDAUDIO\VEN_10EC » pour « HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0257 », null sinon.</summary>
    public static string? HardwareVendor(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var m = VendorToken().Match(hardwareId.Trim());
        return m.Success ? $"{m.Groups["bus"].Value}\\{m.Groups["token"].Value}".ToUpperInvariant() : null;
    }

    /// <summary>Motifs « hardwareId » d'une règle qui visent tout le matériel de ce constructeur sur ce bus.</summary>
    public static string[] VendorPatterns(string vendor)
    {
        var i = vendor.IndexOf('\\');
        var bus = vendor[..i];
        var token = vendor[(i + 1)..];
        return [$"{bus}\\*{token}&*", $"{bus}\\*{token}"];
    }

    /// <summary>
    /// Renseigne <see cref="DriverCandidate.HardwareVendor"/> et <see cref="DriverCandidate.DeviceClass"/> (propositions et
    /// leurs alternatives) d'après le premier périphérique visé dont l'ID matériel désigne un constructeur, à défaut l'ID
    /// matériel qui a permis l'association. Ne remplace pas une valeur déjà connue.
    /// </summary>
    public static void Annotate(IEnumerable<DriverCandidate> candidates, IReadOnlyList<DeviceInfo> devices)
    {
        var byId = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) byId.TryAdd(d.InstanceId, d);
        foreach (var c in candidates)
        {
            Annotate(c, byId, null);
            foreach (var alt in c.Alternatives) Annotate(alt, byId, c);
        }
    }

    private static void Annotate(DriverCandidate c, Dictionary<string, DeviceInfo> devices, DriverCandidate? parent)
    {
        if (c.HardwareVendor is not null) return;
        var targets = c.TargetDeviceIds.Count > 0 ? c.TargetDeviceIds : parent?.TargetDeviceIds ?? new List<string>();
        foreach (var id in targets)
        {
            if (!devices.TryGetValue(id, out var d)) continue;
            var vendor = d.HardwareIds.Concat(d.CompatibleIds).Select(HardwareVendor).FirstOrDefault(v => v is not null);
            if (vendor is null) continue;
            c.HardwareVendor = vendor;
            c.DeviceClass = string.IsNullOrWhiteSpace(d.ClassName) ? null : d.ClassName.Trim();
            return;
        }
        c.HardwareVendor = HardwareVendor(c.MatchedHardwareId) ?? parent?.HardwareVendor;
        if (c.HardwareVendor is not null && parent is not null) c.DeviceClass ??= parent.DeviceClass;
    }

    /// <summary>
    /// Même pilote d'une source à l'autre : même constructeur du matériel, même version numérique, classe du périphérique
    /// identique (sans tenir compte de la casse) quand les deux sont connues.
    /// </summary>
    public static bool SameDriver(DriverCandidate a, DriverCandidate b)
    {
        if (a.HardwareVendor is null || b.HardwareVendor is null || !string.Equals(a.HardwareVendor, b.HardwareVendor, StringComparison.OrdinalIgnoreCase))
            return false;
        if (CanonicalVersion(a.HardwareVendor, a.Version) is not { } va || CanonicalVersion(b.HardwareVendor, b.Version) is not { } vb || va != vb) return false;
        return a.DeviceClass is null || b.DeviceClass is null || string.Equals(a.DeviceClass, b.DeviceClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Puces graphiques NVIDIA : le site NVIDIA publie « 566.36 », Windows Update et les constructeurs « 32.0.15.6636 ».</summary>
    public const string NvidiaVendor = @"PCI\VEN_10DE";

    /// <summary>
    /// Version comparée d'une source à l'autre : la version numérique, sauf pour les puces NVIDIA dont la version Windows
    /// (« 32.0.15.6636 ») est ramenée au numéro commercial publié par NVIDIA (« 566.36 » : les 5 derniers chiffres).
    /// </summary>
    public static Version? CanonicalVersion(string? vendor, string? version)
    {
        if (DriverVersion.Parse(version) is not { } v) return null;
        if (!string.Equals(vendor, NvidiaVendor, StringComparison.OrdinalIgnoreCase) || v.Major >= 100 || v.Build < 10 || v.Revision > 9999) return v;
        var digits = $"{v.Build}{v.Revision:D4}";
        digits = digits[^5..];
        return new Version(int.Parse(digits[..3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(digits[3..], System.Globalization.CultureInfo.InvariantCulture), 0, 0);
    }

    /// <summary>
    /// Identité d'une proposition sans numéro de version lisible (null si sa version est lisible : la version l'identifie).
    /// Windows Update et catalogue Microsoft : « update:&lt;UpdateID&gt; » (même identifiant pour les deux) ; autres sources :
    /// « dell:&lt;releaseID&gt; »... ; à défaut d'identifiant : date et fournisseur du pilote (« windowsupdate:date=2024-05-17;fournisseur=hp »).
    /// </summary>
    public static string? ItemKey(DriverCandidate c)
    {
        if (DriverVersion.Parse(c.Version) is not null) return null;
        string key;
        if (!string.IsNullOrWhiteSpace(c.SourceItemId))
        {
            var ns = c.Source is DriverSourceKind.WindowsUpdate or DriverSourceKind.MicrosoftCatalog ? "update" : c.Source.ToString().ToLowerInvariant();
            key = $"{ns}:{c.SourceItemId.Trim().ToLowerInvariant()}";
        }
        else
            key = $"{c.Source.ToString().ToLowerInvariant()}:date={c.Date:yyyy-MM-dd};fournisseur={c.Manufacturer?.Trim().ToLowerInvariant()}";
        key = new string(key.Where(ch => !char.IsControl(ch)).ToArray());
        return key.Length <= MaxItemKeyLength ? key : key[..MaxItemKeyLength];
    }

    /// <summary>La proposition peut-elle être suivie par la console (version lisible, ou identité sans version) ?</summary>
    public static bool IsTrackable(DriverCandidate c) =>
        !c.IsInformational && (DriverVersion.Parse(c.Version) is not null || ItemKey(c) is not null);
}
