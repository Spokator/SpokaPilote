using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Printers;

/// <summary>
/// Découverte des imprimantes réseau : balayage des ports d'impression (9100 RAW, 631 IPP, 515 LPR) puis
/// identification par SNMP (MIB imprimante + ID IEEE 1284) et IPP (printer-make-and-model).
/// </summary>
public static partial class PrinterDiscovery
{
    private static readonly int[] PrinterPorts = { 9100, 631, 515 };

    /// <summary>Sous-réseaux IPv4 locaux (limités à /22 pour garder un balayage de quelques secondes).</summary>
    public static List<(IPAddress Network, int Prefix, string Interface)> LocalSubnets()
    {
        var withGateway = new List<(IPAddress, int, string)>();
        var others = new List<(IPAddress, int, string)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Réseaux internes de virtualisation (pas le LAN de l'entreprise). Un commutateur Hyper-V « externe »
            // porte en revanche le vrai réseau : on ne l'exclut pas sur son nom.
            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (label.Contains("wsl") || label.Contains("default switch") || label.Contains("host-only") ||
                label.Contains("vmnet") || label.Contains("docker") || label.Contains("vpn") && !label.Contains("ethernet"))
                continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address) || ua.Address.ToString().StartsWith("169.254.")) continue;
                var prefix = Math.Max(ua.PrefixLength, 22);
                (hasGateway ? withGateway : others).Add((NetworkAddress(ua.Address, prefix), prefix, nic.Name));
            }
        }
        // Les interfaces avec passerelle par défaut sont le « vrai » réseau ; les autres ne servent qu'en l'absence de passerelle.
        var result = withGateway.Count > 0 ? withGateway : others;
        return result.DistinctBy(r => (r.Item1.ToString(), r.Item2)).ToList();
    }

    [GeneratedRegex(@"^(?<a>\d{1,3}(\.\d{1,3}){3})\s*-\s*(?<b>\d{1,3}(\.\d{1,3}){3})$")]
    private static partial Regex RangeRegex();

    /// <summary>Analyse « 192.168.1.0/24 », « 10.0.0.10-10.0.0.50 » ou une adresse unique.</summary>
    public static List<IPAddress> ParseTargets(string spec)
    {
        var list = new List<IPAddress>();
        foreach (var part in spec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = RangeRegex().Match(part);
            if (m.Success)
            {
                var start = ToUInt(IPAddress.Parse(m.Groups["a"].Value));
                var end = ToUInt(IPAddress.Parse(m.Groups["b"].Value));
                for (var ip = start; ip <= end && ip - start < 4096; ip++) list.Add(FromUInt(ip));
            }
            else if (part.Contains('/'))
            {
                var pieces = part.Split('/');
                var prefix = Math.Clamp(int.Parse(pieces[1]), 20, 32);
                list.AddRange(Hosts(IPAddress.Parse(pieces[0]), prefix));
            }
            else if (IPAddress.TryParse(part, out var single))
            {
                list.Add(single);
            }
            else
            {
                try { list.AddRange(Dns.GetHostAddresses(part).Where(a => a.AddressFamily == AddressFamily.InterNetwork)); }
                catch (SocketException) { Log.Warn($"Nom d'hôte introuvable : {part}"); }
            }
        }
        return list.Distinct().ToList();
    }

    public static IEnumerable<IPAddress> Hosts(IPAddress network, int prefix)
    {
        var net = ToUInt(NetworkAddress(network, prefix));
        var count = prefix >= 31 ? 1u << (32 - prefix) : (1u << (32 - prefix)) - 2;
        var first = prefix >= 31 ? net : net + 1;
        for (uint i = 0; i < count; i++) yield return FromUInt(first + i);
    }

    public static async Task<List<DiscoveredPrinter>> DiscoverAsync(
        IEnumerable<IPAddress>? targets = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var addresses = targets?.ToList() ?? LocalSubnets().SelectMany(s => Hosts(s.Network, s.Prefix)).Distinct().ToList();
        progress?.Report($"Balayage de {addresses.Count} adresse(s) sur les ports d'impression...");

        var found = new List<DiscoveredPrinter>();
        var scanned = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 128, CancellationToken = ct }, async (ip, token) =>
        {
            var open = new List<int>();
            foreach (var port in PrinterPorts)
                if (await IsPortOpenAsync(ip, port, 600, token).ConfigureAwait(false)) open.Add(port);

            var n = Interlocked.Increment(ref scanned);
            if (n % 64 == 0) progress?.Report($"Balayage : {n}/{addresses.Count}");
            if (open.Count == 0) return;

            var printer = new DiscoveredPrinter { Address = ip.ToString() };
            printer.OpenPorts.AddRange(open);
            await IdentifyAsync(printer, token).ConfigureAwait(false);
            if (printer.IdentifiedBy.Count == 0)
            {
                // Ni SNMP ni IPP : soit une imprimante très restreinte, soit un serveur (ex. Prometheus écoute aussi sur 9100).
                // Un hôte qui expose SSH, RDP ou SMB est un ordinateur/serveur : on l'écarte.
                foreach (var serverPort in new[] { 22, 3389, 445 })
                    if (await IsPortOpenAsync(ip, serverPort, 500, token).ConfigureAwait(false)) return;
                if (open.SequenceEqual(new[] { 631 })) return; // serveur CUPS / Mac
                await IdentifyFromWebPageAsync(printer, token).ConfigureAwait(false);
            }
            lock (found) found.Add(printer);
            progress?.Report($"Trouvée : {printer.DisplayName} ({printer.Address})");
        }).ConfigureAwait(false);

        MarkInstalled(found);
        return found.OrderBy(p => ToUInt(IPAddress.Parse(p.Address))).ToList();
    }

    public static async Task IdentifyAsync(DiscoveredPrinter printer, CancellationToken ct)
    {
        var ip = IPAddress.Parse(printer.Address);

        // 1. SNMP (public, lecture seule)
        try
        {
            var snmp = await SnmpClient.GetAsync(ip, new[]
            {
                SnmpClient.SysDescr, SnmpClient.SysName, SnmpClient.SysLocation, SnmpClient.HrDeviceDescr,
                SnmpClient.PrtSerialNumber, SnmpClient.Ieee1284DeviceId,
            }, ct: ct).ConfigureAwait(false);
            if (snmp.Count > 0)
            {
                printer.IdentifiedBy.Add("SNMP");
                printer.Description = snmp.GetValueOrDefault(SnmpClient.SysDescr);
                printer.HostName ??= NullIfEmpty(snmp.GetValueOrDefault(SnmpClient.SysName));
                printer.Location ??= NullIfEmpty(snmp.GetValueOrDefault(SnmpClient.SysLocation));
                printer.SerialNumber ??= NullIfEmpty(snmp.GetValueOrDefault(SnmpClient.PrtSerialNumber));
                ApplyDeviceId(printer, snmp.GetValueOrDefault(SnmpClient.Ieee1284DeviceId));
                printer.Model ??= NullIfEmpty(snmp.GetValueOrDefault(SnmpClient.HrDeviceDescr));
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // SNMP désactivé : on continue avec IPP.
        }

        // 2. IPP (printer-make-and-model + printer-device-id)
        if (printer.OpenPorts.Contains(631))
        {
            var ipp = await IppClient.GetPrinterAttributesAsync(printer.Address, ct: ct).ConfigureAwait(false);
            if (ipp is not null)
            {
                printer.IdentifiedBy.Add("IPP");
                printer.IppUri = ipp.PrinterUri;
                ApplyDeviceId(printer, ipp.Values.GetValueOrDefault("printer-device-id"));
                var makeModel = NullIfEmpty(ipp.Values.GetValueOrDefault("printer-make-and-model"));
                if (makeModel is not null) printer.Model = makeModel;
                printer.Location ??= NullIfEmpty(ipp.Values.GetValueOrDefault("printer-location"));
                printer.HostName ??= NullIfEmpty(ipp.Values.GetValueOrDefault("printer-name"));
            }
        }

        if (printer.Manufacturer is null)
        {
            var vendor = PrinterVendorCatalog.FindVendor(printer.Model) ?? PrinterVendorCatalog.FindVendor(printer.Description);
            printer.Manufacturer = vendor?.Name;
        }
        if (printer.HostName is null)
        {
            try { printer.HostName = (await Dns.GetHostEntryAsync(printer.Address, ct).ConfigureAwait(false)).HostName; }
            catch { /* pas d'enregistrement DNS inverse */ }
        }
    }

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    /// <summary>Dernier recours : le titre de la page d'administration web contient souvent le modèle.</summary>
    private static async Task IdentifyFromWebPageAsync(DiscoveredPrinter printer, CancellationToken ct)
    {
        foreach (var scheme in new[] { "http", "https" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var html = await Http.Client.GetStringAsync($"{scheme}://{printer.Address}/", cts.Token).ConfigureAwait(false);
                var title = WebUtility.HtmlDecode(TitleRegex().Match(html).Groups["t"].Value).Trim();
                if (title.Length == 0) continue;
                printer.Description = title;
                var vendor = PrinterVendorCatalog.FindVendor(title);
                if (vendor is not null)
                {
                    printer.Manufacturer ??= vendor.Name;
                    printer.Model ??= title.Split(new[] { " - ", " | ", " : " }, StringSplitOptions.None)[0].Trim();
                    printer.IdentifiedBy.Add("Web");
                }
                return;
            }
            catch
            {
                // pas d'interface web (ou certificat auto-signé refusé) : on garde l'adresse seule
            }
        }
    }

    /// <summary>Exploite l'ID IEEE 1284 : « MFG:KYOCERA;MDL:TASKalfa 3253ci;CMD:PCLXL,PJL,KPDL; ».</summary>
    public static void ApplyDeviceId(DiscoveredPrinter printer, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        printer.DeviceId1284 = deviceId;
        var fields = Parse1284(deviceId);
        if (fields.TryGetValue("MFG", out var mfg) || fields.TryGetValue("MANUFACTURER", out mfg)) printer.Manufacturer = mfg;
        if (fields.TryGetValue("MDL", out var mdl) || fields.TryGetValue("MODEL", out mdl)) printer.Model = mdl;
        if (fields.TryGetValue("SN", out var sn) || fields.TryGetValue("SERN", out sn)) printer.SerialNumber ??= sn;
    }

    public static Dictionary<string, string> Parse1284(string deviceId)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in deviceId.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf(':');
            if (idx <= 0) continue;
            dict.TryAdd(pair[..idx].Trim(), pair[(idx + 1)..].Trim());
        }
        return dict;
    }

    /// <summary>Imprimantes déjà configurées sur ce poste (nom → adresse du port TCP/IP).</summary>
    public static List<(string Name, string Driver, string Port, string? Host)> InstalledPrinters()
    {
        var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var printers = new List<(string, string, string, string?)>();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort"))
                foreach (var o in s.Get())
                    using (o) ports[o["Name"]?.ToString() ?? ""] = o["HostAddress"]?.ToString() ?? "";

            using var ps = new ManagementObjectSearcher("SELECT Name, DriverName, PortName FROM Win32_Printer");
            foreach (var o in ps.Get())
            {
                using (o)
                {
                    var port = o["PortName"]?.ToString() ?? "";
                    printers.Add((o["Name"]?.ToString() ?? "", o["DriverName"]?.ToString() ?? "", port, ports.GetValueOrDefault(port)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des imprimantes installées impossible : {ex.Message}");
        }
        return printers;
    }

    private static void MarkInstalled(List<DiscoveredPrinter> found)
    {
        var installed = InstalledPrinters();
        foreach (var p in found)
        {
            var match = installed.FirstOrDefault(i =>
                string.Equals(i.Host, p.Address, StringComparison.OrdinalIgnoreCase) ||
                (p.HostName is not null && string.Equals(i.Host, p.HostName, StringComparison.OrdinalIgnoreCase)) ||
                i.Port.Contains(p.Address, StringComparison.OrdinalIgnoreCase));
            if (match.Name is not null) p.InstalledAs = match.Name;
        }
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return false;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IPAddress NetworkAddress(IPAddress ip, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return FromUInt(ToUInt(ip) & mask);
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
