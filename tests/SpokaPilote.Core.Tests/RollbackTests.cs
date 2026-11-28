using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Tests;

/// <summary>Faux magasin de pilotes : aucun vrai pnputil, jamais de pilote réellement retiré.</summary>
internal sealed class FakeDriverStore : IDriverStore
{
    public Dictionary<string, AddedDriverPackage> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> Created { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> DeleteCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = new();
    public int Scans;

    public IReadOnlyDictionary<string, AddedDriverPackage> Describe(IEnumerable<string> publishedNames) =>
        publishedNames.Where(Packages.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, n => Packages[n], StringComparer.OrdinalIgnoreCase);

    public DateTime? PublishedInfCreated(string publishedName) => Created.TryGetValue(publishedName, out var d) ? d : null;

    public Task<ProcessResult> DeleteAsync(string publishedName, CancellationToken ct)
    {
        var code = DeleteCodes.GetValueOrDefault(publishedName, 0);
        if (code is 0 or 3010) { Deleted.Add(publishedName); Packages.Remove(publishedName); }
        return Task.FromResult(new ProcessResult(code, "", "", false));
    }

    public Task ScanDevicesAsync(CancellationToken ct)
    {
        Scans++;
        return Task.CompletedTask;
    }
}

public sealed class RollbackTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0);

    private static AddedDriverPackage Pkg(string name, string original, string version, string provider = "Intel", string? id = null) => new()
    {
        PublishedName = name, OriginalName = original, Version = version, Provider = provider, ClassName = "Net",
        PackageId = id ?? $"{original}_amd64_0123abcd",
    };

    private static Intervention Recorded(DateTime at, params AddedDriverPackage[] packages) => new()
    {
        Id = at.ToString("yyyyMMdd-HHmmss"),
        Timestamp = at,
        MachineId = "aaaaaaaa-0000-4000-8000-000000000001",
        AddedPackages = packages.ToList(),
        AddedDriverPackages = packages.Select(p => p.PublishedName).ToList(),
    };

    private static Task<(bool Success, string Message)> Rollback(Intervention i, FakeDriverStore store, RollbackOptions? options = null) =>
        InterventionHistory.RollbackAsync(i, options ?? new RollbackOptions(), store, Now, "PC-01", "aaaaaaaa-0000-4000-8000-000000000001",
            sharedHistory: false, _ => { }, null, CancellationToken.None);

    [Fact]
    public async Task Only_packages_still_identical_to_what_was_installed_are_removed()
    {
        var store = new FakeDriverStore();
        store.Packages["oem40.inf"] = Pkg("oem40.inf", "e1d.inf", "12.19.1.37");
        // oem41.inf supprimé puis réattribué par Windows à un autre pilote (Windows Update) depuis l'intervention.
        store.Packages["oem41.inf"] = Pkg("oem41.inf", "nvhda.inf", "1.4.0.1", "NVIDIA");
        // oem42.inf : même INF, mais mis à jour depuis par un autre outil.
        store.Packages["oem42.inf"] = Pkg("oem42.inf", "iaStorVD.inf", "20.1.0.1");
        var intervention = Recorded(Now.AddDays(-1),
            Pkg("oem40.inf", "e1d.inf", "12.19.1.37"),
            Pkg("oem41.inf", "rtux64w10.inf", "10.50.0.1", "Realtek"),
            Pkg("oem42.inf", "iaStorVD.inf", "19.5.0.1"),
            Pkg("oem43.inf", "ibtusb.inf", "23.1.0.1"));

        var (ok, message) = await Rollback(intervention, store);

        Assert.True(ok, message);
        Assert.Equal(["oem40.inf"], store.Deleted);
        Assert.Contains("oem41.inf", message);
        Assert.Contains("rtux64w10.inf → nvhda.inf", message);
        Assert.Contains("version 19.5.0.1 → 20.1.0.1", message);
        Assert.Contains("déjà retiré", message); // oem43.inf
        Assert.True(store.Packages.ContainsKey("oem41.inf"));
        Assert.Equal(1, store.Scans);
        Assert.Equal(Now, intervention.RolledBackAt);
    }

    [Fact]
    public void Same_published_name_with_another_store_folder_is_another_package()
    {
        var recorded = Pkg("oem40.inf", "e1d.inf", "12.19.1.37", id: "e1d.inf_amd64_aaaa1111");
        Assert.Null(InterventionHistory.Mismatch(recorded, Pkg("oem40.inf", "E1D.INF", "12.19.1.37", id: "e1d.inf_amd64_aaaa1111")));
        Assert.NotNull(InterventionHistory.Mismatch(recorded, Pkg("oem40.inf", "e1d.inf", "12.19.1.37", id: "e1d.inf_amd64_bbbb2222")));
        Assert.NotNull(InterventionHistory.Mismatch(new AddedDriverPackage { PublishedName = "oem40.inf" }, recorded)); // identité inconnue
    }

    [Fact]
    public async Task Package_still_in_use_is_reported_and_the_intervention_stays_retryable()
    {
        var store = new FakeDriverStore();
        store.Packages["oem40.inf"] = Pkg("oem40.inf", "e1d.inf", "1.0");
        store.Packages["oem41.inf"] = Pkg("oem41.inf", "ibtusb.inf", "2.0");
        store.DeleteCodes["oem41.inf"] = 5; // pnputil refuse (périphérique en service) : jamais de /force
        var intervention = Recorded(Now.AddHours(-2), Pkg("oem40.inf", "e1d.inf", "1.0"), Pkg("oem41.inf", "ibtusb.inf", "2.0"));

        var (ok, message) = await Rollback(intervention, store);

        Assert.False(ok);
        Assert.Contains("oem41.inf (code 5", message);
        Assert.Null(intervention.RolledBackAt);
        Assert.True(intervention.CanRollback);
        Assert.DoesNotContain("/force", PnpUtil.DeleteDriverArguments("oem41.inf"));
        Assert.Equal("/delete-driver oem41.inf /uninstall", PnpUtil.DeleteDriverArguments("oem41.inf"));
    }

    [Fact]
    public async Task Rollback_last_is_refused_beyond_the_age_limit_but_an_explicit_id_is_allowed()
    {
        var store = new FakeDriverStore();
        store.Packages["oem40.inf"] = Pkg("oem40.inf", "e1d.inf", "1.0");
        var old = Recorded(Now.AddDays(-45), Pkg("oem40.inf", "e1d.inf", "1.0"));
        Assert.Equal(30, new Settings().RollbackLastMaxAgeDays);
        Assert.Equal(TimeSpan.FromDays(30), RollbackOptions.Last(new Settings()).MaxAge);
        var last = RollbackOptions.Last(new Settings { RollbackLastMaxAgeDays = 30 });

        var (ok, message) = await Rollback(old, store, last);
        Assert.False(ok);
        Assert.Contains("plus de 30 jours", message);
        Assert.Contains($"spokapilote rollback {old.Id}", message);
        Assert.Empty(store.Deleted);

        Assert.True((await Rollback(old, store, new RollbackOptions { Explicit = true })).Success);
        Assert.Equal(["oem40.inf"], store.Deleted);
    }

    [Fact]
    public async Task Legacy_entry_needs_an_explicit_id_and_only_packages_created_during_the_intervention_go()
    {
        var at = Now.AddDays(-2);
        var legacy = new Intervention { Id = "20261126-120000", Timestamp = at, AddedDriverPackages = { "oem10.inf", "oem11.inf", "oem12.inf" } };
        var store = new FakeDriverStore();
        store.Packages["oem10.inf"] = Pkg("oem10.inf", "a.inf", "1");
        store.Packages["oem11.inf"] = Pkg("oem11.inf", "b.inf", "1");
        store.Created["oem10.inf"] = at.AddMinutes(20);   // ajouté pendant l'intervention
        store.Created["oem11.inf"] = at.AddDays(1);       // nom réattribué depuis à un autre paquet

        var (refused, why) = await Rollback(legacy, store, new RollbackOptions { Explicit = false, MaxAge = TimeSpan.FromDays(30) });
        Assert.False(refused);
        Assert.Contains("version antérieure à 4.0.1", why);
        Assert.Empty(store.Deleted);

        var (ok, message) = await Rollback(legacy, store);
        Assert.True(ok, message);
        Assert.Equal(["oem10.inf"], store.Deleted);
        Assert.Contains("hors de l'intervention", message);
        Assert.Contains("oem12.inf (absent du magasin", message);
    }

    [Fact]
    public void Find_last_returns_the_most_recent_rollbackable_intervention()
    {
        var list = new List<Intervention>
        {
            new() { Id = "c", Kind = InterventionKinds.DriverStoreClean, RemovedDriverPackages = { "oem1.inf" } },
            new() { Id = "b", AddedDriverPackages = { "oem2.inf" } },
            new() { Id = "a", AddedDriverPackages = { "oem3.inf" } },
        };
        Assert.Equal("b", InterventionHistory.Find(list, "last")!.Id);
        Assert.Equal("a", InterventionHistory.Find(list, "a")!.Id);
        Assert.True(InterventionHistory.IsLast("derniere"));
        Assert.False(InterventionHistory.IsLast("20261128-101010"));
    }
}

public sealed class InstallAttributionTests
{
    private static readonly DriverCandidate Candidate = new() { Id = "catalog:x", Source = DriverSourceKind.MicrosoftCatalog, Title = "Intel Ethernet", Version = "12.19" };

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Published_names_returned_by_pnputil_are_parsed_in_any_language()
    {
        const string english = """
            Microsoft PnP Utility

            Adding driver package:  e1d68x64.inf
            Driver package added successfully.
            Published Name:         oem42.inf

            Adding driver package:  e1dmsg.inf
            Driver package added successfully. (Already exists in the system)
            Published Name:         OEM7.INF
            """;
        const string french = """
            Ajout du package de pilotes : e1d68x64.inf
            Package de pilotes ajouté.
            Nom publié :          oem42.inf
            """;
        Assert.Equal(["oem42.inf", "oem7.inf"], PnpUtil.ParsePublishedNames(english));
        Assert.Equal(["oem42.inf"], PnpUtil.ParsePublishedNames(french));
        Assert.Empty(PnpUtil.ParsePublishedNames("Échec de l'ajout du package de pilotes : accès refusé."));
    }

    [Fact]
    public void Only_packages_added_by_this_step_are_attributed()
    {
        // Avant l'étape : oem7 existait déjà (« déjà présent »). Pendant l'étape, Windows Update ajoute oem43 en parallèle.
        var before = Set("oem1.inf", "oem7.inf");
        var after = Set("oem1.inf", "oem7.inf", "oem42.inf", "oem43.inf");
        var added = DriverInstaller.AttributeStep(Candidate, before, after, ["oem42.inf", "oem7.inf"]);
        var only = Assert.Single(added);
        Assert.Equal("oem42.inf", only.PublishedName);
        Assert.Equal(AddedPackageOrigins.PnpUtil, only.Origin);
        Assert.Equal("Intel Ethernet 12.19", only.Candidate);
    }

    [Fact]
    public void Without_names_from_pnputil_the_step_window_is_used()
    {
        var added = DriverInstaller.AttributeStep(Candidate, Set("oem1.inf"), Set("oem1.inf", "oem50.inf", "oem51.inf"), []);
        Assert.Equal(["oem50.inf", "oem51.inf"], added.Select(a => a.PublishedName));
        Assert.All(added, a => Assert.Equal(AddedPackageOrigins.Step, a.Origin));
        Assert.Empty(DriverInstaller.AttributeStep(Candidate, Set("oem1.inf"), Set("oem1.inf"), []));
    }
}
