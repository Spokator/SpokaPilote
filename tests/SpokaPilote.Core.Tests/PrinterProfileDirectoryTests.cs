using System.Net;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Core.Tests;

/// <summary>Conditions Active Directory des profils d'imprimantes (site, unité d'organisation) et version 2.</summary>
public class PrinterProfileDirectoryTests
{
    private static PrinterProfileContext Ctx(string? site, string? dn) => new()
    {
        ComputerName = "PC-COMPTA-01",
        Addresses = new[] { IPAddress.Parse("10.1.4.20") },
        AdSite = site,
        ComputerDistinguishedName = dn,
    };

    [Theory]
    [InlineData("CN=PC01,OU=Compta,OU=Paris,DC=corp,DC=local", "OU=Paris,DC=corp,DC=local", true)]
    [InlineData("CN=PC01,OU=Compta,OU=Paris,DC=corp,DC=local", "OU=Paris", true)]
    [InlineData("CN=PC01,OU=Compta,OU=Paris,DC=corp,DC=local", "ou = compta , ou=paris", true)]
    [InlineData("CN=PC01,OU=Compta,OU=Paris,DC=corp,DC=local", "OU=Lyon", false)]
    [InlineData("CN=PC01,OU=Compta-Paris,DC=corp,DC=local", "OU=Compta", false)]       // composant entier, pas un préfixe
    [InlineData("CN=PC01,OU=Paris,DC=corp,DC=local", "OU=Compta,OU=Paris", false)]
    public void Organizational_unit_filter_matches_whole_components(string dn, string filter, bool expected)
        => Assert.Equal(expected, PrinterProfileRules.DistinguishedNameContains(dn, filter));

    [Theory]
    [InlineData("OU=Paris,DC=corp,DC=local", true)]
    [InlineData("OU=Paris", true)]
    [InlineData("Paris", false)]
    [InlineData("OU=Paris,,DC=corp", false)]
    [InlineData("", false)]
    public void Organizational_unit_filter_is_validated(string filter, bool valid)
        => Assert.Equal(valid, PrinterProfileRules.IsValidDistinguishedNameFilter(filter));

    [Fact]
    public void Directory_conditions_select_printers_by_site_and_ou()
    {
        var profile = PrinterProfile.Parse("""
        {
          "version": 2,
          "groups": [
            { "name": "Paris", "when": { "adSites": [ "PARIS*" ] },
              "printers": [ { "name": "Paris - Accueil", "address": "10.1.0.25" } ] },
            { "name": "Compta", "when": { "organizationalUnits": [ "OU=Compta" ] },
              "printers": [ { "name": "Compta", "address": "10.1.0.26" } ] }
          ]
        }
        """);
        Assert.True(profile.Validate().IsValid);

        var paris = profile.ApplicableEntries(Ctx("PARIS-SIEGE", "CN=PC-COMPTA-01,OU=Compta,DC=corp,DC=local")).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "Paris - Accueil", "Compta" }, paris);
        var lyon = profile.ApplicableEntries(Ctx("LYON", "CN=PC-COMPTA-01,OU=Ventes,DC=corp,DC=local"));
        Assert.Empty(lyon);
    }

    [Fact]
    public void Unknown_directory_information_never_matches()
    {
        // Poste hors domaine (ou information pas encore connue) : prudence, l'imprimante n'est pas installée.
        var condition = new PrinterProfileCondition { AdSites = new() { "*" }, OrganizationalUnits = new() { "OU=Paris" } };
        Assert.False(condition.Matches(Ctx(null, null)));
        Assert.False(new PrinterProfileCondition { OrganizationalUnits = new() { "OU=Paris" } }.Matches(Ctx("PARIS", null)));
        Assert.True(new PrinterProfileCondition { AdSites = new() { "*" } }.Matches(Ctx("PARIS", null)));
    }

    [Fact]
    public void Directory_conditions_require_version_2()
    {
        var json = """{ "printers": [ { "name": "A", "address": "10.0.0.1", "when": { "adSites": [ "PARIS" ] } } ] }""";
        var v1 = PrinterProfile.Parse(json);
        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v1.RequiredVersion);
        Assert.Contains(v1.Validate().Errors, e => e.Contains("version") && e.Contains('2'));

        v1.Version = v1.RequiredVersion;
        Assert.True(v1.Validate().IsValid);
        Assert.Contains("\"version\": 2", v1.ToJson());
    }

    [Fact]
    public void Profiles_without_directory_conditions_stay_version_1()
    {
        var profile = new PrinterProfile { Printers = { new PrinterProfileEntry { Name = "A", Address = "10.0.0.1" } } };
        Assert.Equal(1, profile.RequiredVersion);
        Assert.Contains("\"version\": 1", profile.ToJson()); // lisible par SpokaPilote 3.1 et antérieurs
        Assert.True(PrinterProfile.Parse("""{ "version": 1, "printers": [ { "name": "A", "address": "10.0.0.1" } ] }""").Validate().IsValid);
    }

    [Fact]
    public void Invalid_directory_criteria_are_reported()
    {
        var profile = PrinterProfile.Parse("""
        { "version": 2, "printers": [ { "name": "A", "address": "10.0.0.1",
          "when": { "adSites": [ "PARIS SIEGE" ], "organizationalUnits": [ "Paris" ] } } ] }
        """);
        var errors = profile.Validate().Errors;
        Assert.Contains(errors, e => e.Contains("site Active Directory"));
        Assert.Contains(errors, e => e.Contains("unité d'organisation"));
    }

    [Fact]
    public void Reading_directory_information_never_throws()
    {
        var (site, dn) = PrinterProfileContext.ReadDirectoryInfo();
        Assert.True(site is null || site.Length > 0);
        Assert.True(dn is null || dn.Length > 0);
    }
}
