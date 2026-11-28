using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Rollout;
using SpokaPilote.Server.Storage;
using Legacy = SpokaPilote.Server.Tests.Legacy31;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>
/// Console 4.0.1 : identité d'une proposition au-delà de « source + titre + version ». (1) Une proposition SANS numéro de
/// version lisible (titre Windows Update sans version finale, « n/a » du catalogue) est suivie par son identité stable
/// (UpdateID...) : retenue par les anneaux, suspendable, comptée par le disjoncteur. (2) Une suspension suit le même pilote
/// proposé par une autre source sous un autre titre (même constructeur du matériel, même classe, même version).
/// </summary>
internal static class Same
{
    public const string IntelGpuHwid = @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028&REV_01";
    public const string NvidiaGpuHwid = @"PCI\VEN_10DE&DEV_25B8&SUBSYS_0B591028&REV_A1";

    public static Legacy.Models.DeviceInfo Device(string hwid, string className, string id = "1") => new()
    {
        InstanceId = $@"{hwid.Split('&')[0]}&DEV_X\{id}",
        Name = className == "Display" ? "Carte graphique" : "Périphérique",
        ClassName = className,
        HardwareIds = [hwid, string.Join('&', hwid.Split('&').Take(2))],
        CompatibleIds = [hwid.Split('&')[0] + "&CC_030000"],
    };

    /// <summary>Proposition lue par l'analyseur EXACT de la 3.1, visant un périphérique (critères hardwareId et category).</summary>
    public static Legacy.Models.DriverCandidate Legacy(Legacy.Models.DriverSourceKind source, string title, string? version, Legacy.Models.DeviceInfo? device,
        string kind = "Update", string? category = null)
    {
        var c = new Legacy.Models.DriverCandidate
        {
            Id = title + version, Source = source, Title = title, Version = version, Kind = Enum.Parse<Legacy.Models.CandidateKind>(kind), Category = category,
        };
        if (device is not null) c.TargetDeviceIds.Add(device.InstanceId);
        return c;
    }

    public static RolloutVersion Versionless(long id, string title, string item, DateTimeOffset firstSeen, string source = "WindowsUpdate", bool suspended = false) => new()
    {
        Id = id, Source = source, Title = title, Version = "", ItemKey = item, Family = RolloutKeys.FamilyPattern(title), Kind = "Update",
        FirstSeen = firstSeen, LastSeen = firstSeen, Suspended = suspended, SuspendAuto = suspended,
        SuspendReason = suspended ? "suspendu sur tout le parc par alice : imprimantes bloquées" : null,
    };

    public static RolloutVersion Hardware(long id, string source, string title, string version, string vendor, string? deviceClass, DateTimeOffset firstSeen,
        bool suspended = true) => new()
    {
        Id = id, Source = source, Title = title, Version = version, Family = RolloutKeys.FamilyPattern(title), Kind = "Update", FirstSeen = firstSeen,
        LastSeen = firstSeen, Suspended = suspended, SuspendAuto = suspended, HardwareVendor = vendor, DeviceClass = deviceClass,
        SuspendReason = suspended ? "suspendu automatiquement : 3 échecs sur 7 installations dans l'anneau « Pilote » en 48 h" : null,
    };
}

public sealed class RolloutIdentityKeysTests
{
    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028&REV_01", @"PCI\VEN_8086")]
    [InlineData(@"pci\ven_10de&dev_25b8", @"PCI\VEN_10DE")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0257&SUBSYS_10280A1F&REV_1000", @"HDAUDIO\VEN_10EC")]
    [InlineData(@"USB\VID_8087&PID_0026&REV_0002", @"USB\VID_8087")]
    [InlineData(@"ACPI\VEN_INT&DEV_33A0", @"ACPI\VEN_INT")]
    [InlineData(@"ACPI\INT33A0", null)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{8c5e}", null)]
    [InlineData(@"PCI\VEN_80861&DEV_1", null)]
    public void The_chip_vendor_is_read_from_the_hardware_id(string hwid, string? vendor) => Assert.Equal(vendor, RolloutKeys.HardwareVendor(hwid));

    [Fact]
    public void Versions_compare_across_sources_including_the_two_nvidia_numberings()
    {
        Assert.Equal(RolloutKeys.CanonicalVersion(RolloutKeys.NvidiaVendor, "32.0.15.6636"), RolloutKeys.CanonicalVersion(RolloutKeys.NvidiaVendor, "566.36 (Windows x.x.15.6636)"));
        Assert.Equal(new Version(566, 36, 0, 0), RolloutKeys.CanonicalVersion(RolloutKeys.NvidiaVendor, "31.0.15.6636"));
        Assert.NotEqual(RolloutKeys.CanonicalVersion(@"PCI\VEN_8086", "32.0.15.6636"), RolloutKeys.CanonicalVersion(@"PCI\VEN_8086", "566.36"));
        Assert.Equal(new Version(31, 0, 101, 5590), RolloutKeys.CanonicalVersion(@"PCI\VEN_8086", "31.0.101.5590 (A12)"));
        Assert.Equal(["32.0.15.6636", "566.36.0.0"], RolloutKeys.RuleVersions(RolloutKeys.NvidiaVendor, "32.0.15.6636"));
        Assert.Equal(["566.36.0.0", "30.0.15.6636", "31.0.15.6636", "32.0.15.6636", "33.0.15.6636"], RolloutKeys.RuleVersions(RolloutKeys.NvidiaVendor, "566.36 (Windows x.x.15.6636)"));
        Assert.Equal(["31.0.101.5590"], RolloutKeys.RuleVersions(@"PCI\VEN_8086", "31.0.101.5590"));
    }

    [Fact]
    public void A_versionless_candidate_is_tracked_only_with_a_stable_identity()
    {
        Assert.False(RolloutKeys.IsTrackable("HP - Printer", null));
        Assert.False(RolloutKeys.IsTrackable("HP - Printer", "n/a", "  "));
        Assert.True(RolloutKeys.IsTrackable("HP - Printer", null, "update:6F1C2A8E-1D2B"));
        Assert.Equal("update:6f1c2a8e-1d2b", RolloutKeys.ItemKey(" update:6F1C2A8E-1D2B\n"));
        Assert.Equal(RolloutKeys.MaxItemKeyLength, RolloutKeys.ItemKey(new string('a', 500))!.Length);
        Assert.Equal("HP - Printer (sans numéro de version)", Same.Versionless(1, "HP - Printer", "update:x", RolloutSamples.T0).DisplayName);
    }
}

// ====================================================================== décisions (postes 4.0.1)

public sealed class RolloutIdentityDecisionTests
{
    private static readonly DateTimeOffset Now = RolloutSamples.T0;
    private static readonly PolicyTarget Pilot = new(RolloutSamples.Pilot, false);
    private static readonly PolicyTarget Early = new(RolloutSamples.Early, false);
    private static readonly PolicyTarget General = new(RolloutSamples.General, false);
    private static readonly PolicyTarget Cautious = new(null, true);

    private static RolloutIndex Index(params RolloutVersion[] versions)
    {
        var s = RolloutSamples.Snapshot(BigFleet.Rings, versions);
        return new RolloutIndex(s, RolloutPlanner.Plan(s, Now), Now);
    }

    private static RolloutDecision D(RolloutIndex index, PolicyTarget target, string source, string title, string? version, string? item = null,
        string? vendor = null, string? deviceClass = null, string kind = "Update") =>
        index.Decide(new CandidateQuery(source, title, version, kind, item, vendor, deviceClass), target, TimeZoneInfo.Utc, Now);

    [Fact]
    public void A_versionless_driver_is_held_by_its_identity_suspended_and_new_ones_are_retained()
    {
        var index = Index(
            Same.Versionless(1, "HP - Printer", "update:aaaa", Now.AddDays(-4)),
            Same.Versionless(2, "Canon - Imaging", "update:bbbb", Now.AddDays(-30)),
            Same.Versionless(3, "Realtek - Extension", "update:cccc", Now.AddDays(-60), suspended: true));

        // Connue, vue il y a 4 jours : retenue pour « Général » (10 jours) avec sa date, autorisée pour « Précoce » (3 jours).
        var held = D(index, General, "WindowsUpdate", "HP - Printer", null, "update:aaaa");
        Assert.Equal(DecisionStatus.Held, held.Status);
        Assert.Equal(Now.AddDays(6), held.AvailableAt);
        Assert.Contains("HP - Printer (sans numéro de version)", held.Reason);
        Assert.Equal(DecisionStatus.Allowed, D(index, Early, "WindowsUpdate", "HP - Printer", null, "update:aaaa").Status);
        Assert.Equal(DecisionStatus.Held, D(index, Cautious, "WindowsUpdate", "HP - Printer", "n/a", "update:aaaa").Status);
        // Même UpdateID proposé par le catalogue Microsoft (autre titre) : même proposition.
        Assert.Equal(DecisionStatus.Held, D(index, General, "MicrosoftCatalog", "HP Inc. - Printer", null, "UPDATE:AAAA").Status);
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "WindowsUpdate", "Canon - Imaging", null, "update:bbbb").Status);

        // Jamais vue : retenue dans les anneaux à délai, autorisée dans l'anneau pilote.
        var fresh = D(index, General, "WindowsUpdate", "HP - Printer", null, "update:dddd");
        Assert.Equal(DecisionStatus.Unknown, fresh.Status);
        Assert.Contains("nouveau pilote, pas encore vu par la console", fresh.Reason);
        Assert.Equal(DecisionStatus.Allowed, D(index, Pilot, "WindowsUpdate", "HP - Printer", null, "update:dddd").Status);

        // Suspendue : tous les anneaux, pilotes manquants compris ; même titre sans version, autre identité : suspendue aussi
        // (comme la règle des postes 3.x, qui ne voient que le titre).
        Assert.Equal(DecisionStatus.Suspended, D(index, Pilot, "WindowsUpdate", "Realtek - Extension", null, "update:cccc", kind: "MissingDriver").Status);
        Assert.Equal(DecisionStatus.Suspended, D(index, Pilot, "WindowsUpdate", "realtek - extension", null, "update:eeee").Status);
        Assert.Equal(DecisionStatus.Allowed, D(index, Pilot, "Dell", "Realtek - Extension", null, "dell:x1").Status);

        // Poste 4.0.0 (sans identité) : comportement précédent, non soumise.
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "WindowsUpdate", "HP - Printer", null).Status);
        // Pilote manquant non soumis (réglage par défaut).
        Assert.Equal(DecisionStatus.Allowed, D(index, General, "WindowsUpdate", "HP - Printer", null, "update:aaaa", kind: "MissingDriver").Status);
    }

    /// <summary>Intel Iris Xe 31.0.101.5590 suspendu pour le paquet Dell : la même version reste bloquée quelle que soit la source et le titre.</summary>
    [Fact]
    public void A_suspension_follows_the_same_driver_version_from_every_source()
    {
        const string intel = @"PCI\VEN_8086";
        var index = Index(
            Same.Hardware(1, "Dell", "Intel UHD Graphics Driver", "31.0.101.5590 (A12)", intel, "Display", Now.AddDays(-40)),
            Same.Hardware(2, "WindowsUpdate", "NVIDIA - Display - 32.0.15.6636", "32.0.15.6636", RolloutKeys.NvidiaVendor, "Display", Now.AddDays(-40)),
            Same.Hardware(3, "WindowsUpdate", "Intel - Display - 31.0.101.5000", "31.0.101.5000", intel, "Display", Now.AddDays(-40), suspended: false));

        RolloutDecision Q(string source, string title, string version, string? vendor = intel, string? cls = "Display") =>
            D(index, Pilot, source, title, version, vendor: vendor, deviceClass: cls);

        foreach (var (source, title, version) in new[]
        {
            ("WindowsUpdate", "Intel - Display - 31.0.101.5590", "31.0.101.5590"),
            ("MicrosoftCatalog", "Intel Corporation - Display - 31.0.101.5590", "31.0.101.5590"),
            ("HP", "Intel UHD Graphics Driver", "31.0.101.5590 Rev.A"),
            ("Lenovo", "Intel UHD Graphics Driver for Windows 11 (64-bit) - ThinkPad T14 Gen 2", "31.0.101.5590"),
            ("LocalRepository", "iigd_dch.inf", "31.0.101.5590"),
        })
        {
            var d = Q(source, title, version);
            Assert.True(d.Status == DecisionStatus.Suspended, $"{source} « {title} » : {d.Status}");
            Assert.Contains("même pilote, proposé par Dell", d.Reason);
            Assert.Equal(RolloutPolicyBuilder.SuspendedHardwarePrefix + 1, d.Rule);
        }
        // Classe inconnue d'un côté (pilote manquant sans classe) : même pilote.
        Assert.Equal(DecisionStatus.Suspended, Q("MicrosoftCatalog", "Intel Corporation - Display - 31.0.101.5590", "31.0.101.5590", cls: null).Status);
        // Autre version, autre constructeur avec le même numéro, autre classe, matériel inconnu (poste 4.0.0) : pas cette suspension.
        Assert.Equal(DecisionStatus.Allowed, Q("WindowsUpdate", "Intel - Display - 31.0.101.5592", "31.0.101.5592").Status);
        Assert.Equal(DecisionStatus.Allowed, Q("WindowsUpdate", "AMD - Display - 31.0.101.5590", "31.0.101.5590", vendor: @"PCI\VEN_1002").Status);
        Assert.Equal(DecisionStatus.Allowed, Q("WindowsUpdate", "Intel - System - 31.0.101.5590", "31.0.101.5590", cls: "System").Status);
        Assert.Equal(DecisionStatus.Allowed, Q("WindowsUpdate", "Intel - Display - 31.0.101.5590", "31.0.101.5590", vendor: null).Status);
        Assert.Equal(DecisionStatus.Allowed, Q("WindowsUpdate", "Intel - Display - 31.0.101.5000", "31.0.101.5000").Status);

        // NVIDIA : suspendue sous sa version Windows, retrouvée sous le numéro commercial du site NVIDIA et chez Dell.
        Assert.Equal(DecisionStatus.Suspended, Q("Nvidia", "NVIDIA RTX / Quadro 566.36", "566.36 (Windows x.x.15.6636)", RolloutKeys.NvidiaVendor).Status);
        Assert.Equal(DecisionStatus.Suspended, Q("Dell", "NVIDIA RTX A2000 Graphics Driver", "32.0.15.6636 (A05)", RolloutKeys.NvidiaVendor).Status);
        Assert.Equal(DecisionStatus.Allowed, Q("Nvidia", "NVIDIA RTX / Quadro 566.45", "566.45 (Windows x.x.15.6645)", RolloutKeys.NvidiaVendor).Status);
    }
}

// ====================================================================== document des postes 3.x (analyseur exact de la 3.1)

public sealed class RolloutIdentityLegacyPolicyTests
{
    private static readonly DateTimeOffset Now = RolloutSamples.T0;
    private static readonly PolicyTarget Cautious = new(null, true);
    private static readonly PolicyTarget General = new(RolloutSamples.General, false);

    private static Legacy.Policy.DriverPolicy Served(PolicyTarget target, RolloutLimits? limits, params RolloutVersion[] versions)
    {
        var s = RolloutSamples.Snapshot(BigFleet.Rings, versions);
        var plans = RolloutPlanner.Plan(s, Now);
        var rules = RolloutPolicyBuilder.Build(s, plans, target, Now, TimeZoneInfo.Utc, limits);
        return BigFleet.Parse31(RolloutPolicyBuilder.Compose(null, rules, null)!);
    }

    [Fact]
    public void A_31_client_holds_and_suspends_versionless_drivers_by_title()
    {
        var p31 = Served(Cautious, null,
            Same.Versionless(1, "HP - Printer", "update:aaaa", Now.AddDays(-4)),
            Same.Versionless(2, "Canon - Imaging", "update:bbbb", Now.AddDays(-30)),
            Same.Versionless(3, "Realtek - Extension", "update:cccc", Now.AddDays(-60), suspended: true),
            RolloutSamples.Version(4, "31.0.101.5590", Now.AddDays(-30)));
        var at = Now.UtcDateTime.ToLocalTime();
        bool Blocked(string title, string kind = "Update", Legacy.Models.DriverSourceKind source = Legacy.Models.DriverSourceKind.WindowsUpdate) =>
            p31.Evaluate(Same.Legacy(source, title, null, null, kind), null, at).IsBlocked;

        Assert.True(Blocked("HP - Printer"));                  // en cours de diffusion
        Assert.True(Blocked("HP - Printer", "Optional"));
        Assert.False(Blocked("HP - Printer", "MissingDriver")); // pilotes manquants non soumis
        Assert.False(Blocked("Canon - Imaging"));               // diffusion terminée
        Assert.True(Blocked("Realtek - Extension", "MissingDriver")); // suspendue : tous types
        Assert.False(Blocked("Realtek - Extension", source: Legacy.Models.DriverSourceKind.Dell));
        // La retenue par titre ne touche pas les versions ordinaires.
        Assert.False(p31.Evaluate(Same.Legacy(Legacy.Models.DriverSourceKind.WindowsUpdate, "Intel - Display - 31.0.101.5590", "31.0.101.5590", null), null, at).IsBlocked);

        // Anneau identifié (poste 4.0 sans décisions) : retenue datée de la proposition sans version, vue hier, délai de 3 jours.
        var early = Served(new PolicyTarget(RolloutSamples.Early, false), null, Same.Versionless(1, "HP - Printer", "update:aaaa", Now.AddDays(-1)));
        Assert.True(early.Evaluate(Same.Legacy(Legacy.Models.DriverSourceKind.WindowsUpdate, "HP - Printer", null, null), null, at).IsBlocked);
    }

    [Fact]
    public void A_31_client_blocks_a_suspended_version_proposed_by_any_source_for_the_same_hardware()
    {
        var gpu = Same.Device(Same.IntelGpuHwid, "Display");
        var nvidia = Same.Device(Same.NvidiaGpuHwid, "Display", "2");
        var otherIntel = Same.Device(@"PCI\VEN_8086&DEV_A0C8&SUBSYS_0A1F1028&REV_20", "System", "3");
        Legacy.Models.DeviceInfo[] devices = [gpu, nvidia, otherIntel];
        var versions = new[]
        {
            Same.Hardware(1, "Dell", "Intel UHD Graphics Driver", "31.0.101.5590 (A12)", @"PCI\VEN_8086", "Display", Now.AddDays(-40)),
            Same.Hardware(2, "Nvidia", "NVIDIA RTX / Quadro 566.36", "566.36 (Windows x.x.15.6636)", RolloutKeys.NvidiaVendor, "Display", Now.AddDays(-40)),
        };
        var at = Now.UtcDateTime.ToLocalTime();
        foreach (var limits in new RolloutLimits?[] { null, new(4, 1024 * 1024) }) // détaillé, puis regroupé (très nombreuses suspensions)
        {
            var p31 = Served(General, limits, versions);
            bool Blocked(Legacy.Models.DriverSourceKind source, string title, string version, Legacy.Models.DeviceInfo device, string? category = null) =>
                p31.Evaluate(Same.Legacy(source, title, version, device, category: category), devices, at).IsBlocked;

            Assert.True(Blocked(Legacy.Models.DriverSourceKind.WindowsUpdate, "Intel - Display - 31.0.101.5590", "31.0.101.5590", gpu));
            Assert.True(Blocked(Legacy.Models.DriverSourceKind.MicrosoftCatalog, "Intel Corporation - Display - 31.0.101.5590", "31.0.101.5590", gpu, "Drivers (Video)"));
            Assert.True(Blocked(Legacy.Models.DriverSourceKind.HP, "Intel UHD Graphics Driver", "31.0.101.5590 Rev.A", gpu, "Driver - Graphics"));
            Assert.True(Blocked(Legacy.Models.DriverSourceKind.Lenovo, "Intel UHD Graphics Driver for Windows 11 (64-bit)", "31.0.101.5590", gpu, "Display and Video Graphics"));
            Assert.True(Blocked(Legacy.Models.DriverSourceKind.WindowsUpdate, "NVIDIA - Display - 32.0.15.6636", "32.0.15.6636", nvidia));
            Assert.True(Blocked(Legacy.Models.DriverSourceKind.Dell, "NVIDIA RTX A2000 Graphics Driver", "31.0.15.6636 (A05)", nvidia));
            // Autre version, autre matériel (même constructeur, autre classe ; même numéro chez un autre constructeur).
            Assert.False(Blocked(Legacy.Models.DriverSourceKind.WindowsUpdate, "Intel - Display - 31.0.101.5592", "31.0.101.5592", gpu));
            Assert.False(Blocked(Legacy.Models.DriverSourceKind.WindowsUpdate, "Intel - System - 31.0.101.5590", "31.0.101.5590", otherIntel));
            Assert.False(Blocked(Legacy.Models.DriverSourceKind.WindowsUpdate, "NVIDIA - Display - 31.0.101.5590", "31.0.101.5590", nvidia));
        }
    }
}

// ====================================================================== bout en bout : rapports, décisions, console, disjoncteur, migration

public sealed class RolloutIdentityApiTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public RolloutIdentityApiTests()
    {
        var rollout = _host.Factory.Services.GetRequiredService<RolloutService>();
        rollout.MinRebuildInterval = TimeSpan.Zero;
        rollout.Zone = TimeZoneInfo.Utc;
    }

    public void Dispose() => _host.Dispose();

    private const string Printer = "HP - Printer";
    private const string PrinterItem = "update:6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab";

    /// <summary>Rapport d'un poste 4.0.1 : propositions (avec identité et périphérique visé) et résultats d'installation.</summary>
    private static string Report(string name, int machine, DateTimeOffset at, IEnumerable<JsonObject> candidates, IEnumerable<JsonObject>? outcomes = null)
    {
        var root = new JsonObject
        {
            ["schema"] = 2, ["appVersion"] = "4.0.1", ["timestamp"] = at.ToString("O"),
            ["system"] = new JsonObject { ["computerName"] = name, ["machineId"] = RolloutReports.MachineId(machine), ["manufacturer"] = "Dell Inc.", ["model"] = "Latitude 7440" },
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)c).ToArray()),
            ["devices"] = new JsonArray(
                new JsonObject
                {
                    ["instanceId"] = @"PCI\VEN_8086&DEV_9A49\3&11583659&0&10", ["name"] = "Intel(R) UHD Graphics", ["className"] = "Display", ["errorCode"] = 0, ["isPresent"] = true,
                    ["hardwareIds"] = new JsonArray(Same.IntelGpuHwid, @"PCI\VEN_8086&DEV_9A49"),
                },
                new JsonObject
                {
                    ["instanceId"] = @"USBPRINT\HPLASERJET\7&1", ["name"] = "HP LaserJet", ["className"] = "Printer", ["errorCode"] = 0, ["isPresent"] = true,
                    ["hardwareIds"] = new JsonArray(@"USBPRINT\HPLaserJet_M40AFE6"),
                }),
        };
        if (outcomes is not null) root["installation"] = new JsonArray(outcomes.Select(o => (JsonNode)o).ToArray());
        return root.ToJsonString();
    }

    private static JsonObject Printer401(string? item = PrinterItem) => new()
    {
        ["title"] = Printer, ["source"] = "Windows Update", ["kind"] = "Mise à jour", ["itemId"] = item, ["deviceIds"] = new JsonArray(@"USBPRINT\HPLASERJET\7&1"),
    };

    private static JsonObject DellIntel() => new()
    {
        ["title"] = "Intel UHD Graphics Driver", ["version"] = "31.0.101.5590 (A12)", ["source"] = "Dell (catalogue officiel)", ["kind"] = "Mise à jour",
        ["deviceIds"] = new JsonArray(@"PCI\VEN_8086&DEV_9A49\3&11583659&0&10"),
    };

    private async Task Upload(string json)
    {
        var r = await _host.Client(AgentToken).PostAsync("/api/reports", TestServerHost.Json(json));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task<JsonElement[]> Decide(int machine, string name, params object[] candidates)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/rollout/decide") { Content = TestServerHost.Json(new { candidates }) };
        req.Headers.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(machine));
        req.Headers.Add("X-SpokaPilote-Machine", name);
        var r = await _host.Client(AgentToken).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await ReadJson(r)).GetProperty("decisions").EnumerateArray().ToArray();
    }

    private async Task<JsonElement> Version(string query)
    {
        var list = await ReadJson(await _host.Client(AdminToken).GetAsync($"/api/rollout/versions?status=all&q={Uri.EscapeDataString(query)}"));
        return list.GetProperty("items").EnumerateArray().Single();
    }

    private async Task SetupAsync()
    {
        var admin = _host.Client(AdminToken);
        await Upload(Report("PC-IT-01", 1, _host.Clock.Now, []));
        await Upload(Report("PC-COMPTA-01", 3, _host.Clock.Now, []));
        var group = await ReadJson(await admin.PostAsync("/api/groups", TestServerHost.Json(new
        {
            name = "Informatique", rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-IT-" } } },
        })));
        var rings = (await ReadJson(await admin.PostAsync("/api/rollout/rings/suggested", null))).GetProperty("rings").EnumerateArray().ToList();
        var pilot = rings[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/rollout/rings/{pilot}", TestServerHost.Json(new { name = "Pilote", delayDays = 0, groups = new[] { group.GetProperty("id").GetInt64() } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/rollout/settings", TestServerHost.Json(new { enabled = true, breaker = new { enabled = true, minFailures = 3, minFailureRatePercent = 20, windowHours = 48 } }))).StatusCode);
    }

    [Fact]
    public async Task A_versionless_driver_is_tracked_held_suspended_from_the_console_and_tripped_by_the_breaker()
    {
        await SetupAsync();
        var item = new { source = "WindowsUpdate", title = Printer, kind = "Update", itemId = PrinterItem };
        // Jamais vue : retenue pour « Général », autorisée pour le pilote.
        Assert.Equal("unknown", (await Decide(3, "PC-COMPTA-01", item))[0].GetProperty("status").GetString());
        Assert.Equal("allowed", (await Decide(1, "PC-IT-01", item))[0].GetProperty("status").GetString());

        // Un rapport 4.0.1 la signale : connue (sans version), retenue 10 jours pour « Général ».
        await Upload(Report("PC-IT-01", 1, _host.Clock.Now, [Printer401()]));
        var v = await Version(Printer);
        Assert.Equal("HP - Printer (sans numéro de version)", v.GetProperty("driver").GetString());
        Assert.Equal("", v.GetProperty("version").GetString());
        Assert.Equal(PrinterItem, v.GetProperty("itemKey").GetString());
        var held = (await Decide(3, "PC-COMPTA-01", item))[0];
        Assert.Equal("held", held.GetProperty("status").GetString());
        Assert.Equal(_host.Clock.Now.AddDays(10), held.GetProperty("availableAt").GetDateTimeOffset());
        // Poste 3.x (anneau le plus prudent) : retenue par titre dans sa politique.
        var legacy = BigFleet.Parse31(await (await _host.Client(AgentToken).GetAsync("/api/policy")).Content.ReadAsStringAsync());
        Assert.True(legacy.Evaluate(Same.Legacy(Legacy.Models.DriverSourceKind.WindowsUpdate, Printer, null, null), null, DateTime.Now).IsBlocked);

        // Disjoncteur : trois postes pilotes en échec (résultats rattachés par l'identité, sans version).
        var failed = new JsonObject { ["title"] = Printer, ["status"] = "Failed", ["message"] = "0x80240022", ["itemId"] = PrinterItem };
        for (var i = 1; i <= 3; i++) await Upload(Report($"PC-IT-0{i}", i, _host.Clock.Now, [Printer401()], [(JsonObject)failed.DeepClone()]));
        v = await Version(Printer);
        Assert.Equal("suspended", v.GetProperty("status").GetString());
        Assert.StartsWith("suspendu automatiquement : 3 échecs sur 3 installations", v.GetProperty("suspension").GetProperty("reason").GetString());
        var suspended = (await Decide(1, "PC-IT-01", item, new { source = "MicrosoftCatalog", title = "HP Inc. - Printer", kind = "MissingDriver", itemId = PrinterItem.ToUpperInvariant() }));
        Assert.All(suspended, d => Assert.Equal("suspended", d.GetProperty("status").GetString()));
        legacy = BigFleet.Parse31(await (await _host.Client(AgentToken).GetAsync("/api/policy")).Content.ReadAsStringAsync());
        Assert.True(legacy.Evaluate(Same.Legacy(Legacy.Models.DriverSourceKind.WindowsUpdate, Printer, null, null, "MissingDriver"), null, DateTime.Now).IsBlocked);

        // Reprise depuis la console, puis suspension manuelle : la console agit sur la proposition sans version comme sur une autre.
        var admin = _host.Client(AdminToken);
        var id = v.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{id}/resume", TestServerHost.Json(new { comment = "pilote corrigé" }))).StatusCode);
        Assert.Equal("allowed", (await Decide(1, "PC-IT-01", item))[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{id}/suspend", TestServerHost.Json(new { reason = "file d'impression bloquée" }))).StatusCode);
        Assert.Contains("file d'impression bloquée", (await Decide(1, "PC-IT-01", item))[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_suspension_of_the_dell_package_blocks_the_same_version_from_the_catalog_for_every_client_generation()
    {
        await SetupAsync();
        // Rapport d'un poste 3.x (sans identité) : le matériel visé est déduit des périphériques du rapport.
        await Upload(Report("PC-IT-01", 1, _host.Clock.Now, [DellIntel()]));
        var v = await Version("Intel UHD Graphics Driver");
        Assert.Equal(@"PCI\VEN_8086", v.GetProperty("hardwareVendor").GetString());
        Assert.Equal("Display", v.GetProperty("deviceClass").GetString());
        var admin = _host.Client(AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{v.GetProperty("id").GetInt64()}/suspend", TestServerHost.Json(new { reason = "écrans noirs" }))).StatusCode);

        // Poste 4.0.1 : même version, catalogue Microsoft, autre titre.
        var catalog = new { source = "MicrosoftCatalog", title = "Intel Corporation - Display - 31.0.101.5590", version = "31.0.101.5590", kind = "Update",
            hardwareVendor = @"PCI\VEN_8086", deviceClass = "Display" };
        var d = (await Decide(1, "PC-IT-01", catalog))[0];
        Assert.Equal("suspended", d.GetProperty("status").GetString());
        Assert.Contains("écrans noirs", d.GetProperty("reason").GetString());
        Assert.Contains("Dell", d.GetProperty("reason").GetString());

        // Postes 3.x et 4.0.0 : règle « même pilote » (constructeur + classe + version) dans la politique, comprise par la 3.1.
        foreach (var decisions in new[] { false, true })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/policy");
            if (decisions)
            {
                req.Headers.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(1));
                req.Headers.Add("X-SpokaPilote-Rollout", "1");
                req.Headers.Add("X-SpokaPilote-Rollout-Decisions", "1");
            }
            var json = await (await _host.Client(AgentToken).SendAsync(req)).Content.ReadAsStringAsync();
            var ids = JsonNode.Parse(json)!["rules"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToList();
            Assert.Contains(RolloutPolicyBuilder.SuspendedHardwarePrefix + v.GetProperty("id").GetInt64(), ids);
            if (decisions) continue; // la section « rollout » est inconnue des postes 3.x : document sans section seulement
            var p31 = BigFleet.Parse31(json);
            var gpu = Same.Device(Same.IntelGpuHwid, "Display");
            var blocked = p31.Evaluate(Same.Legacy(Legacy.Models.DriverSourceKind.MicrosoftCatalog, catalog.title, catalog.version, gpu), [gpu], DateTime.Now);
            Assert.True(blocked.IsBlocked);
            Assert.StartsWith(RolloutPolicyBuilder.SuspendedHardwarePrefix, blocked.Rule!.Id);
            Assert.Contains("écrans noirs", blocked.Reason);
        }
    }

    [Fact]
    public void A_40_database_is_migrated_to_the_new_identity_keeping_ids_outcomes_and_suspensions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "sp-server-mig40-" + Guid.NewGuid().ToString("N"));
        try { Migrate(dataDir); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { /* temporaire */ }
        }
    }

    private static void Migrate(string dataDir)
    {
        var store = new ServerStore(new ServerOptions { DataDirectory = dataDir });
        var path = store.DatabasePath;
        var now = RolloutSamples.T0;
        var json = RolloutReports.Json("PC-01", 1, now, [new("Intel - Display - 31.0.101.5590", "31.0.101.5590")],
            [new("Intel - Display - 31.0.101.5590", "31.0.101.5590", "Failed")]);
        store.SaveReport(ReportParser.Parse(json), json, now);
        var before = Assert.Single(store.LoadRolloutSnapshot(now).Versions);
        Assert.True(store.SuspendVersion(before.Id, automatic: false, "écrans bleus", "alice", now));
        SqliteConnection.ClearAllPools();

        // Base remise dans la forme exacte de la console 4.0 (table des versions sans identité, schéma 5).
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                PRAGMA foreign_keys = OFF;
                CREATE TABLE rollout_versions_v5 (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, title TEXT NOT NULL, version TEXT NOT NULL, family TEXT NOT NULL,
                    kind TEXT, category TEXT, first_seen TEXT NOT NULL, first_machine TEXT, last_seen TEXT NOT NULL, suspended INTEGER NOT NULL DEFAULT 0,
                    suspend_auto INTEGER NOT NULL DEFAULT 0, suspend_reason TEXT, suspended_at TEXT, suspended_by TEXT, acknowledged_at TEXT,
                    acknowledged_by TEXT, resumed_at TEXT, resumed_by TEXT, approved_at TEXT, approved_by TEXT, UNIQUE (source, title, version));
                INSERT INTO rollout_versions_v5 SELECT id, source, title, version, family, kind, category, first_seen, first_machine, last_seen, suspended,
                    suspend_auto, suspend_reason, suspended_at, suspended_by, acknowledged_at, acknowledged_by, resumed_at, resumed_by, approved_at, approved_by
                    FROM rollout_versions;
                DROP TABLE rollout_versions;
                ALTER TABLE rollout_versions_v5 RENAME TO rollout_versions;
                CREATE INDEX ix_rollout_versions_title ON rollout_versions(title, version);
                UPDATE sqlite_sequence SET seq = 41 WHERE name = 'rollout_versions';
                PRAGMA user_version = 5;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var migrated = new ServerStore(new ServerOptions { DataDirectory = dataDir });
        Assert.Equal(5, migrated.OpenedSchemaVersion);
        var after = Assert.Single(migrated.LoadRolloutSnapshot(now).Versions);
        Assert.Equal(before.Id, after.Id);
        Assert.True(after.Suspended);
        Assert.Equal("écrans bleus", after.SuspendReason);
        Assert.Equal("", after.ItemKey);
        Assert.Single(migrated.GetRolloutFailures(before.Id)); // installations rattachées au même identifiant

        // Deux propositions sans version de même titre et de même source, identités différentes : deux lignes ; la séquence est conservée.
        foreach (var (machine, item) in new[] { (2, "update:aaaa"), (3, "update:bbbb") })
        {
            var r = Report($"PC-0{machine}", machine, now, [Printer401(item)]);
            migrated.SaveReport(ReportParser.Parse(r), r, now);
        }
        var versionless = migrated.LoadRolloutSnapshot(now).Versions.Where(x => x.IsVersionless).OrderBy(x => x.Id).ToList();
        Assert.Equal(["update:aaaa", "update:bbbb"], versionless.Select(x => x.ItemKey));
        Assert.True(versionless[0].Id > 41);
        SqliteConnection.ClearAllPools();
        Assert.Equal(ServerStore.SchemaVersion, new ServerStore(new ServerOptions { DataDirectory = dataDir }).OpenedSchemaVersion);
    }
}
