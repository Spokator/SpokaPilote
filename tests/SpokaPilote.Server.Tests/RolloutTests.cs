using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Groups;
using SpokaPilote.Server.Rollout;
using SpokaPilote.Server.Storage;
using Xunit.Abstractions;
using Legacy = SpokaPilote.Server.Tests.Legacy31;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Rapports de test pour la diffusion par anneaux : propositions, résultats d'installation, périphériques en erreur.</summary>
internal static class RolloutReports
{
    public sealed record Cand(string Title, string Version, string Source = "Windows Update", string Kind = "Mise à jour");
    public sealed record Out(string Title, string Version, string Status, string Message = "");
    public sealed record Problem(string Name, string DriverVersion, int ErrorCode = 43);

    public static string MachineId(int n) => new Guid(n, 0x5150, 0, new byte[8]).ToString();

    public static string Json(string name, int machine, DateTimeOffset at, IEnumerable<Cand> candidates, IEnumerable<Out>? outcomes = null,
        IEnumerable<Problem>? problems = null)
    {
        var devices = new JsonArray
        {
            new JsonObject { ["instanceId"] = "PCI\\VEN_8086&DEV_1\\1", ["name"] = "Carte réseau", ["errorCode"] = 0, ["isPresent"] = true },
        };
        var i = 0;
        foreach (var p in problems ?? [])
            devices.Add(new JsonObject
            {
                ["instanceId"] = $"PCI\\VEN_10DE&DEV_{i}\\{i++}", ["name"] = p.Name, ["errorCode"] = p.ErrorCode, ["isPresent"] = true,
                ["hardwareIds"] = new JsonArray("PCI\\VEN_10DE&DEV_2560"), ["driverVersion"] = p.DriverVersion,
            });
        var root = new JsonObject
        {
            ["schema"] = 2,
            ["appVersion"] = "4.0.0",
            ["timestamp"] = at.ToString("O"),
            ["system"] = new JsonObject { ["computerName"] = name, ["machineId"] = MachineId(machine), ["manufacturer"] = "Dell Inc.", ["model"] = "Latitude 7440" },
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
            {
                ["title"] = c.Title, ["version"] = c.Version, ["source"] = c.Source, ["kind"] = c.Kind, ["deviceIds"] = new JsonArray(),
            }).ToArray()),
            ["devices"] = devices,
        };
        if (outcomes is not null)
            root["installation"] = new JsonArray(outcomes.Select(o => (JsonNode)new JsonObject
            {
                ["title"] = o.Title, ["version"] = o.Version, ["status"] = o.Status, ["message"] = o.Message,
            }).ToArray());
        return root.ToJsonString();
    }
}

// ====================================================================== fonctions pures

public sealed class RolloutKeysTests
{
    [Theory]
    [InlineData("Intel - Display - 31.0.101.5590", "Intel - Display - *")]
    [InlineData("NVIDIA GeForce Game Ready 566.36", "NVIDIA GeForce Game Ready *")]
    [InlineData("Dell BIOS 1.23.0", "Dell BIOS *")]
    [InlineData("Realtek Audio Driver", "Realtek Audio Driver")]
    [InlineData("Intel(R) Ethernet Connection I219-LM", "Intel(R) Ethernet Connection I219-LM")]
    [InlineData("10.1.37.7", "10.1.37.7")] // motif trop général (« * ») : titre exact
    public void Family_replaces_version_numbers_with_a_wildcard(string title, string family) =>
        Assert.Equal(family, RolloutKeys.FamilyPattern(title));

    [Fact]
    public void Versions_and_sources_follow_the_client_conventions()
    {
        Assert.Equal(new Version(10, 30, 0, 5714), RolloutKeys.ParseVersion("10.30.00.5714"));
        Assert.True(RolloutKeys.SameVersion("10.30.00.5714", "10.30.0.5714"));
        Assert.Equal(new Version(1, 2, 0, 0), RolloutKeys.ParseVersion("1.2 A05"));
        Assert.Equal(new Version(5, 0, 0, 0), RolloutKeys.ParseVersion("A05")); // comme SpokaPilote.Core
        Assert.Null(RolloutKeys.ParseVersion("version inconnue"));
        Assert.False(RolloutKeys.IsTrackable("Pilote", null));
        Assert.Equal("WindowsUpdate", RolloutKeys.SourceKind("Windows Update"));
        Assert.Equal("Dell", RolloutKeys.SourceKind("Dell (catalogue officiel)"));
        Assert.Equal("Nvidia", RolloutKeys.SourceKind("Nvidia"));
        Assert.Null(RolloutKeys.SourceKind("Source inconnue"));
        Assert.Equal("Update", RolloutKeys.CandidateKind("Mise à jour"));
        Assert.Equal("Intel - Display - 31.0.101.5590", RolloutKeys.DisplayName("Intel - Display - 31.0.101.5590", "31.0.101.5590"));
        Assert.Equal("Intel UHD 31.0.101.5590", RolloutKeys.DisplayName("Intel UHD", "31.0.101.5590"));
    }
}

/// <summary>Données de diffusion fabriquées en mémoire (sans base).</summary>
internal static class RolloutSamples
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    public static RingRecord Ring(long id, string name, int position, int delay, bool validation = false, int min = 1, int maxFailures = 0) =>
        new(id, name, position, delay, validation, min, maxFailures, null, [], T0, T0);

    public static readonly RingRecord Pilot = Ring(1, "Pilote", 1, 0);
    public static readonly RingRecord Early = Ring(2, "Précoce", 2, 3);
    public static readonly RingRecord General = Ring(3, "Général", 3, 10);

    public static RolloutVersion Version(long id, string version, DateTimeOffset firstSeen, string title = "Intel - Display - {0}", string source = "WindowsUpdate",
        bool suspended = false, DateTimeOffset? approved = null, DateTimeOffset? lastSeen = null)
    {
        var t = string.Format(System.Globalization.CultureInfo.InvariantCulture, title, version);
        return new RolloutVersion
        {
            Id = id, Source = source, Title = t, Version = version, Family = RolloutKeys.FamilyPattern(t), Kind = "Update",
            FirstSeen = firstSeen, LastSeen = lastSeen ?? firstSeen, Suspended = suspended, SuspendAuto = suspended,
            SuspendReason = suspended ? "suspendu automatiquement : 3 échecs sur 7 installations dans l'anneau « Pilote » en 48 h" : null,
            ApprovedAt = approved,
        };
    }

    public static RolloutSnapshot Snapshot(IEnumerable<RingRecord> rings, IEnumerable<RolloutVersion> versions,
        Dictionary<(long, long), RingStat>? stats = null, bool enabled = true, bool applyToMissing = false) => new()
    {
        Settings = new RolloutSettings { Enabled = enabled, ApplyToMissingDrivers = applyToMissing },
        Rings = rings.ToList(),
        Versions = versions.ToList(),
        Stats = stats ?? new Dictionary<(long, long), RingStat>(),
        BuiltAt = T0,
    };

    public static TimeZoneInfo Paris =>
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Paris", out var z) ? z : TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
}

public sealed class RolloutPlannerTests
{
    private static readonly RingRecord[] Rings = [RolloutSamples.Pilot, RolloutSamples.Early, RolloutSamples.General];

    [Fact]
    public void Each_ring_gets_a_version_its_delay_after_the_first_detection()
    {
        var first = RolloutSamples.T0;
        var s = RolloutSamples.Snapshot(Rings, [RolloutSamples.Version(1, "31.0.101.5590", first)]);

        RingState[] States(DateTimeOffset now) => RolloutPlanner.Plan(s, now)[0].Rings.Select(r => r.State).ToArray();
        Assert.Equal([RingState.Allowed, RingState.Waiting, RingState.Waiting], States(first));
        Assert.Equal([RingState.Allowed, RingState.Waiting, RingState.Waiting], States(first.AddDays(3).AddTicks(-1)));
        Assert.Equal([RingState.Allowed, RingState.Allowed, RingState.Waiting], States(first.AddDays(3))); // borne incluse
        Assert.Equal([RingState.Allowed, RingState.Allowed, RingState.Allowed], States(first.AddDays(10)));
        Assert.True(RolloutPlanner.Plan(s, first.AddDays(10))[0].Completed);
        Assert.Equal(first.AddDays(3), RolloutPlanner.NextChange(RolloutPlanner.Plan(s, first.AddHours(1)), first.AddHours(1)));
    }

    [Fact]
    public void Eligibility_is_an_instant_independent_of_time_zones_and_daylight_saving()
    {
        // Première détection le 20/10/2026 à 10:00 heure de Paris (été, UTC+2) ; 10 jours plus tard on est passé à l'heure d'hiver.
        var paris = RolloutSamples.Paris;
        var firstLocal = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(2));
        var firstUtc = firstLocal.ToUniversalTime();
        var a = RolloutSamples.Snapshot(Rings, [RolloutSamples.Version(1, "1.0", firstLocal)]);
        var b = RolloutSamples.Snapshot(Rings, [RolloutSamples.Version(1, "1.0", firstUtc)]);
        var now = firstUtc.AddDays(10).AddMinutes(-1);
        var pa = RolloutPlanner.Plan(a, now)[0].For(3)!;
        var pb = RolloutPlanner.Plan(b, now.ToOffset(TimeSpan.FromHours(-7)))[0].For(3)!;
        Assert.Equal(pa.AvailableAt.UtcDateTime, pb.AvailableAt.UtcDateTime);
        Assert.Equal(firstUtc.AddHours(240).UtcDateTime, pa.AvailableAt.UtcDateTime); // 10 × 24 h exactement
        Assert.False(pa.Eligible);
        Assert.False(pb.Eligible);
        Assert.True(RolloutPlanner.Plan(a, now.AddMinutes(1))[0].For(3)!.Eligible);
        // Affichage dans le fuseau de la console : 08:00 UTC le 30/10 = 09:00 heure d'hiver de Paris.
        Assert.Equal("30/10/2026 à 09:00", RolloutPlanner.FormatDate(pa.AvailableAt, paris));
        Assert.Equal("30/10/2026 à 08:00", RolloutPlanner.FormatDate(pa.AvailableAt, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Validation_requires_successes_and_tolerated_failures_in_the_previous_ring()
    {
        var early = RolloutSamples.Ring(2, "Précoce", 2, 3, validation: true, min: 2, maxFailures: 0);
        var general = RolloutSamples.Ring(3, "Général", 3, 10, validation: true, min: 3, maxFailures: 1);
        var v = RolloutSamples.Version(1, "2.0", RolloutSamples.T0);
        var now = RolloutSamples.T0.AddDays(11);

        RingStatus Status(Dictionary<(long, long), RingStat> stats, long ring) =>
            RolloutPlanner.Plan(RolloutSamples.Snapshot([RolloutSamples.Pilot, early, general], [v], stats), now)[0].For(ring)!;

        var none = new Dictionary<(long, long), RingStat>();
        Assert.Equal(RingState.Allowed, Status(none, 1).State);
        Assert.Equal(RingState.AwaitingValidation, Status(none, 2).State);
        Assert.Contains("0/2 réussie(s)", RolloutPlanner.Label(Status(none, 2), v, TimeZoneInfo.Utc));

        var pilotOk = new Dictionary<(long, long), RingStat> { [(1, 1)] = new(2, 0, 0) };
        Assert.True(Status(pilotOk, 2).Eligible);
        Assert.Equal(RingState.Validated, Status(pilotOk, 1).State); // 2 réussites : seuils de « Précoce » atteints
        Assert.Equal(RingState.AwaitingValidation, Status(pilotOk, 3).State);

        // Un échec dans l'anneau Pilote retire l'autorisation (0 toléré par « Précoce »), même avec assez de réussites.
        var pilotFailed = new Dictionary<(long, long), RingStat> { [(1, 1)] = new(5, 1, 0) };
        Assert.False(Status(pilotFailed, 2).Eligible);

        // « Général » tolère 1 échec dans « Précoce » sur 3 réussites.
        var earlyOk = new Dictionary<(long, long), RingStat> { [(1, 1)] = new(2, 0, 0), [(1, 2)] = new(3, 1, 0) };
        Assert.True(Status(earlyOk, 3).Eligible);
        // Délai pas écoulé : la validation ne suffit pas.
        Assert.False(RolloutPlanner.Plan(RolloutSamples.Snapshot([RolloutSamples.Pilot, early, general], [v], earlyOk), RolloutSamples.T0.AddDays(5))[0].For(3)!.Eligible);
    }

    [Fact]
    public void Suspension_blocks_every_ring_and_approval_skips_delays()
    {
        var suspended = RolloutSamples.Version(1, "2.0", RolloutSamples.T0.AddDays(-30), suspended: true);
        var approved = RolloutSamples.Version(2, "3.0", RolloutSamples.T0, approved: RolloutSamples.T0);
        var plans = RolloutPlanner.Plan(RolloutSamples.Snapshot(Rings, [suspended, approved]), RolloutSamples.T0);
        Assert.All(plans[0].Rings, r => Assert.Equal(RingState.Suspended, r.State));
        Assert.Equal("suspended", plans[0].Status);
        Assert.All(plans[1].Rings, r => Assert.True(r.Eligible));
        Assert.Equal("autorisé (diffusion forcée)", RolloutPlanner.Label(plans[1].For(3)!, approved, TimeZoneInfo.Utc));
    }
}

public sealed class RolloutPolicyBuilderTests
{
    private static readonly RingRecord[] Rings = [RolloutSamples.Pilot, RolloutSamples.Early, RolloutSamples.General];
    private static readonly DateTimeOffset Now = RolloutSamples.T0;

    /// <summary>Version 5590 vue il y a 30 jours (diffusion terminée), 5600 vue il y a 4 jours (autorisée Pilote et Précoce, pas Général).</summary>
    private static (RolloutSnapshot Snapshot, List<VersionPlan> Plans) Fleet()
    {
        var s = RolloutSamples.Snapshot(Rings,
        [
            RolloutSamples.Version(1, "31.0.101.5590", Now.AddDays(-30)),
            RolloutSamples.Version(2, "31.0.101.5600", Now.AddDays(-4)),
            RolloutSamples.Version(3, "27.20.100.9999", Now.AddDays(-200), title: "Intel - Display - {0}"),
            RolloutSamples.Version(4, "6.0.9600.1", Now.AddDays(-1), title: "Realtek Audio Driver", source: "Dell"),
        ]);
        return (s, RolloutPlanner.Plan(s, Now));
    }

    private static Legacy.Models.DriverCandidate Candidate(string title, string version, string kind = "Update",
        Legacy.Models.DriverSourceKind source = Legacy.Models.DriverSourceKind.WindowsUpdate) => new()
    {
        Id = title + version, Source = source, Title = title, Version = version,
        Kind = Enum.Parse<Legacy.Models.CandidateKind>(kind),
    };

    private static Legacy.Policy.DriverPolicy Parse31(string json)
    {
        var parsed = Legacy.Policy.DriverPolicy.Parse(json);
        Assert.True(parsed.IsValid, string.Join(" ; ", parsed.Errors));
        return parsed.Policy!;
    }

    [Fact]
    public void Pilot_ring_gets_no_hold_and_later_rings_hold_what_is_not_yet_eligible()
    {
        var (s, plans) = Fleet();
        var pilot = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(RolloutSamples.Pilot, false), Now, TimeZoneInfo.Utc);
        Assert.Empty(pilot.Rules);
        Assert.False(pilot.HoldUnknown);

        var general = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(RolloutSamples.General, false), Now, TimeZoneInfo.Utc);
        var holds = general.Rules.Where(r => r["id"]!.GetValue<string>().StartsWith(RolloutPolicyBuilder.HoldPrefix)).ToList();
        Assert.Equal(["anneau-retenue-2", "anneau-retenue-4"], holds.Select(r => r["id"]!.GetValue<string>()));
        Assert.Equal("Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 02/12/2026 à 09:00",
            holds[0]["reason"]!.GetValue<string>());
        Assert.Equal(Now.AddDays(6).ToString("O"), general.Availability["anneau-retenue-2"]);
        Assert.True(general.HoldUnknown);
        // Familles : Intel en deux plages de versions majeures (27, 31), Realtek (aucune version autorisée → block).
        var families = general.Rules.Where(r => r["id"]!.GetValue<string>().StartsWith(RolloutPolicyBuilder.FamilyPrefix)).ToList();
        Assert.Equal(3, families.Count);
        Assert.Equal(3, general.Families);

        var early = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(RolloutSamples.Early, false), Now, TimeZoneInfo.Utc);
        Assert.Equal(["anneau-retenue-4"], early.Rules.Where(r => r["id"]!.GetValue<string>().StartsWith(RolloutPolicyBuilder.HoldPrefix))
            .Select(r => r["id"]!.GetValue<string>()));
    }

    [Fact]
    public void A_31_client_applies_the_holds_and_retains_unknown_newer_versions()
    {
        var (s, plans) = Fleet();
        const string org = """
            // Politique de l'organisation (commentaires et virgules finales acceptés, comme par les postes)
            { "schema": 1, "name": "Parc siège", "version": 7,
              "rules": [ { "id": "audio-gel", "action": "block", "title": "Conexant*", "reason": "Gel audio" }, ] }
            """;
        var rules = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(RolloutSamples.General, false), Now, TimeZoneInfo.Utc);
        var json = RolloutPolicyBuilder.Compose(org, rules, rollout: null)!;
        Assert.DoesNotContain("\"rollout\"", json);

        // Analyseur EXACT de SpokaPilote 3.1.0 (copie figée) : la politique est valide et appliquée telle quelle.
        var p31 = Parse31(json);
        Assert.Equal("Parc siège", p31.Name);
        Assert.Equal("audio-gel", p31.Rules.Last().Id); // les règles de l'organisation restent, après celles de la diffusion
        var at = Now.UtcDateTime.ToLocalTime();
        Legacy.Policy.PolicyDecision Decide(Legacy.Models.DriverCandidate c) => p31.Evaluate(c, null, at);

        Assert.True(Decide(Candidate("Intel - Display - 31.0.101.5600", "31.0.101.5600")).IsBlocked); // en diffusion : retenue
        Assert.Contains("disponible pour votre anneau « Général »", Decide(Candidate("Intel - Display - 31.0.101.5600", "31.0.101.5600")).Reason);
        Assert.False(Decide(Candidate("Intel - Display - 31.0.101.5590", "31.0.101.5590")).IsBlocked); // diffusion terminée
        // Version plus récente JAMAIS vue par la console (afflux à heure fixe) : retenue par la règle de famille.
        var unknown = Decide(Candidate("Intel - Display - 31.0.101.5700", "31.0.101.5700"));
        Assert.True(unknown.IsBlocked);
        Assert.Contains("postérieure à 31.0.101.5590", unknown.Reason);
        // Nouvelle version majeure : couverte par la dernière plage.
        Assert.True(Decide(Candidate("Intel - Display - 32.0.101.6000", "32.0.101.6000")).IsBlocked);
        // Ancienne branche 27.x : plafonnée à sa propre version autorisée, pas à 31.x.
        Assert.False(Decide(Candidate("Intel - Display - 27.20.100.9999", "27.20.100.9999")).IsBlocked);
        Assert.True(Decide(Candidate("Intel - Display - 27.20.100.10000", "27.20.100.10000")).IsBlocked);
        // Pilote manquant : non soumis aux anneaux par défaut.
        Assert.False(Decide(Candidate("Intel - Display - 31.0.101.5600", "31.0.101.5600", kind: "MissingDriver")).IsBlocked);
        // Règle de l'organisation toujours appliquée.
        Assert.True(Decide(Candidate("Conexant Audio", "1.0")).IsBlocked);
        // Source différente : pas concernée par les retenues de Windows Update.
        Assert.False(Decide(Candidate("Intel - Display - 31.0.101.5600", "31.0.101.5600", source: Legacy.Models.DriverSourceKind.MicrosoftCatalog)).IsBlocked);
    }

    [Fact]
    public void The_rollout_section_is_unknown_to_31_clients_so_it_is_only_sent_on_request()
    {
        var (s, plans) = Fleet();
        var target = new PolicyTarget(RolloutSamples.General, false);
        var rules = RolloutPolicyBuilder.Build(s, plans, target, Now, TimeZoneInfo.Utc);
        var json40 = RolloutPolicyBuilder.Compose(null, rules, RolloutPolicyBuilder.RolloutSection(s, target, rules, true, Now))!;
        // Un poste 3.1 refuserait ce document (propriété inconnue « rollout ») et garderait sa politique précédente : c'est
        // pourquoi la console ne l'envoie qu'aux postes qui le demandent (en-tête X-SpokaPilote-Rollout).
        var refused = Legacy.Policy.DriverPolicy.Parse(json40);
        Assert.False(refused.IsValid);
        Assert.Contains(refused.Errors, e => e.Message.Contains("rollout"));
        var section = JsonNode.Parse(json40)!["rollout"]!;
        Assert.Equal("Général", section["ring"]!.GetValue<string>());
        Assert.True(section["holdUnknown"]!.GetValue<bool>());
        Assert.Equal(Now.AddDays(6).ToString("O"), section["availability"]!["anneau-retenue-2"]!.GetValue<string>());
    }

    [Fact]
    public void Unidentified_clients_get_the_most_cautious_ring_and_suspensions_apply_to_everyone()
    {
        var rings = new[] { RolloutSamples.Pilot, RolloutSamples.Early, RolloutSamples.Ring(3, "Lent", 3, 2) }; // ordre inhabituel des délais
        var s = RolloutSamples.Snapshot(rings,
        [
            RolloutSamples.Version(1, "31.0.101.5600", Now.AddDays(-2.5)), // autorisée « Lent » (2 j), pas « Précoce » (3 j)
            RolloutSamples.Version(2, "31.0.101.5500", Now.AddDays(-60), suspended: true),
        ]);
        var plans = RolloutPlanner.Plan(s, Now);
        var cautious = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(null, true), Now, TimeZoneInfo.Utc);
        Assert.Contains(cautious.Rules, r => r["id"]!.GetValue<string>() == "anneau-retenue-1");
        Assert.Contains(cautious.Rules, r => r["id"]!.GetValue<string>() == "anneau-suspendu-2");
        Assert.Contains("anneau le plus prudent", cautious.Rules.First(r => r["id"]!.GetValue<string>() == "anneau-retenue-1")["reason"]!.GetValue<string>());

        var pilot = RolloutPolicyBuilder.Build(s, plans, new PolicyTarget(RolloutSamples.Pilot, false), Now, TimeZoneInfo.Utc);
        var suspension = Assert.Single(pilot.Rules);
        Assert.Null(suspension["kind"]); // suspension : tous les types, pilotes manquants compris
        var p31 = Parse31(RolloutPolicyBuilder.Compose(null, pilot, null)!);
        var decision = p31.Evaluate(Candidate("Intel - Display - 31.0.101.5500", "31.0.101.5500", kind: "MissingDriver"), null, DateTime.Now);
        Assert.True(decision.IsBlocked);
        Assert.Contains("suspendu automatiquement : 3 échecs sur 7 installations", decision.Reason);

        // Anneaux désactivés : seules les suspensions sont servies.
        var off = RolloutSamples.Snapshot(rings, s.Versions, enabled: false);
        var offRules = RolloutPolicyBuilder.Build(off, RolloutPlanner.Plan(off, Now), PolicyTarget.SuspensionsOnly, Now, TimeZoneInfo.Utc);
        Assert.Equal(["anneau-suspendu-2"], offRules.Rules.Select(r => r["id"]!.GetValue<string>()));
    }

    [Fact]
    public void Nothing_to_add_keeps_the_organization_document_byte_for_byte()
    {
        const string org = "{ \"name\": \"Parc\" /* commentaire */ }";
        Assert.Same(org, RolloutPolicyBuilder.Compose(org, new RolloutRules(), null));
        Assert.Null(RolloutPolicyBuilder.Compose(null, new RolloutRules(), null));
        Assert.Equal("{ pas du json", RolloutPolicyBuilder.Compose("{ pas du json", new RolloutRules { HoldUnknown = true }, new JsonObject()));
    }
}

// ====================================================================== suivi, anneaux, disjoncteur (base réelle, sans HTTP)

public sealed class RolloutTrackingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-rollout-" + Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private readonly ManualClock _clock = new(RolloutSamples.T0);
    private readonly AlertQueue _queue = new();
    private readonly RolloutService _rollout;

    public RolloutTrackingTests()
    {
        _store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        var options = new ServerOptions { DataDirectory = _dir };
        var settings = new AlertSettingsService(_store, new ConfigurationBuilder().Build(), options);
        settings.Update(new AlertSettingsUpdate
        {
            Webhooks = [new AlertSettingsUpdate.WebhookUpdate { Name = "Tests", Format = "json", Url = "https://hooks.exemple.fr/x" }],
            ConsoleUrl = "https://spokapilote.exemple.fr",
        }, _clock.Now);
        var alerts = new AlertService(_store, settings, _queue, _clock, NullLogger<AlertService>.Instance);
        var audit = new AuditService(_store, options, _clock, NullLogger<AuditService>.Instance);
        _rollout = new RolloutService(_store, alerts, audit, _clock, NullLogger<RolloutService>.Instance) { MinRebuildInterval = TimeSpan.Zero, Zone = TimeZoneInfo.Utc };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    private static readonly RolloutReports.Cand Intel = new("Intel - Display - 31.0.101.5590", "31.0.101.5590");

    private SaveResult Upload(string name, int machine, IEnumerable<RolloutReports.Cand> candidates, IEnumerable<RolloutReports.Out>? outcomes = null,
        DateTimeOffset? reportAt = null, IEnumerable<RolloutReports.Problem>? problems = null)
    {
        var json = RolloutReports.Json(name, machine, reportAt ?? _clock.Now, candidates, outcomes, problems);
        var report = ReportParser.Parse(json);
        var saved = _store.SaveReport(report, json, _clock.Now);
        _rollout.OnReportSaved(report, saved);
        return saved;
    }

    private RolloutVersion Only() => Assert.Single(_store.LoadRolloutSnapshot(_clock.Now).Versions);

    private long Group(string name, params long[] machines)
    {
        var id = _store.CreateGroup(name, null, new GroupRules(), _clock.Now)!.Value;
        _store.ChangeGroupMembers(id, machines, [], _clock.Now);
        return id;
    }

    [Fact]
    public void First_detection_is_the_reception_of_the_first_report_and_never_moves_back()
    {
        var saved = Upload("PC-01", 1, [Intel, new("Pilote sans version", ""), new("Realtek Audio", "6.0.9600.1", "Dell (catalogue officiel)", "Pilote manquant")],
            reportAt: _clock.Now.AddHours(-5)); // horloge du poste en retard : seule la réception compte
        Assert.Equal(2, saved.Rollout!.NewVersions);
        var versions = _store.LoadRolloutSnapshot(_clock.Now).Versions;
        Assert.Equal(2, versions.Count); // la proposition sans version n'est pas suivie
        var intel = versions.Single(v => v.Source == "WindowsUpdate");
        Assert.Equal(RolloutSamples.T0, intel.FirstSeen);
        Assert.Equal("PC-01", intel.FirstMachine);
        Assert.Equal("Intel - Display - *", intel.Family);
        Assert.Equal("Update", intel.Kind);
        Assert.Equal("MissingDriver", versions.Single(v => v.Source == "Dell").Kind);

        // Autre poste, deux jours plus tard : même version, première détection inchangée.
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, Upload("PC-02", 2, [Intel]).Rollout!.NewVersions);
        // Ancien rapport renvoyé (horodaté avant la première détection) : la date ne recule pas.
        _clock.Advance(TimeSpan.FromDays(1));
        Upload("PC-03", 3, [Intel], reportAt: RolloutSamples.T0.AddDays(-20));
        intel = _store.LoadRolloutSnapshot(_clock.Now).Versions.Single(v => v.Source == "WindowsUpdate");
        Assert.Equal(RolloutSamples.T0, intel.FirstSeen);
        Assert.Equal(_clock.Now, intel.LastSeen);
        // Même rapport reçu deux fois le même jour : aucune écriture (dernière détection au jour près).
        Assert.Same(RolloutTrack.None, Upload("PC-03", 3, [Intel]).Rollout);
    }

    [Fact]
    public void A_machine_in_several_rings_gets_the_latest_one_and_others_the_default_ring()
    {
        var a = Upload("PC-A", 1, []).MachineRef;
        var b = Upload("PC-B", 2, []).MachineRef;
        var c = Upload("PC-C", 3, []).MachineRef;
        var pilotGroup = Group("Informatique", a, b);
        var generalGroup = Group("Comptabilité", b);
        var pilot = _store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [pilotGroup]), _clock.Now)!.Value;
        var early = _store.CreateRing(new ServerStore.RingInput("Précoce", 3, false, 1, 0, null, []), _clock.Now)!.Value;
        var general = _store.CreateRing(new ServerStore.RingInput("Général", 10, false, 1, 0, null, [generalGroup]), _clock.Now)!.Value;

        Assert.Equal(pilot, _store.GetMachineRing(a));
        Assert.Equal(general, _store.GetMachineRing(b)); // Pilote ET Général : le plus tardif (prudent)
        Assert.Equal(general, _store.GetMachineRing(c)); // aucun groupe : anneau par défaut = le dernier
        Assert.Equal((true, (long?)general), _store.ResolveMachineRing(RolloutReports.MachineId(2), null));
        Assert.Equal((true, (long?)pilot), _store.ResolveMachineRing(null, "pc-a")); // nom seul (casse indifférente)
        Assert.Equal((false, (long?)general), _store.ResolveMachineRing(RolloutReports.MachineId(99), "PC-INCONNU"));

        _store.SaveRolloutSettings(new RolloutSettings { Enabled = true, DefaultRingId = early }, _clock.Now);
        Assert.Equal(early, _store.GetMachineRing(c));
        var (perRing, conflicts, total) = _store.GetRingMachineCounts();
        Assert.Equal((1, 1, 1), (perRing[pilot], perRing[early], perRing[general]));
        Assert.Equal(1, conflicts);
        Assert.Equal(3, total);

        // Réordonner : « Pilote » en dernier devient l'anneau le plus tardif.
        Assert.True(_store.ReorderRings([early, general, pilot], _clock.Now));
        Assert.Equal(pilot, _store.GetMachineRing(b));
        Assert.False(_store.ReorderRings([early, general], _clock.Now)); // incomplet : refusé
        // Groupe supprimé : l'anneau perd ce groupe, ses postes passent dans l'anneau par défaut.
        _store.DeleteGroup(pilotGroup);
        Assert.Equal(early, _store.GetMachineRing(a)); // anneau par défaut choisi plus haut
    }

    [Fact]
    public void Installations_feed_the_validation_of_the_next_ring()
    {
        var p1 = Upload("PC-P1", 1, []).MachineRef;
        var p2 = Upload("PC-P2", 2, []).MachineRef;
        var g = Group("Pilotes", p1, p2);
        var pilot = _store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [g]), _clock.Now)!.Value;
        var early = _store.CreateRing(new ServerStore.RingInput("Précoce", 3, true, 2, 0, null, []), _clock.Now)!.Value;
        _store.SaveRolloutSettings(new RolloutSettings { Enabled = true }, _clock.Now);
        Upload("PC-P1", 1, [Intel]);
        _clock.Advance(TimeSpan.FromDays(4));

        VersionPlan Plan() { _rollout.Changed(urgent: true); return _rollout.Current().Plans.Single(); }
        Assert.Equal(RingState.AwaitingValidation, Plan().For(early)!.State);

        var installed = new RolloutReports.Out(Intel.Title, Intel.Version, "Installed");
        Assert.True(Upload("PC-P1", 1, [], [installed]).Rollout!.StatsChanged);
        Assert.False(Plan().For(early)!.Eligible);
        Upload("PC-P2", 2, [], [installed]);
        Assert.Equal(new RingStat(2, 0, 0), Plan().For(pilot)!.Stat);
        Assert.True(Plan().For(early)!.Eligible);
        // Même rapport renvoyé : pas de double compte.
        Upload("PC-P2", 2, [], [installed]);
        Assert.Equal(2, Plan().For(pilot)!.Stat.Successes);
        // Un poste pilote échoue ensuite (réinstallation) : la validation est perdue (0 échec toléré).
        _clock.Advance(TimeSpan.FromHours(1));
        Upload("PC-P2", 2, [], [new(Intel.Title, Intel.Version, "Failed", "Code 0x80070103")]);
        Assert.Equal(new RingStat(1, 1, 0), Plan().For(pilot)!.Stat);
        Assert.False(Plan().For(early)!.Eligible);
        // Poste supprimé de la console : il ne compte plus.
        _store.DeleteMachine("PC-P2");
        Assert.Equal(new RingStat(1, 0, 0), Plan().For(pilot)!.Stat);
    }

    private List<Alert> Drain()
    {
        var list = new List<Alert>();
        while (_queue.Reader.TryRead(out var a)) list.Add(a);
        return list;
    }

    [Fact]
    public void The_breaker_suspends_a_version_after_enough_failures_in_the_window_then_can_be_resumed()
    {
        var machines = Enumerable.Range(1, 30).Select(i => Upload($"PC-P{i:D2}", i, i == 1 ? [Intel] : []).MachineRef).ToArray();
        var pilot = _store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [Group("Pilotes", machines)]), _clock.Now)!.Value;
        _store.SaveRolloutSettings(new RolloutSettings { Enabled = true }, _clock.Now);
        var ok = new RolloutReports.Out(Intel.Title, Intel.Version, "Installed");
        var ko = new RolloutReports.Out(Intel.Title, Intel.Version, "Failed", "Échec de pnputil (code 1)");
        var id = Only().Id;

        // 17 réussites puis 3 échecs : 3/20 = 15 % < 20 % → pas de suspension.
        for (var i = 1; i <= 17; i++) Upload($"PC-P{i:D2}", i, [], [ok]);
        for (var i = 18; i <= 20; i++) Upload($"PC-P{i:D2}", i, [], [ko]);
        Assert.False(Only().Suspended);
        // 2 échecs sur 2 (100 %) d'une autre version : sous le minimum de 3 échecs.
        Upload("PC-P21", 21, [], [ko with { Title = "Intel - Display - 31.0.101.5600", Version = "31.0.101.5600" }]); // version inconnue : ignorée

        // 4e échec : 4/21 = 19 % → toujours rien ; 5e : 5/22 = 22,7 % → suspension automatique.
        Upload("PC-P21", 21, [], [ko]);
        Assert.False(Only().Suspended);
        Upload("PC-P22", 22, [], [ko]);
        var v = Only();
        Assert.True(v.Suspended);
        Assert.True(v.SuspendAuto);
        Assert.Equal("suspendu automatiquement : 5 échecs sur 22 installations dans l'anneau « Pilote » en 48 h", v.SuspendReason);
        Assert.True(v.NeedsAttention);
        var alert = Assert.Single(Drain(), a => a.Reason == AlertReasons.RolloutSuspended);
        Assert.Equal("critical", alert.Severity);
        Assert.Equal("https://spokapilote.exemple.fr/#/deploiement", alert.Link);
        Assert.Contains("5 échecs sur 22 installations", alert.Message);
        var audit = _store.GetAudit(limit: 10);
        Assert.Contains(audit, e => e.Action == AuditActions.RolloutAutoSuspend && e.Actor == RolloutService.BreakerActor && e.Target == Intel.Title);
        // Suspension servie à tous les anneaux.
        var served = _rollout.Serve(RolloutReports.MachineId(1), "PC-P01", rolloutAware: false);
        Assert.Contains("anneau-suspendu-" + id, served.Json);
        Assert.Equal(1, _rollout.Current().Attention);

        // Reprise : les échecs antérieurs ne comptent plus ; 3 nouveaux échecs sur 3 tentatives → nouvelle suspension.
        Assert.True(_store.ResumeVersion(id, "alice", "fausse alerte", _clock.Now));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Upload("PC-P23", 23, [], [ko]);
        Upload("PC-P24", 24, [], [ko]);
        Assert.False(Only().Suspended);
        Upload("PC-P25", 25, [], [ko]);
        Assert.Equal("suspendu automatiquement : 3 échecs sur 3 installations dans l'anneau « Pilote » en 48 h", Only().SuspendReason);
        // Anti-spam : une alerte par version et par 24 h.
        Assert.Empty(Drain());

        // « Maintenir le blocage » : plus à examiner, toujours suspendu.
        Assert.True(_store.AcknowledgeSuspension(id, "bob", null, _clock.Now));
        Assert.False(Only().NeedsAttention);
        Assert.True(Only().Suspended);
        Assert.Contains(_store.GetRolloutEvents(id, 0, 10).Items, e => e.Action == "acknowledge" && e.Actor == "bob");
        _ = pilot;
    }

    [Fact]
    public void Failures_outside_the_window_do_not_count()
    {
        var machines = Enumerable.Range(1, 6).Select(i => Upload($"PC-{i}", i, i == 1 ? [Intel] : []).MachineRef).ToArray();
        _store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [Group("Pilotes", machines)]), _clock.Now);
        _store.SaveRolloutSettings(new RolloutSettings { Enabled = true, Breaker = new BreakerSettings { WindowHours = 48 } }, _clock.Now);
        var ko = new RolloutReports.Out(Intel.Title, Intel.Version, "Failed");
        Upload("PC-1", 1, [], [ko]);
        Upload("PC-2", 2, [], [ko]);
        _clock.Advance(TimeSpan.FromHours(49));
        Upload("PC-3", 3, [], [ko]);
        Upload("PC-4", 4, [], [ko]);
        Assert.False(Only().Suspended); // 2 échecs seulement dans les 48 dernières heures
        Upload("PC-5", 5, [], [ko]);
        Assert.True(Only().Suspended);
        Assert.Contains("3 échecs sur 3 installations", Only().SuspendReason);
    }

    [Fact]
    public void A_device_in_error_after_the_installation_counts_as_a_failure()
    {
        var machines = Enumerable.Range(1, 3).Select(i => Upload($"PC-{i}", i, i == 1 ? [Intel] : []).MachineRef).ToArray();
        var pilot = _store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [Group("Pilotes", machines)]), _clock.Now)!.Value;
        _store.SaveRolloutSettings(new RolloutSettings { Enabled = true }, _clock.Now);
        var ok = new RolloutReports.Out(Intel.Title, Intel.Version, "Installed");
        for (var i = 1; i <= 3; i++) Upload($"PC-{i}", i, [], [ok]);
        _clock.Advance(TimeSpan.FromHours(2));
        // Rapport suivant : la carte graphique, avec la version installée, est en erreur (code 43).
        for (var i = 1; i <= 3; i++)
        {
            var track = Upload($"PC-{i}", i, [], problems: [new("NVIDIA RTX A2000", "31.0.101.5590")]).Rollout!;
            Assert.True(track.StatsChanged);
        }
        _rollout.Changed(urgent: true);
        Assert.Equal(new RingStat(0, 3, 3), _rollout.Current().Plans.Single().For(pilot)!.Stat);
        Assert.True(Only().Suspended);
        var failure = _store.GetRolloutFailures(Only().Id).First();
        Assert.True(failure.Regression);
        Assert.Contains("« NVIDIA RTX A2000 » en erreur après l'installation", failure.Message);
        // Le même périphérique toujours en erreur au rapport suivant : pas compté deux fois.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Same(RolloutTrack.None, Upload("PC-1", 1, [], problems: [new("NVIDIA RTX A2000", "31.0.101.5590")]).Rollout);
    }
}

// ====================================================================== API : politique effective, droits

public sealed class RolloutApiTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public RolloutApiTests()
    {
        var rollout = _host.Factory.Services.GetRequiredService<RolloutService>();
        rollout.MinRebuildInterval = TimeSpan.Zero;
        rollout.Zone = TimeZoneInfo.Utc;
    }

    public void Dispose() => _host.Dispose();

    private HttpClient Client(string? token) => _host.Client(token);

    private async Task Report(int machine, string name, params RolloutReports.Cand[] candidates)
    {
        var r = await Client(AgentToken).PostAsync("/api/reports", Json(RolloutReports.Json(name, machine, _host.Clock.Now, candidates)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private HttpClient Agent(int? machine, string? name, bool rollout)
    {
        var c = Client(AgentToken);
        if (machine is { } m) c.DefaultRequestHeaders.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(m));
        if (name is not null) c.DefaultRequestHeaders.Add("X-SpokaPilote-Machine", name);
        if (rollout) c.DefaultRequestHeaders.Add("X-SpokaPilote-Rollout", "1");
        return c;
    }

    private async Task<string> Policy(HttpClient client)
    {
        var r = await client.GetAsync("/api/policy");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    private static IEnumerable<string> Ids(string json) =>
        JsonNode.Parse(json)!["rules"]!.AsArray().Select(r => r!["id"]?.GetValue<string>() ?? "");

    /// <summary>Deux postes pilotes (groupe « Informatique »), deux postes ordinaires ; anneaux suggérés + groupe.</summary>
    private async Task<(long Pilot, long General)> SetupAsync()
    {
        var admin = Client(AdminToken);
        var intel = new RolloutReports.Cand("Intel - Display - 31.0.101.5590", "31.0.101.5590");
        await Report(1, "PC-IT-01", intel);
        await Report(2, "PC-IT-02");
        await Report(3, "PC-COMPTA-01");
        await Report(4, "PC-COMPTA-02");
        var group = await ReadJson(await admin.PostAsync("/api/groups", Json(new
        {
            name = "Informatique", rules = new { match = "all", conditions = new[] { new { field = "name", @operator = "startsWith", value = "PC-IT-" } } },
        })));
        var overview = await ReadJson(await admin.PostAsync("/api/rollout/rings/suggested", null));
        var rings = overview.GetProperty("rings").EnumerateArray().ToList();
        Assert.Equal(["Pilote", "Précoce", "Général"], rings.Select(r => r.GetProperty("name").GetString()));
        var pilot = rings[0].GetProperty("id").GetInt64();
        var general = rings[2].GetProperty("id").GetInt64();
        var put = await admin.PutAsync($"/api/rollout/rings/{pilot}", Json(new { name = "Pilote", delayDays = 0, groups = new[] { group.GetProperty("id").GetInt64() } }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var settings = await admin.PutAsync("/api/rollout/settings", Json(new { enabled = true, defaultRingId = general, breaker = new { enabled = true, minFailures = 3, minFailureRatePercent = 20, windowHours = 48 } }));
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        return (pilot, general);
    }

    [Fact]
    public async Task Each_machine_receives_the_policy_of_its_ring_and_the_console_still_edits_the_raw_document()
    {
        const string org = "{ \"schema\": 1, \"name\": \"Parc siège\", \"version\": 3, \"rules\": [ { \"id\": \"gel\", \"action\": \"block\", \"title\": \"Conexant*\" } ] }";
        var admin = Client(AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/policy", Json(org))).StatusCode);
        // Sans anneau : le document tel quel, comme en 3.x.
        Assert.Equal(org, await Policy(Agent(3, "PC-COMPTA-01", rollout: true)));

        await SetupAsync();
        _host.Clock.Advance(TimeSpan.FromHours(1));

        // Poste pilote : version autorisée, aucune retenue ; section « rollout » demandée.
        var pilotJson = await Policy(Agent(1, "PC-IT-01", rollout: true));
        Assert.DoesNotContain(Ids(pilotJson), id => id.StartsWith("anneau-retenue-"));
        Assert.Equal("Pilote", JsonNode.Parse(pilotJson)!["rollout"]!["ring"]!.GetValue<string>());
        Assert.Equal("gel", Ids(pilotJson).Last());

        // Poste ordinaire (anneau par défaut « Général ») : retenue datée + familles.
        var r = await Agent(3, "PC-COMPTA-01", rollout: true).GetAsync("/api/policy");
        Assert.Equal("G%C3%A9n%C3%A9ral", r.Headers.GetValues("X-SpokaPilote-Ring").Single());
        var generalJson = await r.Content.ReadAsStringAsync();
        var hold = JsonNode.Parse(generalJson)!["rules"]!.AsArray().First(x => x!["id"]!.GetValue<string>().StartsWith("anneau-retenue-"))!;
        Assert.Equal("Pilote Intel - Display - 31.0.101.5590 : disponible pour votre anneau « Général » le 06/12/2026 à 09:00", hold["reason"]!.GetValue<string>());
        Assert.Equal("WindowsUpdate", hold["source"]!.GetValue<string>());
        Assert.Contains(Ids(generalJson), id => id.StartsWith("anneau-famille-"));
        Assert.True(JsonNode.Parse(generalJson)!["rollout"]!["machineKnown"]!.GetValue<bool>());

        // Sans en-tête « rollout » (poste 3.x) : pas de section, document accepté par l'analyseur 3.1.
        var legacy = await Policy(Agent(null, null, rollout: false));
        Assert.Null(JsonNode.Parse(legacy)!["rollout"]);
        Assert.True(Legacy.Policy.DriverPolicy.Parse(legacy).IsValid);
        Assert.Contains(Ids(legacy), id => id.StartsWith("anneau-retenue-")); // anneau le plus prudent

        // La console (jeton administrateur, comptes) lit toujours le document enregistré.
        Assert.Equal(org, await (await admin.GetAsync("/api/policy")).Content.ReadAsStringAsync());

        // Diffusion forcée : plus aucune retenue, même pour le poste non identifié.
        var id = (await ReadJson(await admin.GetAsync("/api/rollout/versions"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{id}/approve", Json(new { comment = "correctif urgent" }))).StatusCode);
        Assert.DoesNotContain(Ids(await Policy(Agent(null, null, false))), x => x.StartsWith("anneau-retenue-"));
        // Après 10 jours la version est autorisée partout ; la retenue n'est plus servie (seulement les règles de famille).
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/rollout/versions/{id}/unapprove", null)).StatusCode);
        _host.Clock.Advance(TimeSpan.FromDays(10));
        Assert.DoesNotContain(Ids(await Policy(Agent(3, "PC-COMPTA-01", true))), x => x.StartsWith("anneau-retenue-"));
        Assert.Equal("completed", (await ReadJson(await admin.GetAsync($"/api/rollout/versions/{id}"))).GetProperty("version").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Roles_can_view_technicians_suspend_and_administrators_configure()
    {
        await SetupAsync();
        await _host.CreateUserAsync("lecteur", "readonly");
        await _host.CreateUserAsync("tech", "technician");
        await _host.CreateUserAsync("chef", "admin");
        var reader = await _host.LoginAsync("lecteur");
        var tech = await _host.LoginAsync("tech");
        var chef = await _host.LoginAsync("chef");
        var agent = Client(AgentToken);
        var id = (await ReadJson(await reader.GetAsync("/api/rollout/versions?status=all"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        var ring = (await ReadJson(await reader.GetAsync("/api/rollout"))).GetProperty("rings")[0].GetProperty("id").GetInt64();

        foreach (var c in new[] { reader, tech, chef })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/rollout")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/rollout/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/rollout/versions/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/rollout/history")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/rollout/preview?machine=PC-IT-01")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/rollout")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/rollout/versions/{id}/suspend", Json(new { reason = "x" }))).StatusCode);

        // Technicien : suspendre, reprendre ; pas les anneaux, les réglages ni la diffusion forcée.
        Assert.Equal(HttpStatusCode.OK, (await tech.PostAsync($"/api/rollout/versions/{id}/suspend", Json(new { reason = "écrans bleus (ticket #4512)" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await tech.PostAsync($"/api/rollout/versions/{id}/suspend", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tech.PostAsync($"/api/rollout/versions/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync($"/api/rollout/versions/{id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync("/api/rollout/rings", Json(new { name = "X", delayDays = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PutAsync($"/api/rollout/rings/{ring}", Json(new { name = "X", delayDays = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.DeleteAsync($"/api/rollout/rings/{ring}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PutAsync("/api/rollout/settings", Json(new { enabled = false }))).StatusCode);

        // Administrateur : tout, avec validation des valeurs.
        Assert.Equal(HttpStatusCode.BadRequest, (await chef.PostAsync("/api/rollout/rings", Json(new { name = "Lent", delayDays = 400 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await chef.PostAsync("/api/rollout/rings", Json(new { name = "pilote", delayDays = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await chef.PostAsync("/api/rollout/rings", Json(new { name = "Lent", delayDays = 30 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await chef.PutAsync("/api/rollout/settings", Json(new { enabled = true, breaker = new { minFailureRatePercent = 0 } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await chef.PostAsync($"/api/rollout/versions/{id}/approve", null)).StatusCode);

        // Tout est consigné, avec l'auteur.
        var audit = await ReadJson(await Client(AdminToken).GetAsync("/api/audit?category=rollout&limit=50"));
        var entries = audit.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("action").GetString(), e.GetProperty("actor").GetString())).ToList();
        Assert.Contains(("rollout.suspend", "tech"), entries);
        Assert.Contains(("rollout.resume", "tech"), entries);
        Assert.Contains(("rollout.ring-create", "chef"), entries);
        Assert.Contains(("rollout.approve", "chef"), entries);
        Assert.Contains(entries, e => e.Item1 == "rollout.settings");
        var history = await ReadJson(await reader.GetAsync("/api/rollout/history"));
        Assert.Contains(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "suspend" && e.GetProperty("actor").GetString() == "tech");
    }

    [Fact]
    public async Task The_version_list_is_paged_filtered_and_searched_by_the_server()
    {
        await SetupAsync();
        for (var i = 0; i < 30; i++) await Report(10 + i, $"PC-X{i:D2}", new RolloutReports.Cand($"Pilote {i:D2} - 1.{i}.0.0", $"1.{i}.0.0"));
        var admin = Client(AdminToken);
        var page = await ReadJson(await admin.GetAsync("/api/rollout/versions?status=rollout&offset=10&limit=10"));
        Assert.Equal(31, page.GetProperty("total").GetInt32());
        Assert.Equal(10, page.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.GetProperty("rings").GetArrayLength());
        var item = page.GetProperty("items")[0];
        Assert.Equal(3, item.GetProperty("rings").GetArrayLength());
        Assert.Equal("allowed", item.GetProperty("rings")[0].GetProperty("state").GetString());
        Assert.Equal("waiting", item.GetProperty("rings")[2].GetProperty("state").GetString());
        Assert.StartsWith("en attente jusqu'au ", item.GetProperty("rings")[2].GetProperty("label").GetString());
        Assert.Equal(1, (await ReadJson(await admin.GetAsync("/api/rollout/versions?q=Intel"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ReadJson(await admin.GetAsync("/api/rollout/versions?status=suspended"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/rollout/versions?status=n-importe")).StatusCode);
    }
}

// ====================================================================== migration depuis une base 3.2 (schéma 4)

public sealed class MigrationFrom32Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-mig32-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    [Fact]
    public void A_32_database_is_upgraded_without_rewriting_anything_and_tracks_rollout_afterwards()
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "spokapilote.db");
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // Schéma exact d'une base créée par la console 3.2 (copie figée de sqlite_master), puis un poste, un groupe, la politique.
            cmd.CommandText = Schema32.Sql + """
                INSERT INTO meta(key, value) VALUES('fleet_rev', 1);
                INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json, report_count, display_model)
                    VALUES('PC-ANCIEN', '00000001-5150-0000-0000-000000000000', '2026-11-20T08:00:00.0000000Z', '2026-11-20T08:00:00.0000000Z', '{"name":"PC-ANCIEN"}', 1, 'Dell');
                INSERT INTO machine_groups(name, rules_json, created_at, updated_at) VALUES('Informatique', '{"match":"all","conditions":[]}', '2026-11-01T00:00:00.0000000Z', '2026-11-01T00:00:00.0000000Z');
                INSERT INTO group_members(group_id, machine_ref) VALUES(1, 1);
                INSERT INTO documents(key, json, updated_at) VALUES('policy', '{"name":"Parc","rules":[]}', '2026-11-01T00:00:00.0000000Z');
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        Assert.Equal(4, store.OpenedSchemaVersion);
        Assert.True(store.MigrationDuration < TimeSpan.FromSeconds(5));
        Assert.Equal("PC-ANCIEN", Assert.Single(store.GetMachineSummaries()).Name);
        Assert.Equal("{\"name\":\"Parc\",\"rules\":[]}", store.GetDocument(ServerStore.PolicyKey)!.Json);
        Assert.Empty(store.GetRings());
        Assert.False(store.GetRolloutSettings().Enabled); // diffusion par anneaux désactivée après la mise à jour

        // Le poste existant (identifiant conservé) est suivi dès son rapport suivant.
        var json = RolloutReports.Json("PC-ANCIEN", 1, RolloutSamples.T0, [new("Intel - Display - 31.0.101.5590", "31.0.101.5590")]);
        var saved = store.SaveReport(ReportParser.Parse(json), json, RolloutSamples.T0);
        Assert.Equal(1, saved.MachineRef);
        Assert.Equal(1, saved.Rollout!.NewVersions);
        var ring = store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [1]), RolloutSamples.T0)!.Value;
        Assert.Equal(ring, store.GetMachineRing(1));

        SqliteConnection.ClearAllPools();
        var again = new ServerStore(new ServerOptions { DataDirectory = _dir });
        Assert.Equal(ServerStore.SchemaVersion, again.OpenedSchemaVersion);
        Assert.Single(again.LoadRolloutSnapshot(RolloutSamples.T0).Versions);
    }
}

// ====================================================================== mesures (20 000 postes × 300 versions)

public sealed class RolloutPerformanceTests : IDisposable
{
    private const int Machines = 20_000;
    private const int Versions = 300;
    private readonly TestServerHost _host = new();
    private readonly ITestOutputHelper _output;

    public RolloutPerformanceTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Parc synthétique inséré directement en base (20 000 postes, 3 anneaux par groupes, 300 versions de 60 familles vues
    /// au cours des 30 derniers jours, compteurs d'installation) : mesure la politique effective servie à des postes pris au
    /// hasard, la taille du document, et l'enregistrement d'un rapport (proportionnel au rapport, pas au parc).
    /// </summary>
    [Fact]
    public async Task Effective_policy_requests_stay_fast_on_20000_machines_and_300_versions()
    {
        var store = _host.Factory.Services.GetRequiredService<ServerStore>();
        var rollout = _host.Factory.Services.GetRequiredService<RolloutService>();
        var now = _host.Clock.Now;
        var seed = Stopwatch.StartNew();
        var pilotGroup = store.CreateGroup("Pilotes", null, new GroupRules(), now)!.Value;
        var earlyGroup = store.CreateGroup("Précoces", null, new GroupRules(), now)!.Value;
        store.CreateRing(new ServerStore.RingInput("Pilote", 0, false, 1, 0, null, [pilotGroup]), now);
        store.CreateRing(new ServerStore.RingInput("Précoce", 3, true, 5, 0, null, [earlyGroup]), now);
        store.CreateRing(new ServerStore.RingInput("Général", 10, true, 20, 0, null, []), now);
        store.SaveRolloutSettings(new RolloutSettings { Enabled = true }, now);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            using var m = c.CreateCommand();
            m.CommandText = "INSERT INTO machines(name, machine_id, last_seen, last_report, summary_json) VALUES($n, $id, $at, $at, '{}');";
            var n = m.Parameters.Add("$n", SqliteType.Text);
            var mid = m.Parameters.Add("$id", SqliteType.Text);
            m.Parameters.AddWithValue("$at", ServerStore.Iso(now));
            for (var i = 1; i <= Machines; i++) { n.Value = $"PC-{i:D5}"; mid.Value = RolloutReports.MachineId(i); m.ExecuteNonQuery(); }
            using var g = c.CreateCommand();
            g.CommandText = $"""
                INSERT INTO group_members(group_id, machine_ref) SELECT {pilotGroup}, id FROM machines WHERE id % 100 = 0;
                INSERT INTO group_members(group_id, machine_ref) SELECT {earlyGroup}, id FROM machines WHERE id % 10 = 1;
                """;
            g.ExecuteNonQuery();
            using var v = c.CreateCommand();
            v.CommandText = """
                INSERT INTO rollout_versions(source, title, version, family, kind, first_seen, last_seen) VALUES($s, $t, $v, $f, 'Update', $at, $at);
                INSERT INTO rollout_stats(version_id, ring_id, successes, failures) VALUES(last_insert_rowid(), 1, $ok, 0);
                """;
            var (s, t, ver, f, at, ok) = (v.Parameters.Add("$s", SqliteType.Text), v.Parameters.Add("$t", SqliteType.Text), v.Parameters.Add("$v", SqliteType.Text),
                v.Parameters.Add("$f", SqliteType.Text), v.Parameters.Add("$at", SqliteType.Text), v.Parameters.Add("$ok", SqliteType.Integer));
            for (var i = 0; i < Versions; i++)
            {
                var family = i % 60;
                var version = $"{30 + family % 3}.0.{family}.{1000 + i}";
                s.Value = (family % 4) switch { 0 => "WindowsUpdate", 1 => "Dell", 2 => "HP", _ => "Lenovo" };
                t.Value = $"Fabricant {family} - Pilote {family} - {version}";
                ver.Value = version;
                f.Value = $"Fabricant {family} - Pilote {family} - *";
                at.Value = ServerStore.Iso(now.AddDays(-30 + i / 10.0));
                ok.Value = i % 7;
                v.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _output.WriteLine($"Parc synthétique inséré en {seed.ElapsedMilliseconds} ms.");
        rollout.Changed(urgent: true);

        var view = rollout.Current();
        Assert.Equal(Versions, view.Plans.Count);
        var build = Stopwatch.StartNew();
        rollout.Changed(urgent: true);
        rollout.Current();
        var buildMs = build.Elapsed.TotalMilliseconds;

        var random = new Random(42);
        var clients = Enumerable.Range(0, 8).Select(_ => _host.Client(AgentToken)).ToList();
        async Task<(double Ms, int Bytes)> One(int i)
        {
            var machine = random.Next(1, Machines + 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/policy");
            request.Headers.Add("X-SpokaPilote-Machine-Id", RolloutReports.MachineId(machine));
            request.Headers.Add("X-SpokaPilote-Machine", $"PC-{machine:D5}");
            request.Headers.Add("X-SpokaPilote-Rollout", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            var sw = Stopwatch.StartNew();
            using var r = await clients[i % clients.Count].SendAsync(request);
            var body = await r.Content.ReadAsByteArrayAsync();
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (sw.Elapsed.TotalMilliseconds, body.Length);
        }
        for (var i = 0; i < 50; i++) await One(i); // préchauffage (JIT, cache des trois anneaux)
        var timings = new List<(double Ms, int Bytes)>();
        for (var i = 0; i < 2000; i++) timings.Add(await One(i));
        var sorted = timings.Select(x => x.Ms).Order().ToList();
        double P(double q) => sorted[(int)Math.Ceiling(q * sorted.Count) - 1];

        // Taille du document (non compressé) pour chaque anneau, et forme compressée.
        var sizes = new List<string>();
        foreach (var ring in view.Snapshot.Rings)
        {
            var served = rollout.Serve(null, null, false) is { } _ ? rollout.Current().Policies.Values.FirstOrDefault(p => p.Target.Ring?.Id == ring.Id) : null;
            if (served is not null) sizes.Add($"{ring.Name} : {served.Rules.Holds} retenues, {served.Rules.Families} familles, {served.Utf8!.Length / 1024.0:0.0} Ko ({(served.Gzip?.Length ?? served.Utf8.Length) / 1024.0:0.0} Ko compressé)");
        }

        // Enregistrement d'un rapport de 15 propositions et 5 installations sur ce parc.
        var uploads = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var machine = 1 + i * 97 % Machines;
            var cands = Enumerable.Range(0, 15).Select(k => (i * 15 + k) % Versions).Select(k =>
                new RolloutReports.Cand($"Fabricant {k % 60} - Pilote {k % 60} - {30 + k % 60 % 3}.0.{k % 60}.{1000 + k}", $"{30 + k % 60 % 3}.0.{k % 60}.{1000 + k}",
                    (k % 60 % 4) switch { 0 => "Windows Update", 1 => "Dell (catalogue officiel)", 2 => "HP (catalogue officiel)", _ => "Lenovo (catalogue officiel)" })).ToList();
            var outs = cands.Take(5).Select(c => new RolloutReports.Out(c.Title, c.Version, "Installed")).ToList();
            var json = RolloutReports.Json($"PC-{machine:D5}", machine, now.AddMinutes(i), cands, outs);
            var report = ReportParser.Parse(json);
            var sw = Stopwatch.StartNew();
            var saved = store.SaveReport(report, json, now.AddMinutes(i));
            rollout.OnReportSaved(report, saved);
            uploads.Add(sw.Elapsed.TotalMilliseconds);
        }
        var up = uploads.Order().ToList();

        _output.WriteLine($"Politique effective (20 000 postes, {Versions} versions) : p50 {P(0.50):0.00} ms, p95 {P(0.95):0.00} ms, p99 {P(0.99):0.00} ms, " +
                          $"max {sorted[^1]:0.00} ms sur {sorted.Count} requêtes ; corps reçu moyen {timings.Average(x => x.Bytes) / 1024.0:0.0} Ko.");
        _output.WriteLine($"Recalcul de l'état de diffusion : {buildMs:0.0} ms ; {string.Join(" · ", sizes)}");
        _output.WriteLine($"Rapport (15 propositions, 5 installations) : p50 {up[up.Count / 2]:0.00} ms, p95 {up[(int)(up.Count * 0.95)]:0.00} ms.");
        Assert.True(P(0.95) < 20, $"p95 = {P(0.95):0.00} ms");
        Assert.True(up[(int)(up.Count * 0.95)] < 50, $"rapport p95 = {up[(int)(up.Count * 0.95)]:0.00} ms");
    }
}
