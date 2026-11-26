using System.Globalization;
using System.Net;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Diffusion par anneaux côté poste (4.0) : section « rollout » servie par la console, retenues datées affichées à
/// l'heure locale, versions de familles inconnues retenues, compatibilité des règles avec les postes 3.x.
/// </summary>
public sealed class RolloutPolicyTests
{
    private static readonly DateTimeOffset Available = new(2026, 12, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Forme exacte servie par la console 4.0 à un poste de l'anneau « Général » (voir RolloutPolicyBuilder côté serveur).</summary>
    private const string Rules = """
        {
          "schema": 1, "name": "Parc siège", "version": 7,
          "rules": [
            { "id": "anneau-suspendu-9", "action": "block", "source": "WindowsUpdate", "title": "Realtek - Audio - 6.0.9600.1", "versions": "6.0.9600.1",
              "reason": "Pilote Realtek - Audio - 6.0.9600.1 : suspendu automatiquement : 3 échecs sur 7 installations dans l'anneau « Pilote » en 48 h" },
            { "id": "anneau-retenue-12", "action": "block", "source": "WindowsUpdate", "title": "Intel - Display - 31.0.101.5600",
              "kind": ["Update", "Optional", "Firmware"], "versions": "31.0.101.5600",
              "reason": "Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 06/12/2026 à 10:00" },
            { "id": "anneau-retenue-13", "action": "block", "source": "Dell", "title": "Dell BIOS 1.24.0", "kind": ["Update", "Optional", "Firmware"],
              "versions": "1.24.0", "reason": "Pilote Dell BIOS 1.24.0 : disponible pour votre anneau « Général » après validation dans l'anneau « Précoce » (1/3 installation(s) réussie(s), 0 échec)" },
            { "id": "anneau-famille-1", "action": "pin", "source": "WindowsUpdate", "title": "Intel - Display - *", "kind": ["Update", "Optional", "Firmware"],
              "pinVersion": "31.0.101.5590", "reason": "Diffusion par anneaux (anneau « Général ») : version de « Intel - Display - * » postérieure à 31.0.101.5590 pas encore autorisée pour cet anneau" },
            { "id": "anneau-famille-2", "action": "block", "source": "Dell", "title": "Dell BIOS *", "kind": ["Update", "Optional", "Firmware"],
              "reason": "Diffusion par anneaux (anneau « Général ») : « Dell BIOS * » en cours de diffusion, aucune version encore autorisée pour cet anneau" },
            { "id": "urgence", "action": "allow", "title": "Intel - Display - *", "reason": "Autorisation de l'organisation" }
          ]
        """;

    private static string Section => $$"""
          "rollout": { "ring": "Général", "ringPosition": 3, "ringCount": 3, "delayDays": 10, "validationRing": "Précoce", "minSuccesses": 3,
            "maxFailures": 0, "enabled": true, "conservative": false, "machineKnown": true, "holdUnknown": true, "applyToMissingDrivers": false,
            "generatedAt": "2026-11-26T09:00:00.0000000+00:00", "futureField": { "x": 1 },
            "availability": { "anneau-retenue-12": "{{Available:O}}" } }
        """;

    private static string Served(bool withSection = true) => Rules + (withSection ? ",\n" + Section : "") + "\n}";

    private static DriverPolicy Parse(string json)
    {
        var r = DriverPolicy.Parse(json);
        Assert.True(r.IsValid, string.Join(" ; ", r.Errors));
        return DriverPolicy.Combine(r.Policy, null);
    }

    private static DriverCandidate Cand(string title, string? version, DriverSourceKind source = DriverSourceKind.WindowsUpdate,
        CandidateKind kind = CandidateKind.Update, bool informational = false) =>
        new() { Id = title, Source = source, Title = title, Version = version, Kind = kind, IsInformational = informational };

    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Dated_holds_are_shown_in_local_time_and_marked_as_rollout_holds()
    {
        var p = Parse(Served());
        Assert.Equal("Général", p.Rollout!.Ring);
        Assert.Equal(5, p.Rules.Count(r => r.IsRollout));
        Assert.False(p.Rules.Single(r => r.Id == "urgence").IsRollout);

        var d = p.Evaluate(Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600"), null, Now);
        Assert.True(d.IsRolloutHold);
        Assert.Equal(Available, d.AvailableAt);
        var local = Available.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm", CultureInfo.GetCultureInfo("fr-FR"));
        Assert.Equal($"Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le {local}", d.Reason);

        // Retenue sans date (validation en attente) : motif de la console, sans référence de règle ajoutée.
        var bios = p.Evaluate(Cand("Dell BIOS 1.24.0", "1.24.0", DriverSourceKind.Dell, CandidateKind.Firmware), null, Now);
        Assert.True(bios.IsRolloutHold);
        Assert.Null(bios.AvailableAt);
        Assert.EndsWith("(1/3 installation(s) réussie(s), 0 échec)", bios.Reason);

        // Suspension : tous types, pilotes manquants compris.
        var suspended = p.Evaluate(Cand("Realtek - Audio - 6.0.9600.1", "6.0.9600.1", kind: CandidateKind.MissingDriver), null, Now);
        Assert.True(suspended.IsRolloutHold);
        Assert.Contains("suspendu automatiquement", suspended.Reason);
    }

    [Fact]
    public void Unknown_families_are_held_known_newer_versions_are_pinned_and_an_allow_rule_does_not_lift_a_hold()
    {
        var p = Parse(Served());
        // Famille jamais vue par la console : retenue (afflux à heure fixe, avant tout rapport).
        var unknown = p.Evaluate(Cand("Contoso - Camera - 2.4.0.1", "2.4.0.1"), null, Now);
        Assert.True(unknown.IsRolloutHold);
        Assert.Equal("rollout", unknown.Setting);
        Assert.Contains("nouveau pilote, pas encore vu par la console", unknown.Reason);
        Assert.Contains("votre anneau « Général »", unknown.Reason);
        Assert.Contains("après validation dans l'anneau « Précoce »", unknown.Reason);
        // Non soumis : pilote manquant (sauf réglage de la console), pilote informatif, version illisible.
        Assert.False(p.Evaluate(Cand("Contoso - Camera - 2.4.0.1", "2.4.0.1", kind: CandidateKind.MissingDriver), null, Now).IsBlocked);
        Assert.False(p.Evaluate(Cand("Pilote graphique Intel", "31.0", DriverSourceKind.Intel, informational: true), null, Now).IsBlocked);
        Assert.False(p.Evaluate(Cand("Contoso Camera", null), null, Now).IsBlocked);

        // Famille connue : version ≤ dernière autorisée → passe la règle de famille, puis l'« allow » de l'organisation.
        var ok = p.Evaluate(Cand("Intel - Display - 31.0.101.5590", "31.0.101.5590"), null, Now);
        Assert.True(ok.IsExplicitAllow);
        // Version plus récente, jamais vue : retenue par la règle de famille, que l'« allow » placé après ne lève pas.
        var newer = p.Evaluate(Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700"), null, Now);
        Assert.True(newer.IsRolloutHold);
        Assert.Contains("postérieure à 31.0.101.5590", newer.Reason);
        Assert.DoesNotContain("(règle", newer.Reason);
        // Pilote manquant d'une famille connue : jamais retenu par la diffusion.
        Assert.True(p.Evaluate(Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600", kind: CandidateKind.MissingDriver), null, Now).IsExplicitAllow);
    }

    [Fact]
    public void Apply_marks_held_candidates_so_that_interfaces_show_them_like_blocked_ones()
    {
        var p = Parse(Served());
        var held = Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600");
        var blocked = Cand("Conexant Audio", "1.0");
        var list = new List<DriverCandidate> { held, blocked };
        var org = DriverPolicy.Parse(Served().Replace("""{ "id": "urgence",""", """{ "id": "gel", "action": "block", "title": "Conexant*", "reason": "gel" }, { "id": "urgence","""));
        var policy = DriverPolicy.Combine(org.Policy, null);
        var (merged, decisions) = DriverScanEngine.ApplyPolicy(list, [], policy, Now);
        Assert.Equal(2, decisions.Count);
        Assert.True(held.HeldByRollout);
        Assert.Equal(Available, held.RolloutAvailableAt);
        Assert.Equal("Général", held.RolloutRing);
        Assert.NotNull(held.BlockedReason);
        Assert.False(blocked.HeldByRollout);
        Assert.Equal("gel (règle « gel »)", blocked.BlockedReason);
        Assert.Equal(2, merged.Count);
        _ = p;
    }

    [Fact]
    public void Without_the_rollout_section_the_same_rules_block_exactly_as_on_a_31_client()
    {
        // Document servi à un poste 3.x (pas d'en-tête X-SpokaPilote-Rollout) : mêmes règles, sans section.
        var p = Parse(Served(withSection: false));
        Assert.Null(p.Rollout);
        Assert.DoesNotContain(p.Rules, r => r.IsRollout); // simples règles de la politique
        var d = p.Evaluate(Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600"), null, Now);
        Assert.True(d.IsBlocked);
        Assert.False(d.IsRolloutHold);
        Assert.StartsWith("Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 06/12/2026", d.Reason);
        Assert.True(p.Evaluate(Cand("Intel - Display - 31.0.101.5700", "31.0.101.5700"), null, Now).IsBlocked);
        // Pas de retenue des familles inconnues sans la section (comportement 3.x).
        Assert.False(p.Evaluate(Cand("Contoso - Camera - 2.4.0.1", "2.4.0.1"), null, Now).IsBlocked);
    }

    [Fact]
    public void Rollout_section_tolerates_new_fields_but_unknown_top_level_properties_are_still_refused()
    {
        Assert.True(DriverPolicy.Parse(Served()).IsValid); // « futureField » dans la section : ignoré
        var bad = DriverPolicy.Parse("""{ "name": "x", "rolout": {} }""");
        Assert.False(bad.IsValid);
        Assert.Contains("rolout", bad.Errors[0].Message);
    }

    [Fact]
    public void Summary_names_the_ring_and_counts_only_organization_rules()
    {
        var org = DriverPolicy.Parse(Served()).Policy!;
        var loaded = new LoadedPolicy { Effective = DriverPolicy.Combine(org, null), Organization = org };
        Assert.Contains("(1 règle(s)", loaded.Summary);
        Assert.Contains("diffusion par anneaux : anneau « Général », 2 version(s) retenue(s), 1 suspendue(s)", loaded.Summary);
    }

    [Fact]
    public void A_policy_with_300_held_versions_and_60_families_is_read_and_applied_quickly()
    {
        // Taille servie à l'anneau « Général » du parc de mesure (20 000 postes, 300 versions en cours de diffusion).
        var rules = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var family = i % 60;
            var version = $"{30 + family % 3}.0.{family}.{1000 + i}";
            rules.Add($$"""{ "id": "anneau-retenue-{{i}}", "action": "block", "source": "WindowsUpdate", "title": "Fabricant {{family}} - Pilote {{family}} - {{version}}", "kind": ["Update", "Optional", "Firmware"], "versions": "{{version}}", "reason": "Pilote {{i}} : disponible pour votre anneau « Général » le 06/12/2026 à 10:00" }""");
        }
        for (var f = 0; f < 60; f++)
            rules.Add($$"""{ "id": "anneau-famille-{{f}}", "action": "block", "source": "WindowsUpdate", "title": "Fabricant {{f}} - Pilote {{f}} - *", "kind": ["Update", "Optional", "Firmware"], "reason": "Famille {{f}} en cours de diffusion" }""");
        var json = $$"""{ "schema": 1, "name": "Parc", "rules": [ {{string.Join(",\n", rules)}} ], "rollout": { "ring": "Général", "enabled": true, "holdUnknown": true, "delayDays": 10 } }""";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = Parse(json);
        var candidates = Enumerable.Range(0, 30).Select(i => Cand($"Fabricant {i} - Pilote {i} - 31.0.{i}.9999", $"31.0.{i}.9999")).ToList();
        foreach (var c in candidates) Assert.True(p.Evaluate(c, null, Now).IsRolloutHold);
        sw.Stop();
        Assert.Equal(360, p.Rules.Count(r => r.IsRollout));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }

    private sealed class Capture(string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task The_console_receives_the_machine_identity_and_the_rollout_request_other_hosts_do_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sp-rollout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var handler = new Capture(Served());
            PolicyLoader Loader(Settings s) => new(s, new HttpClient(handler))
            {
                RefreshReferenceList = false, CacheFile = Path.Combine(dir, "cache.json"), ReferenceCacheFile = Path.Combine(dir, "ref.json"),
                Identity = () => ("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-COMPTA-01"),
            };
            var loaded = await Loader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton" }).LoadAsync();
            Assert.Equal("Général", loaded.Organization!.Rollout!.Ring);
            var r = Assert.Single(handler.Requests);
            Assert.Equal("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", r.Headers.GetValues("X-SpokaPilote-Machine-Id").Single());
            Assert.Equal("PC-COMPTA-01", r.Headers.GetValues("X-SpokaPilote-Machine").Single());
            Assert.Equal("1", r.Headers.GetValues("X-SpokaPilote-Rollout").Single());

            // Cache local : la section « rollout » est conservée (poste hors ligne : mêmes retenues).
            handler.Requests.Clear();
            var offline = await new PolicyLoader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton" },
                new HttpClient(new FailingHandler()))
            {
                RefreshReferenceList = false, CacheFile = Path.Combine(dir, "cache.json"), ReferenceCacheFile = Path.Combine(dir, "ref.json"),
            }.LoadAsync();
            Assert.True(offline.FromCache);
            Assert.True(offline.Effective.Evaluate(Cand("Intel - Display - 31.0.101.5600", "31.0.101.5600"), null, Now).IsRolloutHold);

            // Autre hôte (PolicySource) : ni jeton, ni identité.
            await Loader(new Settings { ServerUrl = "https://console.exemple.fr", ServerToken = "jeton", PolicySource = "https://intranet.exemple.fr/p.json" })
                .LoadAsync();
            var other = Assert.Single(handler.Requests);
            Assert.False(other.Headers.Contains("X-SpokaPilote-Machine-Id"));
            Assert.False(other.Headers.Contains("X-SpokaPilote-Rollout"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temporaire */ }
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Console injoignable");
    }
}
