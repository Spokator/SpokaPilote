using System.Net;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Core.Tests;

public class PrinterProfileTests
{
    private static PrinterProfileContext Poste(string name, params string[] ips) =>
        new() { ComputerName = name, Addresses = ips.Select(IPAddress.Parse).ToList() };

    // ------------------------------------------------------------------ conditions

    [Theory]
    [InlineData("COMPTA-PC01", "COMPTA-*", true)]
    [InlineData("compta-pc01", "COMPTA-*", true)]
    [InlineData("XCOMPTA-01", "COMPTA-*", false)]
    [InlineData("PC-01", "PC-??", true)]
    [InlineData("PC-001", "PC-??", false)]
    [InlineData("LYON-RH-03", "*-RH-*", true)]
    [InlineData("A.B", "A.B", true)]
    [InlineData("AXB", "A.B", false)] // le point n'est pas un joker
    public void Wildcard_matches_computer_names(string name, string pattern, bool expected)
        => Assert.Equal(expected, PrinterProfileRules.WildcardMatch(name, pattern));

    [Theory]
    [InlineData("10.1.0.0/16", "10.1.200.3", true)]
    [InlineData("10.1.0.0/16", "10.2.0.1", false)]
    [InlineData("10.0.16.0/20", "10.0.31.255", true)]
    [InlineData("10.0.16.0/20", "10.0.32.0", false)]
    [InlineData("192.168.1.10/32", "192.168.1.10", true)]
    [InlineData("192.168.1.10", "192.168.1.11", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "10.0.0.1", false)]
    public void Cidr_contains(string cidr, string ip, bool expected)
        => Assert.Equal(expected, PrinterProfileRules.CidrContains(cidr, IPAddress.Parse(ip)));

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("abc/24")]
    [InlineData("10.0.0.0/8/1")]
    [InlineData("")]
    public void Invalid_cidr_is_rejected(string cidr) => Assert.False(PrinterProfileRules.TryParseCidr(cidr, out _, out _));

    [Fact]
    public void Condition_requires_all_criteria_and_honours_exclusions()
    {
        var c = new PrinterProfileCondition { Subnets = new() { "10.1.0.0/16" }, ComputerNames = new() { "COMPTA-*" }, ExcludeComputerNames = new() { "COMPTA-TEST*" } };
        Assert.True(c.Matches(Poste("COMPTA-01", "10.1.4.20")));
        Assert.False(c.Matches(Poste("COMPTA-01", "10.2.4.20")));    // mauvais site
        Assert.False(c.Matches(Poste("RH-01", "10.1.4.20")));        // mauvais service
        Assert.False(c.Matches(Poste("COMPTA-TEST2", "10.1.4.20"))); // exclu
        Assert.True(c.Matches(Poste("COMPTA-02", "192.168.0.5", "10.1.9.9"))); // une des adresses suffit
    }

    private const string ThreeSites = """
        {
          // Profil de démonstration : 3 sites
          "name": "Groupe",
          "printers": [ { "name": "Copieur Direction", "address": "10.0.0.50", "when": { "computerNames": [ "DIR-*" ] } } ],
          "groups": [
            { "name": "Paris", "when": { "subnets": [ "10.1.0.0/16" ] },
              "printers": [ { "name": "Paris - Accueil", "address": "10.1.0.25", "driverName": "KX DRIVER for Universal Printing", "setDefault": true },
                            { "name": "Paris - Compta", "address": "imp-compta.paris.corp.local", "when": { "computerNames": [ "COMPTA-*" ] } } ] },
            { "name": "Lyon", "when": { "subnets": [ "10.2.0.0/16" ] },
              "printers": [ { "name": "Lyon - Accueil", "address": "10.2.0.25", "protocol": "lpr", "lprQueue": "PASSTHRU" } ] },
            { "name": "Nantes", "when": { "subnets": [ "10.3.0.0/16", "192.168.30.0/24" ] },
              "printers": [ { "name": "Nantes - Atelier", "address": "10.3.0.25", "ippOnly": true } ] },
          ],
        }
        """;

    [Fact]
    public void Three_sites_profile_selects_printers_by_subnet_and_name()
    {
        var profile = PrinterProfile.Parse(ThreeSites);
        Assert.True(profile.Validate().IsValid, string.Join("; ", profile.Validate().Errors));
        Assert.Equal(5, profile.AllEntries().Count());

        Assert.Equal(new[] { "Paris - Accueil" }, profile.ApplicableEntries(Poste("RH-01", "10.1.3.3")).Select(e => e.Name));
        Assert.Equal(new[] { "Paris - Accueil", "Paris - Compta" }, profile.ApplicableEntries(Poste("COMPTA-07", "10.1.3.3")).Select(e => e.Name));
        Assert.Equal(new[] { "Lyon - Accueil" }, profile.ApplicableEntries(Poste("COMPTA-07", "10.2.3.3")).Select(e => e.Name)); // condition du groupe d'abord
        Assert.Equal(new[] { "Nantes - Atelier" }, profile.ApplicableEntries(Poste("PC", "192.168.30.40")).Select(e => e.Name));
        Assert.Equal(new[] { "Copieur Direction" }, profile.ApplicableEntries(Poste("DIR-01", "172.16.0.1")).Select(e => e.Name));
        Assert.Empty(profile.ApplicableEntries(Poste("PC", "172.16.0.1")));

        var lyon = profile.Groups[1].Printers[0];
        Assert.Equal("lpr", lyon.EffectiveProtocol);
        Assert.Equal("LPR_10.2.0.25_PASSTHRU", PrinterDeployPlanner.PortNameFor(lyon));
        Assert.Equal("http://10.3.0.25:631/ipp/print", profile.Groups[2].Printers[0].EffectiveIppUrl);
    }

    // ------------------------------------------------------------------ validation

    private static List<string> Errors(string printersJson) =>
        PrinterProfile.Parse($$"""{ "printers": [ {{printersJson}} ] }""").Validate().Errors;

    [Fact]
    public void Validation_accepts_a_minimal_printer() => Assert.Empty(Errors("""{ "name": "A", "address": "10.0.0.1" }"""));

    [Theory]
    [InlineData("""{ "name": "A" }""", "indiquez soit")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "connection": "\\\\srv\\q" }""", "indiquez soit")]
    [InlineData("""{ "address": "10.0.0.1" }""", "« name » obligatoire")]
    [InlineData("""{ "name": "A\\B", "address": "10.0.0.1" }""", "nom invalide")]
    [InlineData("""{ "name": "A", "address": "imp 01; Remove-Item" }""", "adresse")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "protocol": "smb" }""", "protocole")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "portNumber": 70000 }""", "port 70000")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "ippOnly": true, "protocol": "raw" }""", "incompatible")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "lprQueue": "a b" }""", "file LPR")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "ippUrl": "ftp://x/y" }""", "URL IPP")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "when": { "subnets": [ "10.0.0.0/40" ] } }""", "sous-réseau")]
    [InlineData("""{ "name": "A", "address": "10.0.0.1", "when": { "computerNames": [ "PC 01" ] } }""", "motif")]
    [InlineData("""{ "connection": "srv\\q" }""", "connexion")]
    public void Validation_rejects_invalid_entries(string json, string expected)
        => Assert.Contains(Errors(json), e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Validation_handles_duplicate_names()
    {
        var unconditional = PrinterProfile.Parse("""{ "printers": [ { "name": "A", "address": "10.0.0.1" }, { "name": "a", "address": "10.0.0.2" } ] }""");
        Assert.Contains(unconditional.Validate().Errors, e => e.Contains("plusieurs fois"));

        var bySite = PrinterProfile.Parse("""
            { "groups": [ { "when": { "subnets": ["10.1.0.0/16"] }, "printers": [ { "name": "Accueil", "address": "10.1.0.9" } ] },
                          { "when": { "subnets": ["10.2.0.0/16"] }, "printers": [ { "name": "Accueil", "address": "10.2.0.9" } ] } ] }
            """);
        var v = bySite.Validate();
        Assert.True(v.IsValid);
        Assert.Contains(v.Warnings, w => w.Contains("Accueil"));
    }

    [Fact]
    public void Validation_rejects_unknown_version_and_reports_bad_json()
    {
        Assert.Contains(PrinterProfile.Parse("""{ "version": 9, "printers": [] }""").Validate().Errors, e => e.Contains("Version"));
        Assert.Throws<FormatException>(() => PrinterProfile.Parse("{ \"printers\": [ { \"name\": } ] }"));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("imp-01.corp.local", true)]
    [InlineData("IMP01", true)]
    [InlineData("fe80::1", true)]
    [InlineData("-imp", false)]
    [InlineData("imp_01", false)]
    [InlineData("imp'01", false)]
    [InlineData("", false)]
    public void Host_validation(string host, bool expected) => Assert.Equal(expected, PrinterProfileRules.IsValidHost(host));

    // ------------------------------------------------------------------ idempotence (comparaison profil ↔ poste)

    private static readonly InstalledPort[] Ports =
    {
        new("IP_10.0.0.5", "10.0.0.5", "raw", 9100, null),
        new("10.0.0.6", "10.0.0.6", "raw", 9101, null),
        new("LPR_10.0.0.7", "10.0.0.7", "lpr", 515, "lp"),
    };

    private static InstalledPrinter Installed(string name, string port, string driver = "HP Universal Printing PCL 6",
        string? location = null, bool shared = false, bool isDefault = false) =>
        PrinterInventory.Describe(name, driver, port, location, null, shared, null, isDefault, Ports);

    [Fact]
    public void Absent_printer_must_be_created()
    {
        var changes = PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5" }, null);
        Assert.Equal(PrinterChangeKind.Create, Assert.Single(changes).Kind);
    }

    [Fact]
    public void Same_name_same_address_is_up_to_date()
    {
        var existing = Installed("A", "IP_10.0.0.5");
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5" }, existing));
        // Pilote non précisé dans le profil : le pilote actuel convient.
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", Location = null, Shared = null }, existing));
        // Casse du nom d'hôte et du nom de pilote indifférente.
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", DriverName = "hp universal printing pcl 6" }, existing));
    }

    [Fact]
    public void Different_address_or_driver_requires_update()
    {
        var existing = Installed("A", "IP_10.0.0.5", location: "RDC");
        var changes = PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.99", DriverName = "Microsoft IPP Class Driver", Location = "1er étage" }, existing);
        Assert.Equal(new[] { PrinterChangeKind.Address, PrinterChangeKind.Driver, PrinterChangeKind.Location }, changes.Select(c => c.Kind));
        Assert.Contains("10.0.0.5 → 10.0.0.99", changes[0].Description);
    }

    [Fact]
    public void Forced_protocol_must_match_port_configuration()
    {
        var raw9101 = Installed("B", "10.0.0.6");
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "B", Address = "10.0.0.6" }, raw9101)); // automatique
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "B", Address = "10.0.0.6", Protocol = "raw", PortNumber = 9101 }, raw9101));
        Assert.Contains(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "B", Address = "10.0.0.6", Protocol = "raw" }, raw9101), c => c.Kind == PrinterChangeKind.Address);
        Assert.Contains(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "B", Address = "10.0.0.6", Protocol = "lpr" }, raw9101), c => c.Kind == PrinterChangeKind.Address);

        var lpr = Installed("C", "LPR_10.0.0.7");
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "C", Address = "10.0.0.7", Protocol = "lpr" }, lpr));
        Assert.NotEmpty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "C", Address = "10.0.0.7", Protocol = "lpr", LprQueue = "PASSTHRU" }, lpr));
    }

    [Fact]
    public void Ipp_queue_is_compared_by_host_and_driver()
    {
        var ipp = Installed("D", "http://10.0.0.8:631/ipp/print", driver: "Microsoft IPP Class Driver");
        Assert.Equal(InstalledPortKind.Ipp, ipp.PortKind);
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "D", Address = "10.0.0.8", IppOnly = true }, ipp));
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "D", Address = "10.0.0.8" }, ipp)); // protocole automatique
        Assert.Contains(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "D", Address = "10.0.0.8", Protocol = "raw" }, ipp), c => c.Kind == PrinterChangeKind.Address);
        // File RAW existante, profil « ippOnly » : adresse (protocole) et pilote à changer.
        var raw = Installed("A", "IP_10.0.0.5");
        Assert.Equal(new[] { PrinterChangeKind.Address, PrinterChangeKind.Driver },
            PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", IppOnly = true }, raw).Select(c => c.Kind));
    }

    [Fact]
    public void Optional_settings_only_change_when_specified()
    {
        var existing = Installed("A", "IP_10.0.0.5", location: "RDC", shared: true, isDefault: false);
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5" }, existing));
        Assert.Equal(PrinterChangeKind.Shared, Assert.Single(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", Shared = false }, existing)).Kind);
        Assert.Equal(PrinterChangeKind.Default, Assert.Single(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", SetDefault = true }, existing)).Kind);
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5", Location = " RDC " }, existing));
    }

    [Fact]
    public void Local_or_wsd_printer_with_same_name_is_redirected_to_the_network_address()
    {
        var wsd = Installed("A", "WSD-1234-5678");
        Assert.Equal(InstalledPortKind.Wsd, wsd.PortKind);
        Assert.Contains(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Name = "A", Address = "10.0.0.5" }, wsd), c => c.Kind == PrinterChangeKind.Address);
    }

    [Fact]
    public void Existing_connection_is_up_to_date()
    {
        var connection = Installed(@"\\srv-print\Compta", "nul:");
        Assert.True(connection.IsConnection);
        Assert.Empty(PrinterDeployPlanner.Compare(new PrinterProfileEntry { Connection = @"\\srv-print\Compta" }, connection));
    }

    [Fact]
    public void Existing_port_is_reused_whatever_its_name()
    {
        Assert.Equal("10.0.0.6", PrinterDeployPlanner.FindReusablePort(new PrinterProfileEntry { Address = "10.0.0.6", PortNumber = 9101 }, Ports)?.Name);
        Assert.Null(PrinterDeployPlanner.FindReusablePort(new PrinterProfileEntry { Address = "10.0.0.6" }, Ports)); // 9100 attendu
        Assert.Equal("IP_10.0.0.6", PrinterDeployPlanner.PortNameFor(new PrinterProfileEntry { Address = "10.0.0.6" }));
        Assert.Equal("IP_10.0.0.6_9101", PrinterDeployPlanner.PortNameFor(new PrinterProfileEntry { Address = "10.0.0.6", PortNumber = 9101 }));
    }

    // ------------------------------------------------------------------ inventaire et export

    private static PrinterInventory SampleInventory()
    {
        var inv = new PrinterInventory();
        inv.TcpPorts.AddRange(Ports);
        inv.Printers.Add(Installed("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF"));
        inv.Printers.Add(Installed("OneNote (Desktop)", "nul:", "Send to Microsoft OneNote 16 Driver"));
        inv.Printers.Add(Installed("Fax", "SHRFAX:", "Microsoft Shared Fax Driver"));
        inv.Printers.Add(Installed("Copieur RDC", "IP_10.0.0.5", location: "RDC", isDefault: true));
        inv.Printers.Add(Installed("Traceur", "10.0.0.6", driver: "HP DesignJet"));
        inv.Printers.Add(Installed("Atelier", "LPR_10.0.0.7", driver: "Generic / Text Only"));
        inv.Printers.Add(Installed("Accueil WSD", "WSD-aaaa"));
        inv.Printers.Add(Installed(@"\\srv-print\Compta", "nul:", "KX DRIVER"));
        return inv;
    }

    [Fact]
    public void Export_keeps_network_printers_and_connections_only()
    {
        var (profile, skipped) = PrinterProfileDeployer.Export(SampleInventory(), "REF-01");
        Assert.Equal(new[] { "Atelier", "Copieur RDC", "Traceur", @"\\srv-print\Compta" }, profile.Printers.Select(p => p.EffectiveName));
        Assert.Equal(new[] { "Accueil WSD", "Fax", "Microsoft Print to PDF", "OneNote (Desktop)" }, skipped.Select(s => s.Name));
        Assert.Contains(skipped, s => s.Name == "Accueil WSD" && s.Reason.Contains("WSD"));

        var copieur = profile.Printers.Single(p => p.Name == "Copieur RDC");
        Assert.Equal("10.0.0.5", copieur.Address);
        Assert.Equal("HP Universal Printing PCL 6", copieur.DriverName);
        Assert.True(copieur.SetDefault);
        Assert.Null(copieur.PortNumber);
        Assert.Equal(9101, profile.Printers.Single(p => p.Name == "Traceur").PortNumber);
        Assert.Equal("lpr", profile.Printers.Single(p => p.Name == "Atelier").Protocol);
        Assert.Equal("REF-01", profile.ExportedFrom);
        Assert.True(profile.Validate().IsValid, string.Join("; ", profile.Validate().Errors));
    }

    [Fact]
    public void Exported_profile_redeployed_on_the_same_post_is_up_to_date()
    {
        var inventory = SampleInventory();
        var (profile, _) = PrinterProfileDeployer.Export(inventory, "REF-01");
        var reloaded = PrinterProfile.Parse(profile.ToJson()); // aller-retour JSON
        foreach (var entry in reloaded.ApplicableEntries(Poste("REF-01", "10.0.0.2")))
            Assert.Empty(PrinterDeployPlanner.Compare(entry, inventory.Find(entry.EffectiveName!)));
    }

    [Fact]
    public void Json_is_camel_case_without_nulls()
    {
        var json = new PrinterProfile { Printers = { new PrinterProfileEntry { Name = "Copieur « RDC »", Address = "10.0.0.5" } } }.ToJson();
        Assert.Contains("\"name\": \"Copieur « RDC »\"", json);
        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("effectiveName", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"removeUnlisted\": false", json);
    }

    [Fact]
    public void Unused_port_detection()
    {
        var inv = SampleInventory();
        Assert.True(inv.IsPortUnused("IP_10.0.0.5", exceptPrinter: "Copieur RDC"));
        Assert.False(inv.IsPortUnused("IP_10.0.0.5"));
        Assert.False(inv.IsPortUnused("PORTPROMPT:", exceptPrinter: "Microsoft Print to PDF")); // pas un port TCP/IP
    }

    // ------------------------------------------------------------------ source du profil

    [Theory]
    [InlineData("https://console.corp.fr/api/printer-profile", true)]
    [InlineData("http://localhost:5000/api/printer-profile", true)]
    [InlineData("http://127.0.0.1:5000/x", true)]
    [InlineData("http://[::1]:5000/x", true)]
    [InlineData("http://console.corp.fr/api/printer-profile", false)]
    [InlineData("ftp://console.corp.fr/profil.json", false)]
    public void Profile_url_requires_https_except_localhost(string url, bool expected)
        => Assert.Equal(expected, PrinterProfileSource.IsAllowedUrl(new Uri(url)));

    [Fact]
    public void Server_endpoint_is_built_from_server_url()
    {
        Assert.Equal("https://console.corp.fr/api/printer-profile", PrinterProfileSource.ServerProfileUri("https://console.corp.fr/").ToString());
        Assert.Equal("https://console.corp.fr/spoka/api/printer-profile", PrinterProfileSource.ServerProfileUri("https://console.corp.fr/spoka").ToString());
        Assert.True(PrinterProfileSource.IsServerKeyword(null));
        Assert.True(PrinterProfileSource.IsServerKeyword("serveur"));
        Assert.False(PrinterProfileSource.IsServerKeyword("profil.json"));
    }

    [Fact]
    public async Task Missing_server_configuration_is_reported()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrinterProfileSource.LoadAsync(null, new Infrastructure.Settings()));
        await Assert.ThrowsAsync<Install.SecurityException>(() =>
            PrinterProfileSource.LoadAsync(null, new Infrastructure.Settings { ServerUrl = "http://console.corp.fr" }));
    }
}
