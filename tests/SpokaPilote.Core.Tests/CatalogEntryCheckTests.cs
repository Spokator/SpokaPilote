using System.Net;
using System.Text;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Paquets du Microsoft Update Catalog : la page de recherche (peut-être relayée, donc altérée, par la console) ne fait
/// que désigner une entrée. Titre et version sont confirmés sur la fiche lue directement chez Microsoft, puis sur l'INF
/// signé du paquet (DriverVer, Provider) : une console compromise ne peut pas présenter un ancien pilote sous le titre ou
/// la version d'un autre pour contourner la politique, les anneaux ou « plus récent que l'installé ».
/// </summary>
public sealed class CatalogEntryCheckTests : IDisposable
{
    private const string UpdateId = "027e8668-e62d-411f-a4a8-7ad2a44d8d09";
    private const string Title = "Intel Net Driver Update (2.1.5.10)";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "catalog-" + Guid.NewGuid().ToString("N"));

    public CatalogEntryCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* fichiers temporaires */ }
    }

    /// <summary>Extrait de la fiche réelle (ScopedViewInline.aspx, novembre 2026) de l'entrée Intel I225-V 2.1.5.10.</summary>
    private static string DetailsPage(string title = Title, string version = "2.1.5.10", string id = UpdateId) => $"""
        <div id="titleDiv"><span id="ScopedViewHandler_titleText">{title}</span></div>
        <span id="ScopedViewHandler_labelLastModified_Separator" class="labelTitle">Last Modified:</span>
        <span id="ScopedViewHandler_date">12/29/2025</span>
        <span id="ScopedViewHandler_UpdateID">{id}</span>
        <span id="ScopedViewHandler_company">Wistron Corporation</span>
        <div id="driverManufacturerDiv"><span id="ScopedViewHandler_labelDriverManufacturer_Separator" class="labelTitle">Driver Manufacturer:</span>
          <span id="ScopedViewHandler_labelSeparator" class="labelTitle"></span>
          <span id="ScopedViewHandler_manufacturer">Intel</span></div>
        <div id="driverClassDiv"><span id="ScopedViewHandler_driverClass">Networking</span></div>
        <div id="driverProviderDiv"><span id="ScopedViewHandler_labelDriverProvider_Separator" class="labelTitle">Driver Provider:</span>
          <span id="ScopedViewHandler_driverProvider">Intel</span></div>
        <div id="versionDiv"><span id="ScopedViewHandler_labelDriverVersion_Separator" class="labelTitle">Version:</span>
          <span id="ScopedViewHandler_version">{version}</span></div>
        """;

    private static CatalogEntryDetails Details(string title = Title, string? version = "2.1.5.10", string? provider = "Intel") =>
        new(UpdateId, title, version, provider, provider);

    private string Inf(string provider, string driverVer, string manufacturer = "Intel")
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName, "e2f.inf");
        File.WriteAllText(path, $"""
            [Version]
            Signature   = "$WINDOWS NT$"
            Class       = Net
            Provider    = %Provider%
            DriverVer   = {driverVer}
            CatalogFile = e2f.cat

            [Manufacturer]
            %Mfg% = Intel, NTamd64.10.0

            [Intel.NTamd64.10.0]
            %E15F3% = E15F3.ndi, PCI\VEN_8086&DEV_15F3

            [Strings]
            Provider = "{provider}"
            Mfg      = "{manufacturer}"
            E15F3    = "Intel(R) Ethernet Controller I225-V"
            """);
        return path;
    }

    // ------------------------------------------------------------------ fiche Microsoft

    [Fact]
    public void Entry_details_are_read_from_the_Microsoft_page()
    {
        var details = MicrosoftCatalogClient.ParseEntryDetails(DetailsPage(), UpdateId);
        Assert.NotNull(details);
        Assert.Equal(Title, details.Title);
        Assert.Equal("2.1.5.10", details.Version);
        Assert.Equal("Intel", details.Provider);
        Assert.Equal("Intel", details.Manufacturer);

        Assert.Null(MicrosoftCatalogClient.ParseEntryDetails(DetailsPage(id: "9eccd056-3f65-4d34-98f5-17263d1b1cb4"), UpdateId)); // autre entrée
        Assert.Null(MicrosoftCatalogClient.ParseEntryDetails("<html>Maintenance</html>", UpdateId));                             // page inattendue
        Assert.Null(MicrosoftCatalogClient.ParseEntryDetails(DetailsPage(version: "n/a"), UpdateId)!.Version);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Entry_details_are_always_requested_directly_at_Microsoft()
    {
        var console = new Stub(_ => throw new InvalidOperationException("la console ne doit pas être sollicitée"));
        var microsoft = new Stub(req =>
        {
            Assert.Equal("www.catalog.update.microsoft.com", req.RequestUri!.Host);
            Assert.Equal("/ScopedViewInline.aspx", req.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DetailsPage(), Encoding.UTF8, "text/html") };
        });
        using (ConsoleCache.UseForTest(new ConsoleCache(new Uri("https://console.exemple.fr/"), "jeton", ConsoleCacheMode.On, catalogs: true, console)))
        {
            var details = await MicrosoftCatalogClient.GetEntryDetailsAsync(UpdateId, new HttpClient(microsoft), default);
            Assert.Equal("2.1.5.10", details?.Version);
        }
        Assert.Empty(console.Requests);
        Assert.Single(microsoft.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => MicrosoftCatalogClient.GetEntryDetailsAsync("x&updateid=y", new HttpClient(microsoft), default));

        var down = new Stub(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(await MicrosoftCatalogClient.GetEntryDetailsAsync(UpdateId, new HttpClient(down), default));
    }

    [Theory]
    [InlineData(Title, "2.1.5.10", null)]
    [InlineData("intel  net driver update (2.1.5.10) ", "2.1.5.10.0", null)]          // casse, espaces, version complétée
    [InlineData("Intel Net Driver Update (2.1.6.0)", "2.1.5.10", "titre")]            // titre d'une autre entrée
    [InlineData(Title, "2.1.6.0", "version")]                                          // version gonflée : « plus récent »
    [InlineData(Title, null, "version")]                                               // version effacée : règles par version esquivées
    public void Search_results_contradicted_by_the_Microsoft_page_are_refused(string title, string? version, string? refused)
    {
        var problem = CatalogPackageCheck.CompareWithDetails(title, version, Details());
        if (refused is null) Assert.Null(problem);
        else Assert.Contains(refused, problem);
    }

    [Fact]
    public async Task Unreachable_details_block_the_entry_only_when_searches_go_through_the_console()
    {
        Task<CatalogEntryDetails?> Unreachable(string id, CancellationToken ct) => Task.FromResult<CatalogEntryDetails?>(null);
        var relayed = await CatalogPackageCheck.ConfirmEntryAsync(UpdateId, Title, "2.1.5.10", default, Unreachable, detailsRequired: true);
        Assert.Contains("invérifiables", relayed.Refusal);
        var direct = await CatalogPackageCheck.ConfirmEntryAsync(UpdateId, Title, "2.1.5.10", default, Unreachable, detailsRequired: false);
        Assert.Null(direct.Refusal);

        Task<CatalogEntryDetails?> Genuine(string id, CancellationToken ct) => Task.FromResult<CatalogEntryDetails?>(Details());
        Assert.Null((await CatalogPackageCheck.ConfirmEntryAsync(UpdateId, Title, "2.1.5.10", default, Genuine, detailsRequired: true)).Refusal);
        Assert.Contains("version", (await CatalogPackageCheck.ConfirmEntryAsync(UpdateId, Title, "9.9.9.9", default, Genuine, detailsRequired: false)).Refusal);
    }

    // ------------------------------------------------------------------ INF du paquet

    [Fact]
    public void Inf_with_the_announced_version_and_vendor_is_kept()
    {
        var inf = Inf("Intel", "12/28/2025,2.1.5.10");
        var (kept, reason) = CatalogPackageCheck.CheckInfs([inf], Title, "2.1.5.10", Details());
        Assert.Equal(inf, Assert.Single(kept));
        Assert.Null(reason);
        // Sans fiche (recherche directe, fiche injoignable) : le titre seul désigne l'éditeur.
        Assert.Single(CatalogPackageCheck.CheckInfs([inf], Title, "2.1.5.10", null).Infs);
    }

    [Fact]
    public void Older_package_announced_with_another_version_is_refused()
    {
        // Console compromise : l'entrée d'un ancien pilote vulnérable (2.1.3.3) présentée comme 2.1.5.10.
        var (kept, reason) = CatalogPackageCheck.CheckInfs([Inf("Intel", "05/02/2023,2.1.3.3")], Title, "2.1.5.10", null);
        Assert.Empty(kept);
        Assert.Contains("version de l'INF 2.1.3.3 différente de la version annoncée 2.1.5.10", reason);
        Assert.Empty(CatalogPackageCheck.CheckInfs([Inf("Intel", "05/02/2023")], Title, "2.1.5.10", null).Infs); // DriverVer sans version
    }

    [Fact]
    public void Inf_from_an_unrelated_vendor_is_refused()
    {
        var (kept, reason) = CatalogPackageCheck.CheckInfs([Inf("Contoso", "12/28/2025,2.1.5.10", "Contoso")], Title, "2.1.5.10", Details());
        Assert.Empty(kept);
        Assert.Contains("Contoso", reason);
    }

    [Theory]
    [InlineData("Realtek Semiconductor Corp. - Net - 10.79.50.1003", "Realtek", "Realtek", null)]
    [InlineData("AMD - Display - 31.0.21921.1000", "Advanced Micro Devices, Inc.", "AMD", null)]       // AMD = Advanced Micro Devices
    [InlineData("HP - Printer - 61.310.1.25919", "Hewlett-Packard", "HP", null)]
    [InlineData("Conexant - MEDIA - 8.66.95.69", "Synaptics", "Conexant", null)]                       // fabricant des modèles
    [InlineData("Wistron Corporation - Net", "Intel", "Intel", "Intel")]                                // fiche : fournisseur Intel
    public void Vendor_spellings_of_real_catalog_entries_are_related(string title, string provider, string manufacturer, string? detailsProvider)
    {
        var details = detailsProvider is null ? null : new CatalogEntryDetails(UpdateId, title, "1.0", detailsProvider, detailsProvider);
        Assert.Single(CatalogPackageCheck.CheckInfs([Inf(provider, "01/01/2026,1.0.0.0", manufacturer)], title, "1.0", details).Infs);
    }

    [Fact]
    public void Only_the_matching_infs_of_a_package_are_kept()
    {
        var good = Inf("Intel", "12/28/2025,2.1.5.10");
        var stale = Inf("Intel", "05/02/2023,2.1.3.3");
        var (kept, _) = CatalogPackageCheck.CheckInfs([stale, good], Title, "2.1.5.10", Details());
        Assert.Equal(good, Assert.Single(kept));
    }

    [Fact]
    public void Entry_without_version_is_checked_for_its_vendor_only()
    {
        Assert.Single(CatalogPackageCheck.CheckInfs([Inf("Intel", "12/28/2025,2.1.5.10")], "Intel - Net", null, null).Infs);
        Assert.Empty(CatalogPackageCheck.CheckInfs([Inf("Contoso", "12/28/2025,2.1.5.10", "Contoso")], "Intel - Net", null, null).Infs);
    }
}
