using System.Management;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Printers;

public enum InstalledPortKind
{
    /// <summary>Port TCP/IP standard (RAW ou LPR).</summary>
    TcpIp,
    /// <summary>Imprimante IPP (port URL http/https/ipp).</summary>
    Ipp,
    /// <summary>Port WSD (découverte automatique) : adresse non exportable.</summary>
    Wsd,
    /// <summary>Connexion partagée d'un serveur d'impression (\\serveur\file).</summary>
    Connection,
    /// <summary>Imprimante locale ou virtuelle (PDF, XPS, OneNote, Fax, USB, LPT, fichier...).</summary>
    Local,
}

public sealed record InstalledPort(string Name, string Host, string Protocol, int PortNumber, string? LprQueue);

/// <summary>État d'une imprimante installée sur le poste (Win32_Printer + port associé).</summary>
public sealed record InstalledPrinter
{
    public required string Name { get; init; }
    public string DriverName { get; init; } = "";
    public string PortName { get; init; } = "";
    public string? Location { get; init; }
    public string? Comment { get; init; }
    public bool Shared { get; init; }
    public string? ShareName { get; init; }
    public bool IsDefault { get; init; }
    public InstalledPortKind PortKind { get; init; }
    /// <summary>Hôte du port TCP/IP ou de l'URL IPP.</summary>
    public string? Host { get; init; }
    /// <summary>« raw », « lpr » ou « ipp ».</summary>
    public string? Protocol { get; init; }
    public int? PortNumber { get; init; }
    public string? LprQueue { get; init; }
    public string? IppUrl { get; init; }

    public bool IsConnection => PortKind == InstalledPortKind.Connection;
    /// <summary>Imprimante réseau directe (gérable par un profil et par « removeUnlisted »).</summary>
    public bool IsNetwork => PortKind is InstalledPortKind.TcpIp or InstalledPortKind.Ipp;
}

/// <summary>Photographie des imprimantes et ports TCP/IP du poste.</summary>
public sealed class PrinterInventory
{
    public List<InstalledPrinter> Printers { get; } = new();
    public List<InstalledPort> TcpPorts { get; } = new();

    public InstalledPrinter? Find(string name) =>
        Printers.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Un port n'est retirable que s'il est TCP/IP et qu'aucune autre imprimante ne l'utilise.</summary>
    public bool IsPortUnused(string portName, string? exceptPrinter = null) =>
        TcpPorts.Any(p => p.Name.Equals(portName, StringComparison.OrdinalIgnoreCase)) &&
        !Printers.Any(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase) &&
                           (exceptPrinter is null || !p.Name.Equals(exceptPrinter, StringComparison.OrdinalIgnoreCase)));

    public static PrinterInventory Read()
    {
        var inventory = new PrinterInventory();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, HostAddress, PortNumber, Protocol, Queue FROM Win32_TCPIPPrinterPort"))
                foreach (var o in s.Get())
                    using (o)
                    {
                        var protocol = Convert.ToInt32(o["Protocol"] ?? 1) == 2 ? "lpr" : "raw";
                        inventory.TcpPorts.Add(new InstalledPort(
                            o["Name"]?.ToString() ?? "",
                            o["HostAddress"]?.ToString() ?? "",
                            protocol,
                            Convert.ToInt32(o["PortNumber"] ?? 9100),
                            protocol == "lpr" ? o["Queue"]?.ToString() : null));
                    }

            using var ps = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
            foreach (var o in ps.Get())
                using (o)
                    inventory.Printers.Add(Describe(
                        o["Name"]?.ToString() ?? "",
                        o["DriverName"]?.ToString() ?? "",
                        o["PortName"]?.ToString() ?? "",
                        NullIfEmpty(o["Location"]?.ToString()),
                        NullIfEmpty(o["Comment"]?.ToString()),
                        o["Shared"] is true,
                        NullIfEmpty(o["ShareName"]?.ToString()),
                        o["Default"] is true,
                        inventory.TcpPorts));
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des imprimantes installées impossible : {ex.Message}");
        }
        return inventory;
    }

    /// <summary>Classe une imprimante d'après son nom et son port (logique pure, testable).</summary>
    public static InstalledPrinter Describe(string name, string driver, string portName, string? location, string? comment,
        bool shared, string? shareName, bool isDefault, IReadOnlyList<InstalledPort> tcpPorts)
    {
        var printer = new InstalledPrinter
        {
            Name = name, DriverName = driver, PortName = portName, Location = location, Comment = comment,
            Shared = shared, ShareName = shareName, IsDefault = isDefault, PortKind = InstalledPortKind.Local,
        };

        if (name.StartsWith(@"\\", StringComparison.Ordinal))
            return printer with { PortKind = InstalledPortKind.Connection };

        var tcp = tcpPorts.FirstOrDefault(p => p.Name.Equals(portName, StringComparison.OrdinalIgnoreCase));
        if (tcp is not null && tcp.Host.Length > 0)
            return printer with
            {
                PortKind = InstalledPortKind.TcpIp, Host = tcp.Host, Protocol = tcp.Protocol,
                PortNumber = tcp.PortNumber, LprQueue = tcp.LprQueue,
            };

        if (Uri.TryCreate(portName, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ipp" or "ipps")
            return printer with { PortKind = InstalledPortKind.Ipp, Host = uri.Host.Trim('[', ']'), Protocol = "ipp", IppUrl = portName };

        if (portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
            return printer with { PortKind = InstalledPortKind.Wsd };

        return printer;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public enum PrinterChangeKind { Create, Driver, Address, Location, Comment, Shared, Default }

public sealed record PrinterChange(PrinterChangeKind Kind, string Description);

/// <summary>Comparaison profil ↔ poste : ce qu'il faut faire pour qu'une imprimante soit conforme (idempotence).</summary>
public static class PrinterDeployPlanner
{
    public static List<PrinterChange> Compare(PrinterProfileEntry entry, InstalledPrinter? existing)
    {
        var changes = new List<PrinterChange>();
        if (existing is null)
        {
            changes.Add(new PrinterChange(PrinterChangeKind.Create, entry.IsConnection ? $"connexion à {entry.Connection}" : $"création ({DescribeTarget(entry)})"));
            return changes;
        }

        if (entry.IsConnection)
        {
            if (entry.SetDefault && !existing.IsDefault) changes.Add(new PrinterChange(PrinterChangeKind.Default, "imprimante par défaut"));
            return changes;
        }

        if (!AddressMatches(entry, existing))
            changes.Add(new PrinterChange(PrinterChangeKind.Address, $"adresse : {DescribeExisting(existing)} → {DescribeTarget(entry)}"));

        var wantedDriver = entry.EffectiveProtocol == "ipp" ? PrinterDriverResolver.IppClassDriver : entry.DriverName?.Trim();
        if (wantedDriver is not null && !wantedDriver.Equals(existing.DriverName, StringComparison.OrdinalIgnoreCase))
            changes.Add(new PrinterChange(PrinterChangeKind.Driver, $"pilote : « {existing.DriverName} » → « {wantedDriver} »"));

        if (entry.Location is not null && !Same(entry.Location, existing.Location))
            changes.Add(new PrinterChange(PrinterChangeKind.Location, $"emplacement : « {existing.Location} » → « {entry.Location.Trim()} »"));
        if (entry.Comment is not null && !Same(entry.Comment, existing.Comment))
            changes.Add(new PrinterChange(PrinterChangeKind.Comment, "commentaire"));
        if (entry.Shared is { } shared && shared != existing.Shared)
            changes.Add(new PrinterChange(PrinterChangeKind.Shared, shared ? "partage activé" : "partage désactivé"));
        if (entry.SetDefault && !existing.IsDefault)
            changes.Add(new PrinterChange(PrinterChangeKind.Default, "imprimante par défaut"));
        return changes;
    }

    /// <summary>
    /// Même destination ? Hôte identique (sans tenir compte de la casse) et, si le protocole est imposé par le profil,
    /// même protocole / port / file. Protocole automatique : RAW, LPR ou IPP vers le même hôte conviennent.
    /// </summary>
    public static bool AddressMatches(PrinterProfileEntry entry, InstalledPrinter existing)
    {
        if (!existing.IsNetwork || existing.Host is null || entry.Address is null) return false;
        if (!existing.Host.Equals(entry.Address.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        var auto = entry.Protocol is null && !entry.IppOnly;
        if (auto) return true;
        return entry.EffectiveProtocol switch
        {
            "raw" => existing.Protocol == "raw" && existing.PortNumber == entry.EffectivePortNumber,
            "lpr" => existing.Protocol == "lpr" && string.Equals(existing.LprQueue, entry.EffectiveLprQueue, StringComparison.OrdinalIgnoreCase),
            "ipp" => existing.Protocol == "ipp" && (entry.IppUrl is null || string.Equals(existing.IppUrl?.TrimEnd('/'), entry.IppUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    public static string DescribeTarget(PrinterProfileEntry entry) => entry.EffectiveProtocol switch
    {
        _ when entry.IsConnection => entry.Connection!,
        "ipp" => entry.EffectiveIppUrl,
        "lpr" => $"{entry.Address} LPR/{entry.EffectiveLprQueue}",
        _ => entry.EffectivePortNumber == 9100 ? entry.Address ?? "" : $"{entry.Address}:{entry.EffectivePortNumber}",
    };

    public static string DescribeExisting(InstalledPrinter p) => p.PortKind switch
    {
        InstalledPortKind.TcpIp when p.Protocol == "lpr" => $"{p.Host} LPR/{p.LprQueue}",
        InstalledPortKind.TcpIp => p.PortNumber is null or 9100 ? p.Host ?? p.PortName : $"{p.Host}:{p.PortNumber}",
        InstalledPortKind.Ipp => p.IppUrl ?? p.PortName,
        _ => p.PortName,
    };

    /// <summary>Nom du port TCP/IP à créer (convention « IP_adresse » de PrinterInstaller pour le RAW 9100).</summary>
    public static string PortNameFor(PrinterProfileEntry entry) => entry.EffectiveProtocol switch
    {
        "lpr" => $"LPR_{entry.Address!.Trim()}_{entry.EffectiveLprQueue}",
        _ => entry.EffectivePortNumber == 9100 ? $"IP_{entry.Address!.Trim()}" : $"IP_{entry.Address!.Trim()}_{entry.EffectivePortNumber}",
    };

    /// <summary>Port existant réutilisable (même hôte, protocole, numéro et file), quel que soit son nom.</summary>
    public static InstalledPort? FindReusablePort(PrinterProfileEntry entry, IEnumerable<InstalledPort> ports) =>
        ports.FirstOrDefault(p =>
            p.Host.Equals(entry.Address?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            p.Protocol == (entry.EffectiveProtocol == "lpr" ? "lpr" : "raw") &&
            (entry.EffectiveProtocol == "lpr"
                ? string.Equals(p.LprQueue, entry.EffectiveLprQueue, StringComparison.OrdinalIgnoreCase)
                : p.PortNumber == entry.EffectivePortNumber));

    private static bool Same(string a, string? b) => a.Trim().Equals((b ?? "").Trim(), StringComparison.Ordinal);
}
