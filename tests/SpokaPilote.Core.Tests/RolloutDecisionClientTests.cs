using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;
using Xunit.Abstractions;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Poste 4.0 face à une console en mode « décisions » : la politique ne porte que les suspensions, les retenues sont
/// demandées proposition par proposition (POST /api/rollout/decide) puis appliquées par l'analyse ; console injoignable →
/// dernières décisions connues, retenue prudente des autres ; console antérieure (sans décisions) → comportement précédent.
/// </summary>
[Collection(nameof(GlobalDataProtectionState))] // un test remplace DataProtection.Security
public sealed class RolloutDecisionClientTests : IDisposable
{
    private static readonly DateTimeOffset Available = new(2026, 12, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Local);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sp-decide-{Guid.NewGuid():N}");

    public RolloutDecisionClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temporaire */ }
    }

    /// <summary>Politique servie en mode décisions (anneau « Général ») : une suspension, une règle de l'organisation qui autorise Intel.</summary>
    private static string PolicyJson(bool decisions = true, bool holdUnknown = true) => $$"""
        {
          "schema": 1, "name": "Parc siège", "version": 7,
          "rules": [
            { "id": "anneau-suspendu-9", "action": "block", "source": "WindowsUpdate", "title": "Realtek - Audio - 6.0.9600.1", "versions": "6.0.9600.1",
              "reason": "Pilote Realtek - Audio - 6.0.9600.1 : suspendu automatiquement" },
            { "id": "urgence", "action": "allow", "title": "Intel - Display - *", "reason": "Autorisation de l'organisation" },
            { "id": "gel", "action": "block", "title": "Conexant*", "reason": "Gel audio" }
          ],
          "rollout": { "ring": "Général", "ringPosition": 3, "ringCount": 3, "delayDays": 10, "enabled": true, "conservative": false,
            "machineKnown": true, "holdUnknown": {{(holdUnknown ? "true" : "false")}}, "applyToMissingDrivers": false,
            {{(decisions ? "\"decisions\": true," : "")}} "generatedAt": "2026-11-26T09:00:00.0000000+00:00", "availability": {} }
        }
        """;

    /// <summary>Console simulée : GET /api/policy, POST /api/rollout/decide (décisions calculées par <paramref name="decide"/>).</summary>
    private sealed class FakeConsole(string policy, Func<JsonNode, JsonObject>? decide) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();
        public bool Down { get; set; }
        public bool DecideDown { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    await gz.CopyToAsync(ms, cancellationToken);
                    bytes = ms.ToArray();
                }
                body = Encoding.UTF8.GetString(bytes);
            }
            Requests.Add((request, body));
            if (Down) throw new HttpRequestException("Console injoignable");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/policy") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(policy) };
            if (path == "/api/rollout/decide")
            {
                if (DecideDown) throw new HttpRequestException("Console injoignable");
                if (decide is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(decide(JsonNode.Parse(body!)!).ToJsonString()) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Décisions de la console simulée selon le titre de la proposition.</summary>
    private static JsonObject Decide(JsonNode request) => new()
    {
        ["ring"] = "Général",
        ["decisions"] = new JsonArray(request["candidates"]!.AsArray().Select(c =>
        {
            var title = c!["title"]!.GetValue<string>();
            JsonObject d = title switch
            {
                "Intel - Display - 31.0.101.5600" => new() { ["status"] = "held", ["availableAt"] = Available.ToString("O"), ["reason"] = "Pilote … : disponible le 06/12/2026" },
                "Dell BIOS 1.24.0" => new() { ["status"] = "held", ["reason"] = "Pilote Dell BIOS 1.24.0 : disponible pour votre anneau « Général » après validation" },
                "Realtek - Audio - 6.0.9600.1" => new() { ["status"] = "suspended", ["reason"] = "Pilote Realtek - Audio - 6.0.9600.1 : suspendu automatiquement" },
                "Contoso - Camera - 2.4.0.1" => new() { ["status"] = "unknown", ["reason"] = "Pilote Contoso - Camera - 2.4.0.1 : nouveau pilote, pas encore vu par la console" },
                "Futur - Statut - 1.0" => new() { ["status"] = "quarantaine" }, // statut d'une console plus récente
                _ => new() { ["status"] = "allowed" },
            };
            return (JsonNode)d;
        }).ToArray()),
    };

    private PolicyLoader Loader(FakeConsole console) => new(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton" }, new HttpClient(console))
    {
        RefreshReferenceList = false,
        CacheFile = Path.Combine(_dir, "policy-cache.json"),
        ReferenceCacheFile = Path.Combine(_dir, "ref.json"),
        DecisionCacheFile = Path.Combine(_dir, "rollout-decisions.json"),
        Identity = () => ("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-COMPTA-01"),
    };

    private static DriverCandidate Cand(string title, string version, DriverSourceKind source = DriverSourceKind.WindowsUpdate,
        CandidateKind kind = CandidateKind.Update) => new() { Id = title, Source = source, Title = title, Version = version, Kind = kind };

    private static List<DriverCandidate> Candidates() =>
    [
        Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600"),
        Cand("Intel - Display - 31.0.101.5590", "31.0.101.5590"),
        Cand("Dell BIOS 1.24.0", "1.24.0", DriverSourceKind.Dell, CandidateKind.Firmware),
        Cand("Realtek - Audio - 6.0.9600.1", "6.0.9600.1", kind: CandidateKind.MissingDriver),
        Cand("Contoso - Camera - 2.4.0.1", "2.4.0.1"),
        Cand("Conexant Audio 1.0", "1.0"),
    ];

    private static async Task<(LoadedPolicy Policy, List<DriverCandidate> Merged)> ScanAsync(PolicyLoader loader, List<DriverCandidate> candidates)
    {
        var policy = await loader.LoadAsync();
        var (merged, _) = await DriverScanEngine.ApplyPolicyAsync(loader, policy, candidates, [], CancellationToken.None, Now);
        return (policy, merged);
    }

    [Fact]
    public async Task The_scan_asks_the_console_for_its_candidates_and_applies_each_decision()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        var candidates = Candidates();
        var alternative = Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600", DriverSourceKind.MicrosoftCatalog);
        candidates[1].Alternatives.Add(alternative);
        var (policy, merged) = await ScanAsync(Loader(console), candidates);

        // La politique est demandée en mode décisions, avec l'identité du poste.
        var get = console.Requests.First(r => r.Request.RequestUri!.AbsolutePath == "/api/policy").Request;
        Assert.Equal("1", get.Headers.GetValues("X-SpokaPilote-Rollout-Decisions").Single());
        Assert.Equal("1", get.Headers.GetValues("X-SpokaPilote-Rollout").Single());
        // Une seule demande de décisions, compressée, avec les propositions (et l'alternative) sous leur nom de source du poste.
        var (post, body) = console.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.Equal("Bearer", post.Headers.Authorization!.Scheme);
        Assert.Equal("PC-COMPTA-01", post.Headers.GetValues("X-SpokaPilote-Machine").Single());
        Assert.Equal("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", post.Headers.GetValues("X-SpokaPilote-Machine-Id").Single());
        Assert.Contains("gzip", post.Content!.Headers.ContentEncoding);
        var sent = JsonNode.Parse(body!)!["candidates"]!.AsArray();
        Assert.Equal(7, sent.Count);
        Assert.Contains(sent, c => c!["source"]!.GetValue<string>() == "MicrosoftCatalog");
        Assert.Contains(sent, c => c!["source"]!.GetValue<string>() == "Dell" && c["kind"]!.GetValue<string>() == "Firmware");

        DriverCandidate Get(string title) => merged.Single(c => c.Title == title && c.Source != DriverSourceKind.MicrosoftCatalog);
        // Retenue datée : motif à l'heure locale, l'« allow » de l'organisation ne la lève pas.
        var held = Get("Intel - Display - 31.0.101.5600");
        Assert.True(held.HeldByRollout);
        Assert.Equal(Available, held.RolloutAvailableAt);
        Assert.Equal("Général", held.RolloutRing);
        Assert.StartsWith("Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le ", held.BlockedReason);
        // Autorisée : la règle de l'organisation s'applique ensuite normalement (allow), l'alternative retenue est écartée.
        var allowed = Get("Intel - Display - 31.0.101.5590");
        Assert.Null(allowed.BlockedReason);
        Assert.Empty(allowed.Alternatives);
        // Retenue sans date, suspension (pilote manquant compris), version jamais vue.
        Assert.Contains("après validation", Get("Dell BIOS 1.24.0").BlockedReason);
        Assert.True(Get("Dell BIOS 1.24.0").HeldByRollout);
        Assert.Contains("suspendu automatiquement", Get("Realtek - Audio - 6.0.9600.1").BlockedReason);
        Assert.True(Get("Realtek - Audio - 6.0.9600.1").HeldByRollout);
        Assert.Contains("pas encore vu par la console", Get("Contoso - Camera - 2.4.0.1").BlockedReason);
        // Blocage de l'organisation : affiché comme tel.
        Assert.Equal("Gel audio (règle « gel »)", Get("Conexant Audio 1.0").BlockedReason);
        Assert.False(Get("Conexant Audio 1.0").HeldByRollout);
        Assert.Contains("décisions de la console : 5 proposition(s) retenue(s) sur 7", policy.Summary);
        Assert.Empty(policy.Messages);
    }

    [Fact]
    public async Task An_unknown_status_from_a_newer_console_is_a_hold_never_an_authorization()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        var (_, merged) = await ScanAsync(Loader(console), [Cand("Futur - Statut - 1.0", "1.0")]);
        Assert.True(merged.Single().HeldByRollout);
    }

    [Fact]
    public async Task Without_the_console_the_last_known_decisions_apply_and_new_candidates_are_held()
    {
        var console = new FakeConsole(PolicyJson(), Decide);
        await ScanAsync(Loader(console), Candidates()); // décisions mises en cache
        Assert.True(File.Exists(Path.Combine(_dir, "rollout-decisions.json")));

        // 1. Politique reçue, demande de décisions en échec : cache, et retenue prudente de la proposition jamais décidée.
        console.DecideDown = true;
        var fresh = Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700");
        var (policy, merged) = await ScanAsync(Loader(console), [.. Candidates(), fresh]);
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.Equal(Available, merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").RolloutAvailableAt);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason); // autorisée la dernière fois
        Assert.True(merged.Single(c => c.Title == fresh.Title).HeldByRollout);
        Assert.Contains("console injoignable", merged.Single(c => c.Title == fresh.Title).BlockedReason);
        Assert.Contains(policy.Messages, m => m.Contains("Dernières décisions connues appliquées à 6 proposition(s)") && m.Contains("1 autre(s) retenue(s)"));

        // 2. Console entièrement injoignable : politique ET décisions depuis le cache, sans nouvel essai réseau pour les décisions.
        console.Down = true;
        console.Requests.Clear();
        (policy, merged) = await ScanAsync(Loader(console), [.. Candidates(), fresh]);
        Assert.True(policy.FromCache);
        Assert.DoesNotContain(console.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.True(merged.Single(c => c.Title == "Realtek - Audio - 6.0.9600.1").HeldByRollout); // suspension : cache et règle
        Assert.True(merged.Single(c => c.Title == fresh.Title).HeldByRollout);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason);

        // 3. Sans aucun cache de décisions : tout ce qui est soumis aux anneaux est retenu (jamais « tout autoriser »).
        File.Delete(Path.Combine(_dir, "rollout-decisions.json"));
        (_, merged) = await ScanAsync(Loader(console), Candidates());
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").HeldByRollout);
        Assert.False(merged.Single(c => c.Title == "Realtek - Audio - 6.0.9600.1" && c.Kind == CandidateKind.MissingDriver).BlockedReason is null);
    }

    [Fact]
    public async Task Non_elevated_interface_shows_blocks_and_holds_from_the_policy_received_by_system_runs()
    {
        // Exécution SYSTEM (tâche, agent) : politique et décisions reçues, mises en cache dans %ProgramData%.
        var console = new FakeConsole(PolicyJson(), Decide);
        var machine = Directory.CreateDirectory(Path.Combine(_dir, "ProgramData")).FullName;
        var system = new PolicyLoader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton" }, new HttpClient(console))
        {
            RefreshReferenceList = false,
            CacheFile = Path.Combine(machine, "policy-cache.json"),
            ReferenceCacheFile = Path.Combine(_dir, "ref.json"),
            DecisionCacheFile = Path.Combine(machine, "rollout-decisions.json"),
            Identity = () => ("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-COMPTA-01"),
        };
        await ScanAsync(system, Candidates());

        // Interface non élevée : pas de jeton (réservé aux administrateurs) ; caches propres à l'utilisateur vides.
        PolicyLoader Gui() => new(new Settings { ServerUrl = "https://console.exemple.fr" }, new HttpClient(console))
        {
            RefreshReferenceList = false,
            CacheFile = Path.Combine(_dir, "user", "policy-cache.json"),
            ReferenceCacheFile = Path.Combine(_dir, "ref.json"),
            DecisionCacheFile = Path.Combine(_dir, "user", "rollout-decisions.json"),
            MachineCacheFile = Path.Combine(machine, "policy-cache.json"),
            MachineDecisionCacheFile = Path.Combine(machine, "rollout-decisions.json"),
        };
        var previous = DataProtection.Security;
        var security = new FakeFileSecurity();
        DataProtection.Security = security;
        try
        {
            security.Own(Path.Combine(machine, "policy-cache.json"), DataProtection.SystemSid);
            security.Own(Path.Combine(machine, "rollout-decisions.json"), DataProtection.SystemSid);
            console.Requests.Clear();
            var (policy, merged) = await ScanAsync(Gui(), Candidates());

            Assert.Empty(console.Requests); // aucune demande vouée au refus (401)
            Assert.True(policy.MachineCacheOnly);
            Assert.Contains("Parc siège", policy.Organization!.DisplayName);
            Assert.Contains(policy.Messages, m => m.Contains("Interface sans droits administrateur") && m.Contains("au moment d'installer"));
            Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
            Assert.True(merged.Single(c => c.Title == "Realtek - Audio - 6.0.9600.1").HeldByRollout);
            Assert.NotNull(merged.Single(c => c.Title == "Conexant Audio 1.0").BlockedReason); // règle « gel » de l'organisation
            Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason);

            // Cache préparé par un utilisateur (propriétaire inattendu) : jamais lu ; l'interface le dit.
            security.Own(Path.Combine(machine, "policy-cache.json"), FakeFileSecurity.Attacker);
            (policy, _) = await ScanAsync(Gui(), Candidates());
            Assert.Null(policy.Organization);
            Assert.True(policy.MachineCacheOnly);
            Assert.Contains(policy.Messages, m => m.Contains("politique de la console indisponible") && m.Contains("bloqués"));
            Assert.Empty(console.Requests);
        }
        finally
        {
            DataProtection.Security = previous;
        }
    }

    [Fact]
    public async Task In_a_ring_without_delay_a_missing_decision_does_not_hold_anything()
    {
        var console = new FakeConsole(PolicyJson(holdUnknown: false), Decide) { DecideDown = true };
        var (_, merged) = await ScanAsync(Loader(console), [Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700")]);
        Assert.Null(merged.Single().BlockedReason);
    }

    [Fact]
    public async Task An_older_console_without_decisions_keeps_the_previous_behaviour()
    {
        // Console 4.0 antérieure : pas de « decisions » dans la section, retenues dans les règles ; aucune demande de décisions.
        var json = PolicyJson(decisions: false).Replace("""{ "id": "urgence",""", """
            { "id": "anneau-retenue-12", "action": "block", "source": "WindowsUpdate", "title": "Intel - Display - 31.0.101.5600", "kind": ["Update", "Optional", "Firmware"],
              "versions": "31.0.101.5600", "reason": "Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 06/12/2026 à 10:00" },
            { "id": "anneau-famille-1", "action": "pin", "source": "WindowsUpdate", "title": "Intel - Display - *", "kind": ["Update", "Optional", "Firmware"], "pinVersion": "31.0.101.5600" },
            { "id": "urgence",
            """);
        var console = new FakeConsole(json, decide: null);
        var (policy, merged) = await ScanAsync(Loader(console), Candidates());
        Assert.DoesNotContain(console.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide");
        Assert.Null(policy.Effective.Decisions);
        Assert.True(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").HeldByRollout);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5590").BlockedReason);
        Assert.True(merged.Single(c => c.Title == "Contoso - Camera - 2.4.0.1").HeldByRollout); // famille inconnue (règles)
    }

    [Fact]
    public async Task A_policy_file_or_share_never_triggers_a_decision_request()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, """{ "schema": 1, "name": "Fichier", "rules": [] }""");
        var console = new FakeConsole(PolicyJson(), Decide);
        var loader = new PolicyLoader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton", PolicySource = path }, new HttpClient(console))
        {
            RefreshReferenceList = false, CacheFile = Path.Combine(_dir, "c.json"), ReferenceCacheFile = Path.Combine(_dir, "r.json"),
            DecisionCacheFile = Path.Combine(_dir, "d.json"),
        };
        var (_, merged) = await ScanAsync(loader, Candidates());
        Assert.Empty(console.Requests);
        Assert.Null(merged.Single(c => c.Title == "Intel - Display - 31.0.101.5600").BlockedReason);
    }

    // ------------------------------------------------------------------ 4.0.1 : propositions sans version, même pilote d'une autre source

    private static readonly DeviceInfo IntelGpu = new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028&REV_01\3&11583659&0&10", Name = "Intel(R) Iris(R) Xe Graphics", ClassName = "Display",
        HardwareIds = [@"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028&REV_01", @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028"],
        CompatibleIds = [@"PCI\VEN_8086&DEV_9A49&REV_01", @"PCI\VEN_8086&CC_030000"],
    };

    private static readonly DeviceInfo Printer = new()
    {
        InstanceId = @"USBPRINT\HPLASERJET\7&1", Name = "HP LaserJet", ClassName = "Printer", HardwareIds = [@"USBPRINT\HPLaserJet_M40AFE6"],
    };

    private static DriverCandidate For(DeviceInfo device, string title, string? version, DriverSourceKind source, string? itemId = null)
    {
        var c = new DriverCandidate { Id = $"{source}:{title}", Source = source, Title = title, Version = version, SourceItemId = itemId, Kind = CandidateKind.Update };
        c.TargetDeviceIds.Add(device.InstanceId);
        c.TargetDeviceNames.Add(device.Name);
        return c;
    }

    [Fact]
    public void Driver_identity_spans_windows_update_and_the_catalog_and_reads_the_chip_vendor()
    {
        var wu = For(Printer, "HP - Printer", null, DriverSourceKind.WindowsUpdate, "6F1C2A8E-1D2B-4C3D-9E4F-0123456789AB");
        var catalog = For(Printer, "HP Inc. - Printer", "n/a", DriverSourceKind.MicrosoftCatalog, "6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab");
        Assert.Equal("update:6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", DriverIdentity.ItemKey(wu));
        Assert.Equal(DriverIdentity.ItemKey(wu), DriverIdentity.ItemKey(catalog));
        Assert.Null(DriverIdentity.ItemKey(For(IntelGpu, "Intel - Display - 31.0.101.5590", "31.0.101.5590", DriverSourceKind.WindowsUpdate, "x")));
        var noId = new DriverCandidate { Id = "x", Source = DriverSourceKind.Dell, Title = "Pilote", Manufacturer = "Dell", Date = new DateTime(2024, 5, 17) };
        Assert.Equal("dell:date=2024-05-17;fournisseur=dell", DriverIdentity.ItemKey(noId));
        Assert.NotEqual(RolloutDecisions.Key(wu), RolloutDecisions.Key(For(Printer, "HP - Printer", null, DriverSourceKind.WindowsUpdate, "autre")));
        // Rapport envoyé à la console : identité des propositions sans version (pas des liens informatifs, jamais installés).
        var link = new DriverCandidate { Id = "intel", Source = DriverSourceKind.Intel, Title = "Pilote graphique Intel", IsInformational = true, InfoUrl = "https://www.intel.com" };
        var report = ScanReport.From(new ScanResult { System = new SystemInfo(), Devices = [], Candidates = [wu, link] },
            [new InstallOutcome(wu, InstallStatus.Failed, "0x80240022")]);
        Assert.Equal("update:6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", report.Candidates[0].ItemId);
        Assert.Null(report.Candidates[1].ItemId);
        Assert.Equal(report.Candidates[0].ItemId, report.Installation![0].ItemId);

        Assert.Equal(@"PCI\VEN_8086", DriverIdentity.HardwareVendor(@"PCI\VEN_8086&DEV_9A49&SUBSYS_0A1F1028&REV_01"));
        Assert.Equal(@"HDAUDIO\VEN_10EC", DriverIdentity.HardwareVendor(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0257&SUBSYS_10280A1F"));
        Assert.Null(DriverIdentity.HardwareVendor(@"ACPI\INT33A0"));
        Assert.Equal(DriverIdentity.CanonicalVersion(DriverIdentity.NvidiaVendor, "32.0.15.6636"), DriverIdentity.CanonicalVersion(DriverIdentity.NvidiaVendor, "566.36 (Windows x.x.15.6636)"));

        var dell = For(IntelGpu, "Intel UHD Graphics Driver", "31.0.101.5590 (A12)", DriverSourceKind.Dell);
        var lenovo = For(IntelGpu, "Intel UHD Graphics Driver for Windows 11 (64-bit)", "31.0.101.5590", DriverSourceKind.Lenovo);
        var other = For(IntelGpu, "Intel - Display - 31.0.101.5592", "31.0.101.5592", DriverSourceKind.WindowsUpdate);
        DriverIdentity.Annotate([dell, lenovo, other], [IntelGpu, Printer]);
        Assert.Equal(@"PCI\VEN_8086", dell.HardwareVendor);
        Assert.Equal("Display", dell.DeviceClass);
        Assert.True(DriverIdentity.SameDriver(dell, lenovo));
        Assert.False(DriverIdentity.SameDriver(dell, other));
    }

    /// <summary>Décisions selon l'identité (sans version) ou le matériel (même pilote) envoyés par le poste.</summary>
    private static JsonObject DecideByIdentity(JsonNode request) => new()
    {
        ["ring"] = "Général",
        ["decisions"] = new JsonArray(request["candidates"]!.AsArray().Select(c =>
        {
            JsonObject d = c!["itemId"]?.GetValue<string>() == "update:printer-1"
                ? new() { ["status"] = "held", ["availableAt"] = Available.ToString("O"), ["reason"] = "Pilote HP - Printer (sans numéro de version) : disponible le 06/12/2026" }
                : c["title"]!.GetValue<string>() == "Intel UHD Graphics Driver"
                    ? new() { ["status"] = "suspended", ["reason"] = "Pilote Intel UHD Graphics Driver 31.0.101.5590 (A12) : suspendu sur tout le parc par alice : écrans noirs" }
                    : new() { ["status"] = "allowed" };
            return (JsonNode)d;
        }).ToArray()),
    };

    [Fact]
    public async Task A_versionless_candidate_is_sent_with_its_identity_and_held_like_any_other()
    {
        var console = new FakeConsole(PolicyJson(), DecideByIdentity);
        var printer = For(Printer, "HP - Printer", null, DriverSourceKind.WindowsUpdate, "printer-1");
        var policy = await Loader(console).LoadAsync();
        var (merged, _) = await DriverScanEngine.ApplyPolicyAsync(Loader(console), policy, [printer], [Printer], CancellationToken.None, Now);
        var sent = JsonNode.Parse(console.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide").Body!)!["candidates"]!.AsArray().Single()!;
        Assert.Equal("update:printer-1", sent["itemId"]!.GetValue<string>());
        Assert.Null(sent["version"]);
        var held = merged.Single();
        Assert.True(held.HeldByRollout);
        Assert.Equal(Available, held.RolloutAvailableAt);

        // Console injoignable, aucune décision connue pour cette identité : retenue par prudence (anneau à délai), comme une version.
        console.DecideDown = true;
        var fresh = For(Printer, "HP - Printer", null, DriverSourceKind.WindowsUpdate, "printer-2");
        policy = await Loader(console).LoadAsync();
        (merged, _) = await DriverScanEngine.ApplyPolicyAsync(Loader(console), policy, [fresh], [Printer], CancellationToken.None, Now);
        Assert.True(merged.Single().HeldByRollout);
        Assert.Contains("console injoignable", merged.Single().BlockedReason);
        // L'identité déjà décidée garde sa décision (cache), même sans console.
        (merged, _) = await DriverScanEngine.ApplyPolicyAsync(Loader(console), policy, [For(Printer, "HP - Printer", null, DriverSourceKind.WindowsUpdate, "printer-1")],
            [Printer], CancellationToken.None, Now);
        Assert.Equal(Available, merged.Single().RolloutAvailableAt);
    }

    /// <summary>
    /// Intel 31.0.101.5590 suspendu pour le paquet Dell : le catalogue Microsoft et Windows Update proposent la même version
    /// sous d'autres titres. La fusion ne doit JAMAIS retenir l'une d'elles à la place du paquet Dell bloqué.
    /// </summary>
    [Fact]
    public async Task Merge_never_picks_the_same_suspended_version_from_another_source()
    {
        var console = new FakeConsole(PolicyJson(), DecideByIdentity);
        var dell = For(IntelGpu, "Intel UHD Graphics Driver", "31.0.101.5590 (A12)", DriverSourceKind.Dell);
        var catalog = For(IntelGpu, "Intel Corporation - Display - 31.0.101.5590", "31.0.101.5590", DriverSourceKind.MicrosoftCatalog, "c0ffee");
        catalog.Alternatives.Add(new DriverCandidate { Id = "alt", Source = DriverSourceKind.MicrosoftCatalog, Title = "Intel Corporation - Display - 31.0.101.5590", Version = "31.0.101.5590" });
        catalog.Alternatives.Add(new DriverCandidate { Id = "alt2", Source = DriverSourceKind.MicrosoftCatalog, Title = "Intel Corporation - Display - 31.0.101.5555", Version = "31.0.101.5555" });
        var wu = For(IntelGpu, "Intel - Display - 31.0.101.5590", "31.0.101.5590", DriverSourceKind.WindowsUpdate, "f00d");
        var policy = await Loader(console).LoadAsync();
        var (merged, decisions) = await DriverScanEngine.ApplyPolicyAsync(Loader(console), policy, [catalog, wu, dell], [IntelGpu], CancellationToken.None, Now);

        // Le matériel visé est envoyé avec chaque proposition (la console 4.0.1 suspend elle aussi le même pilote).
        var sent = JsonNode.Parse(console.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide").Body!)!["candidates"]!.AsArray();
        Assert.All(sent, c => Assert.Equal(@"PCI\VEN_8086", c!["hardwareVendor"]!.GetValue<string>()));
        Assert.All(sent, c => Assert.Equal("Display", c!["deviceClass"]!.GetValue<string>()));

        var shown = Assert.Single(merged);
        Assert.Equal(DriverSourceKind.Dell, shown.Source); // tous bloqués : la source la plus prioritaire reste affichée
        Assert.True(shown.HeldByRollout);
        Assert.Contains("écrans noirs", shown.BlockedReason);
        Assert.All([catalog, wu], c =>
        {
            Assert.True(c.HeldByRollout);
            Assert.Contains("même pilote, proposé par Dell", c.BlockedReason);
        });
        Assert.Equal(["Intel Corporation - Display - 31.0.101.5555"], catalog.Alternatives.Select(a => a.Title)); // l'alternative de même version est écartée
        Assert.All(decisions, d => Assert.True(d.Decision.IsBlocked));
    }

    [Fact]
    public void A_hardware_suspension_rule_served_to_40_clients_blocks_every_source()
    {
        // Règle complémentaire servie par la console 4.0.1 (format 1 : aussi comprise par les postes 3.x et 4.0.0).
        var json = """
            { "schema": 1, "name": "Parc", "rules": [
              { "id": "anneau-suspendu-7", "action": "block", "source": "Dell", "title": "Intel UHD Graphics Driver", "versions": "31.0.101.5590", "reason": "Pilote X : suspendu" },
              { "id": "anneau-suspendu-materiel-7", "action": "block", "hardwareId": ["PCI\\*VEN_8086&*", "PCI\\*VEN_8086"], "category": "Display",
                "versions": "31.0.101.5590", "reason": "Pilote X : suspendu (même pilote, quelle que soit la source)" } ],
              "rollout": { "ring": "Général", "enabled": true, "decisions": true, "holdUnknown": false } }
            """;
        var parsed = DriverPolicy.Parse(json);
        Assert.True(parsed.IsValid, string.Join(" ; ", parsed.Errors));
        var policy = DriverPolicy.Combine(parsed.Policy, null);
        var hp = For(IntelGpu, "Intel UHD Graphics Driver", "31.0.101.5590 Rev.A", DriverSourceKind.HP);
        var newer = For(IntelGpu, "Intel - Display - 31.0.101.5592", "31.0.101.5592", DriverSourceKind.WindowsUpdate);
        var (merged, _) = DriverScanEngine.ApplyPolicy([hp, newer], [IntelGpu], policy, Now);
        Assert.True(hp.HeldByRollout);
        Assert.Null(newer.BlockedReason);
        Assert.Equal(newer, Assert.Single(merged));
        // Résumé : une suspension, pas deux.
        var loaded = new LoadedPolicy { Effective = policy, Organization = parsed.Policy, OrganizationOrigin = "https://console.exemple.fr/api/policy" };
        Assert.Contains("1 suspendue(s)", loaded.Summary);
    }

    // ------------------------------------------------------------------ revérification juste avant l'installation

    /// <summary>
    /// L'interface installe depuis une analyse gardée en mémoire (parfois depuis des heures) : un pilote suspendu par le
    /// disjoncteur entre l'analyse et le clic ne doit pas être installé. La politique et les décisions sont redemandées.
    /// </summary>
    [Fact]
    public async Task A_driver_suspended_between_the_scan_and_the_install_click_is_withdrawn()
    {
        var suspendedNow = new HashSet<string>();
        var console = new FakeConsole(PolicyJson(), request =>
        {
            var answer = Decide(request);
            var list = request["candidates"]!.AsArray();
            for (var i = 0; i < list.Count; i++)
                if (suspendedNow.Contains(list[i]!["title"]!.GetValue<string>()))
                    answer["decisions"]![i] = new JsonObject { ["status"] = "suspended", ["reason"] = "Pilote X : suspendu automatiquement : 3 échecs" };
            return answer;
        });
        var ok = For(IntelGpu, "Intel - Display - 31.0.101.5590", "31.0.101.5590", DriverSourceKind.WindowsUpdate);
        var other = Cand("Contoso - Camera - 2.4.0.2", "2.4.0.2");
        var alreadyBlocked = Cand("Conexant Audio 1.0", "1.0");
        var (_, merged) = await ScanAsync(Loader(console), [ok, other, alreadyBlocked]);
        Assert.Null(ok.BlockedReason);
        Assert.Null(other.BlockedReason);
        Assert.NotNull(alreadyBlocked.BlockedReason);

        // Rien n'a changé : rien n'est écarté.
        var recheck = await DriverScanEngine.RecheckPolicyAsync(Loader(console), [ok, other], [IntelGpu], CancellationToken.None, Now);
        Assert.False(recheck.Changed);

        // Le disjoncteur suspend Intel entre l'analyse et le clic.
        suspendedNow.Add(ok.Title);
        console.Requests.Clear();
        recheck = await DriverScanEngine.RecheckPolicyAsync(Loader(console), [ok, other, alreadyBlocked], [IntelGpu], CancellationToken.None, Now);
        var withdrawn = Assert.Single(recheck.NewlyBlocked);
        Assert.Same(ok, withdrawn.Candidate);
        Assert.True(ok.HeldByRollout);
        Assert.Contains("suspendu automatiquement", ok.BlockedReason);
        Assert.Null(other.BlockedReason);
        // Politique rechargée, décisions redemandées pour les seules propositions encore installables.
        Assert.Contains(console.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/policy");
        var sent = JsonNode.Parse(console.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/rollout/decide").Body!)!["candidates"]!.AsArray();
        Assert.Equal(2, sent.Count);
        // L'installeur refuse de toute façon un candidat bloqué.
        Assert.Contains(merged, c => ReferenceEquals(c, alreadyBlocked));

        // Console injoignable au moment du clic : dernières décisions connues (en cache) — ni blocage arbitraire, ni levée de blocage.
        console.Down = true;
        var again = Cand("Contoso - Camera - 2.4.0.2", "2.4.0.2");
        recheck = await DriverScanEngine.RecheckPolicyAsync(Loader(console), [again], [], CancellationToken.None, Now);
        Assert.False(recheck.Changed);
        Assert.True(recheck.Policy.FromCache);
        var never = Cand("Contoso - Camera - 9.9.9.9", "9.9.9.9");
        recheck = await DriverScanEngine.RecheckPolicyAsync(Loader(console), [never], [], CancellationToken.None, Now);
        Assert.True(never.HeldByRollout); // jamais décidée, console injoignable : retenue par prudence (anneau à délai)
    }

    [Fact]
    public async Task A_new_organization_rule_published_after_the_scan_is_applied_before_installing()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, """{ "schema": 1, "name": "Fichier", "rules": [] }""");
        var loader = new PolicyLoader(new Settings { PolicySource = path }, new HttpClient(new FakeConsole("{}", null)))
        {
            RefreshReferenceList = false, CacheFile = Path.Combine(_dir, "c.json"), ReferenceCacheFile = Path.Combine(_dir, "r.json"), DecisionCacheFile = Path.Combine(_dir, "d.json"),
        };
        var gpu = For(IntelGpu, "Intel - Display - 31.0.101.5590", "31.0.101.5590", DriverSourceKind.WindowsUpdate);
        var hidden = Cand("Realtek Audio", "6.0.9600.1");
        var (_, merged) = await ScanAsync(loader, [gpu, hidden]);
        Assert.Equal(2, merged.Count);
        File.WriteAllText(path, """
            { "schema": 1, "name": "Fichier", "rules": [
              { "id": "gel", "action": "block", "hardwareId": "PCI\\VEN_8086*", "reason": "Gel des pilotes graphiques" },
              { "id": "masque", "action": "block", "hide": true, "title": "Realtek*", "reason": "Géré à part" } ] }
            """);
        var recheck = await DriverScanEngine.RecheckPolicyAsync(loader, [gpu, hidden], [IntelGpu], CancellationToken.None, Now);
        Assert.Equal(2, recheck.NewlyBlocked.Count);
        Assert.Equal("Gel des pilotes graphiques (règle « gel »)", gpu.BlockedReason);
        Assert.False(gpu.HeldByRollout);
        Assert.NotNull(hidden.BlockedReason); // masqué par la nouvelle politique : jamais installé
    }

    [Fact]
    public void The_cache_keeps_the_most_recent_decisions_of_the_same_console_only()
    {
        var path = Path.Combine(_dir, "cache.json");
        var c = Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600");
        RolloutDecisions.Save(path, "https://console.exemple.fr/api/policy",
            new Dictionary<string, RolloutDecision> { [RolloutDecisions.Key(c)] = new() { Status = RolloutDecision.Held, DecidedAt = Now } }, Now);
        Assert.Equal(RolloutDecision.Held, RolloutDecisions.FromCacheFile(path, "https://console.exemple.fr/api/policy", [c])!.For(c)!.Status);
        Assert.Null(RolloutDecisions.FromCacheFile(path, "https://autre.exemple.fr/api/policy", [c])); // autre console : ignoré
        // Même titre en majuscules, version écrite autrement : même proposition (comme la console).
        Assert.Equal(RolloutDecisions.Key(c), RolloutDecisions.Key(Cand("INTEL - DISPLAY - 31.0.101.5600", "31.0.101.5600.0")));
        var many = Enumerable.Range(0, RolloutDecisions.MaxCacheEntries + 10).ToDictionary(i => $"k{i}", i => new RolloutDecision { DecidedAt = Now.AddMinutes(i) });
        many["vieux"] = new RolloutDecision { DecidedAt = Now.AddDays(-400) };
        RolloutDecisions.Save(path, "https://console.exemple.fr/api/policy", many, Now.AddDays(1));
        var all = JsonNode.Parse(File.ReadAllText(path))!["decisions"]!.AsObject();
        Assert.Equal(RolloutDecisions.MaxCacheEntries, all.Count);
        Assert.False(all.ContainsKey("vieux"));
        Assert.False(all.ContainsKey("k0"));
    }
}

/// <summary>
/// Mesure côté poste (à la demande) : temps de lecture de la politique et d'application aux propositions d'un poste, avant
/// (politique avec la liste des retenues de tout le parc) et après (politique réduite aux suspensions + décisions). Utilise
/// les documents enregistrés par « SpokaPilote.LoadTest rollout --save-dir » : SPOKAPILOTE_ROLLOUT_BENCH_DIR=dossier.
/// Sans cette variable, rien n'est mesuré.
/// </summary>
public sealed class RolloutClientMeasurement(ITestOutputHelper output)
{
    /// <summary>
    /// Bout en bout contre une console réelle (à la demande) : le code du poste charge sa politique, demande ses décisions et
    /// les applique aux propositions enregistrées par le banc. Variables : SPOKAPILOTE_ROLLOUT_BENCH_URL, _TOKEN (jeton
    /// d'enrôlement) et SPOKAPILOTE_ROLLOUT_BENCH_DIR (dossier du banc : decide-request.json, machine.txt). Caches dans un
    /// dossier temporaire.
    /// </summary>
    [Fact]
    public async Task Live_console_end_to_end_client_time()
    {
        if (Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_URL") is not { Length: > 0 } url ||
            Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_TOKEN") is not { Length: > 0 } token ||
            Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_DIR") is not { Length: > 0 } dirs) return;
        var dir = dirs.Split(';')[0];
        var machine = File.ReadAllLines(Path.Combine(dir, "machine.txt"));
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-request.json")))!["candidates"]!.AsArray();
        var scratch = Path.Combine(Path.GetTempPath(), $"sp-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            for (var run = 0; run < 3; run++)
            {
                var candidates = request.Select((c, i) => new DriverCandidate
                {
                    Id = $"c{i}", Source = Enum.TryParse<DriverSourceKind>(c!["source"]!.GetValue<string>(), out var s) ? s : DriverSourceKind.WindowsUpdate,
                    Title = c["title"]!.GetValue<string>(), Version = c["version"]!.GetValue<string>(), Kind = Enum.Parse<CandidateKind>(c["kind"]!.GetValue<string>()),
                }).ToList();
                var loader = new PolicyLoader(new Settings { ServerUrl = url, ServerToken = token }, new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }))
                {
                    RefreshReferenceList = false, CacheFile = Path.Combine(scratch, "policy-cache.json"), ReferenceCacheFile = Path.Combine(scratch, "ref.json"),
                    DecisionCacheFile = Path.Combine(scratch, "rollout-decisions.json"), Identity = () => (machine[1], machine[0]),
                };
                var sw = Stopwatch.StartNew();
                var policy = await loader.LoadAsync();
                var load = sw.Elapsed.TotalMilliseconds;
                var (merged, _) = await DriverScanEngine.ApplyPolicyAsync(loader, policy, candidates, [], CancellationToken.None);
                output.WriteLine($"{machine[0]} : politique {load:0} ms ({policy.Organization?.Rules.Count ?? 0} règle(s), décisions : {policy.Organization?.Rollout?.Decisions}), " +
                                 $"décisions + application {sw.Elapsed.TotalMilliseconds - load:0} ms, total {sw.Elapsed.TotalMilliseconds:0} ms ; " +
                                 $"{merged.Count(c => c.HeldByRollout)}/{merged.Count} retenue(s) ; {string.Join(" ", policy.Messages)}");
            }
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { /* temporaire */ }
        }
    }

    [Fact]
    public void Client_side_policy_application_time_from_saved_documents()
    {
        if (Environment.GetEnvironmentVariable("SPOKAPILOTE_ROLLOUT_BENCH_DIR") is not { Length: > 0 } dirs) return;
        foreach (var dir in dirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var request = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-request.json")))!["candidates"]!.AsArray();
            var candidates = request.Select((c, i) => new DriverCandidate
            {
                Id = $"c{i}", Source = Enum.TryParse<DriverSourceKind>(c!["source"]!.GetValue<string>(), out var s) ? s : DriverSourceKind.WindowsUpdate,
                Title = c["title"]!.GetValue<string>(), Version = c["version"]!.GetValue<string>(),
                Kind = Enum.Parse<CandidateKind>(c["kind"]!.GetValue<string>()),
            }).ToList();
            output.WriteLine($"{dir} : {candidates.Count} proposition(s)");
            foreach (var file in new[] { "policy-40-rules.json", "policy-3x.json", "policy-40-decisions.json" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                RolloutDecisions? decisions = null;
                if (file == "policy-40-decisions.json" && File.Exists(Path.Combine(dir, "decide-response.json")) &&
                    JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "decide-response.json")))?["decisions"] is JsonArray list && list.Count == candidates.Count)
                    decisions = new RolloutDecisions(candidates.Zip(list).GroupBy(x => RolloutDecisions.Key(x.First)).ToDictionary(g => g.Key, g => new RolloutDecision
                    {
                        Status = g.First().Second!["status"]!.GetValue<string>(), Reason = g.First().Second!["reason"]?.GetValue<string>(),
                        AvailableAt = g.First().Second!["availableAt"] is { } at ? DateTimeOffset.Parse(at.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
                    }), fromCache: false);
                var times = new List<double>();
                var held = 0;
                var accepted = json.Length <= 2 * 1024 * 1024;
                for (var run = 0; run < (json.Length > 500_000 ? 1 : 3); run++)
                {
                    var sw = Stopwatch.StartNew();
                    var parsed = DriverPolicy.Parse(json);
                    if (!parsed.IsValid) { output.WriteLine($"  {file} : invalide ({parsed.Errors.FirstOrDefault()})"); break; }
                    var policy = DriverPolicy.Combine(parsed.Policy, PolicyLoader.LoadEmbeddedReference().Policy);
                    policy.Decisions = decisions;
                    var copy = candidates.Select(c => new DriverCandidate { Id = c.Id, Source = c.Source, Title = c.Title, Version = c.Version, Kind = c.Kind }).ToList();
                    var (merged, _) = DriverScanEngine.ApplyPolicy(copy, [], policy);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    held = merged.Count(c => c.BlockedReason is not null);
                }
                if (times.Count > 0)
                    output.WriteLine($"  {file} : {json.Length / 1024.0:0} Ko ({(accepted ? "accepté" : "REFUSÉ par PolicyLoader : > 2 Mo")}), " +
                                     $"lecture + application : {times.Min():0} ms (meilleur de {times.Count}), {held} proposition(s) bloquée(s) ou retenue(s)");
            }
        }
    }
}
