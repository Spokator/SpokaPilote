using System.Text.RegularExpressions;
using System.Xml.Linq;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Sources;
using Xunit.Abstractions;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Contrôles sur des fichiers RÉELS téléchargés à la main (lecture seule) dans un dossier désigné par la variable
/// SPOKAPILOTE_REAL_SAMPLES : CatalogPC.cab, DriverPackCatalog.cab (Dell), hpref.cab, hpdp.cab (HP), x-*\ (leur contenu
/// extrait par expand.exe), search.html et scoped.html (Microsoft Update Catalog, entrée 027e8668-…). Ignorés sinon.
/// </summary>
public sealed class RealSamplesTests(ITestOutputHelper output)
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("SPOKAPILOTE_REAL_SAMPLES");

    public sealed class RealSamplesFactAttribute : FactAttribute
    {
        public RealSamplesFactAttribute()
        {
            if (string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder)) Skip = "SPOKAPILOTE_REAL_SAMPLES non défini (fichiers réels téléchargés à la main).";
        }
    }

    [RealSamplesFact]
    public void Real_signed_catalogs_carry_a_signing_time_and_the_expected_publisher()
    {
        var sources = new Dictionary<string, string>
        {
            ["CatalogPC.cab"] = "https://downloads.dell.com/catalog/CatalogPC.cab",
            ["DriverPackCatalog.cab"] = "https://downloads.dell.com/catalog/DriverPackCatalog.cab",
            ["hpref.cab"] = "https://hpia.hpcloud.hp.com/ref/8723/8723_64_11.0.23h2.cab",
            ["hpdp.cab"] = "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab",
        };
        foreach (var (name, url) in sources)
        {
            var path = Path.Combine(Folder!, name);
            var sig = SignatureVerifier.Verify(path);
            var signedAt = SignatureVerifier.GetSigningTime(path);
            output.WriteLine($"{name} : {sig} ; signé le {signedAt:O}");
            Assert.True(sig.IsValid);
            Assert.True(TrustPolicy.IsExpectedPublisher(new Uri(url), sig.Signer));
            Assert.True(new TrustPolicy(new Settings()).IsTrustedPublisher(sig.Signer));
            Assert.NotNull(signedAt);
            Assert.InRange(signedAt.Value, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow.AddDays(1));
        }
    }

    [RealSamplesFact]
    public void Real_catalog_entry_page_confirms_the_real_search_row()
    {
        const string id = "027e8668-e62d-411f-a4a8-7ad2a44d8d09";
        var details = MicrosoftCatalogClient.ParseEntryDetails(File.ReadAllText(Path.Combine(Folder!, "scoped.html")), id);
        Assert.NotNull(details);
        output.WriteLine($"{details}");
        var row = Assert.Single(MicrosoftCatalogClient.ParseSearchResults(File.ReadAllText(Path.Combine(Folder!, "search.html"))), e => e.UpdateId == id);
        Assert.Null(CatalogPackageCheck.CompareWithDetails(row.Title, row.Version, details));
        Assert.Equal("Intel", details.Provider);
    }

    [RealSamplesFact]
    public void Real_vendor_commands_still_pass_the_scan_checks()
    {
        var hp = XDocument.Load(Path.Combine(Folder!, "x-hpref", "hpref.cab"));
        var commands = hp.Descendants("SilentInstall").Select(e => e.Value.Trim()).Where(c => c.Length > 0).ToList();
        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.Null(VendorCommand.Validate(c)));
        // SoftPaqs .exe : acceptés. Un SoftPaq livré en .zip (hpcpr-….zip dans la référence 8723 de novembre 2026) ne peut
        // pas être lancé par « "%FILE%" /s /e » : refusé à chaque installation, il est désormais écarté dès l'analyse.
        var files = hp.Descendants("Url").Select(u => Path.GetFileName(u.Value.Trim())).Where(n => n.Length > 0).ToList();
        foreach (var name in files)
        {
            var problem = VendorCommand.Validate("\"%FILE%\" /s /e /f \"%PACKAGEPATH%\"", name);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Assert.Null(problem);
            else Assert.NotNull(problem);
        }
        output.WriteLine($"HP : SoftPaqs non .exe écartés : {string.Join(", ", files.Where(n => !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))}");

        var dell = File.ReadAllText(Path.Combine(Folder!, "x-CatalogPC", "catalogpc.cab"));
        var paths = Regex.Matches(dell, "<SoftwareComponent [^>]*path=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.Null(VendorCommand.Validate("\"%FILE%\" /s /l=\"%PACKAGEPATH%\\dup.log\"", Path.GetFileName(p))));
        output.WriteLine($"HP : {commands.Count} commandes ; Dell : {paths.Count} paquets.");
    }
}
