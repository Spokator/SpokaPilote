using System.Net;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;

namespace SpokaPilote.Core.Tests;

public class PolicyTests
{
    // ------------------------------------------------------------------ jeux d'essai

    private static readonly DeviceInfo Gpu = new()
    {
        InstanceId = @"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE&REV_A1\4&1",
        Name = "NVIDIA GeForce RTX 3070",
        Manufacturer = "NVIDIA",
        ClassName = "Display",
        HardwareIds = new[] { @"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE&REV_A1", @"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE" },
        CompatibleIds = new[] { @"PCI\VEN_10DE&DEV_2484&REV_A1", @"PCI\VEN_10DE&DEV_2484", @"PCI\VEN_10DE&CC_030000" },
    };

    private static readonly DeviceInfo Sst = new()
    {
        InstanceId = @"PCI\VEN_8086&DEV_A0C8&SUBSYS_0A1F1028&REV_20\3&2",
        Name = "Intel® Smart Sound Technology (Intel® SST) Audio Controller",
        Manufacturer = "Intel Corporation",
        ClassName = "System",
        HardwareIds = new[] { @"PCI\VEN_8086&DEV_A0C8&SUBSYS_0A1F1028&REV_20" },
    };

    private static readonly IReadOnlyList<DeviceInfo> Devices = new[] { Gpu, Sst };

    private static DriverCandidate Candidate(string title, string? version, DeviceInfo? device = null,
        DriverSourceKind source = DriverSourceKind.WindowsUpdate, CandidateKind kind = CandidateKind.Update,
        string? manufacturer = null, DateTime? date = null, string? id = null)
    {
        var c = new DriverCandidate
        {
            Id = id ?? $"{source}:{title}:{version}",
            Source = source,
            Title = title,
            Version = version,
            Manufacturer = manufacturer,
            Date = date,
            Kind = kind,
            MatchedHardwareId = device?.HardwareIds.FirstOrDefault(),
        };
        if (device is not null)
        {
            c.TargetDeviceIds.Add(device.InstanceId);
            c.TargetDeviceNames.Add(device.Name);
        }
        return c;
    }

    private static DriverPolicy Policy(string rulesJson, string settingsJson = "{}", PolicyLayer layer = PolicyLayer.Organization)
    {
        var result = DriverPolicy.Parse($$"""{ "schema": 1, "name": "Test", "version": 7, "settings": {{settingsJson}}, "rules": [ {{rulesJson}} ] }""", layer);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        return result.Policy!;
    }

    private static PolicyDecision Eval(DriverPolicy p, DriverCandidate c, DateTime? now = null) => p.Evaluate(c, Devices, now);

    // ------------------------------------------------------------------ jokers et critères

    [Fact]
    public void HardwareId_wildcards_match_hardware_and_compatible_ids_case_insensitively()
    {
        var gpu = Candidate("NVIDIA - Display - 31.0.15.5244", "31.0.15.5244", Gpu);
        Assert.True(Eval(Policy("""{ "action": "block", "hardwareId": "pci\\ven_10de*", "reason": "x" }"""), gpu).IsBlocked);
        // ID exact présent seulement dans les ID compatibles du périphérique.
        Assert.True(Eval(Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE&DEV_2484", "reason": "x" }"""), gpu).IsBlocked);
        // « ? » = un caractère.
        Assert.True(Eval(Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE&DEV_248?", "reason": "x" }"""), gpu).IsBlocked);
        // Le motif couvre toute la valeur : pas de correspondance partielle implicite.
        Assert.False(Eval(Policy("""{ "action": "block", "hardwareId": "VEN_10DE", "reason": "x" }"""), gpu).IsBlocked);
        Assert.False(Eval(Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_1002*", "reason": "x" }"""), gpu).IsBlocked);
    }

    [Fact]
    public void HardwareId_uses_matched_id_when_devices_are_unknown()
    {
        var c = Candidate("Pilote", "1.0", Gpu);
        var p = Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE&DEV_2484&SUBSYS_146B10DE&REV_A1", "reason": "x" }""");
        Assert.True(p.Evaluate(c, devices: null).IsBlocked);
    }

    [Fact]
    public void Title_manufacturer_device_category_source_and_kind_criteria_combine_with_and()
    {
        var c = Candidate("NVIDIA GeForce Game Ready 552.44", "552.44", Gpu, manufacturer: "NVIDIA");
        Assert.True(Eval(Policy("""{ "action": "block", "title": "*GeForce*", "manufacturer": "nvidia", "reason": "x" }"""), c).IsBlocked);
        Assert.False(Eval(Policy("""{ "action": "block", "title": "NVIDIA", "reason": "x" }"""), c).IsBlocked); // ancré
        Assert.True(Eval(Policy("""{ "action": "block", "deviceName": "*RTX 30*", "category": "Display", "reason": "x" }"""), c).IsBlocked);
        Assert.True(Eval(Policy("""{ "action": "block", "source": ["Dell", "WindowsUpdate"], "kind": "Update", "reason": "x" }"""), c).IsBlocked);
        Assert.False(Eval(Policy("""{ "action": "block", "title": "*GeForce*", "source": "MicrosoftCatalog", "reason": "x" }"""), c).IsBlocked);
        Assert.False(Eval(Policy("""{ "action": "block", "title": "*GeForce*", "kind": "MissingDriver", "reason": "x" }"""), c).IsBlocked);
        // Fabricant absent du candidat (catalogue Microsoft) : celui du périphérique visé compte.
        var catalog = Candidate("Intel Corporation - System - 10.29.0.5152", "10.29.0.5152", Sst, DriverSourceKind.MicrosoftCatalog);
        Assert.True(Eval(Policy("""{ "action": "block", "manufacturer": "Intel*", "reason": "x" }"""), catalog).IsBlocked);
    }

    // ------------------------------------------------------------------ plages de versions

    [Fact]
    public void Version_ranges_are_inclusive_and_exact_versions_are_normalized()
    {
        var p = Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE*", "versionMin": "560.70", "versionMax": "560.94", "reason": "BSOD" }""");
        Assert.True(Eval(p, Candidate("g", "560.70", Gpu)).IsBlocked);
        Assert.True(Eval(p, Candidate("g", "560.81", Gpu)).IsBlocked);
        Assert.True(Eval(p, Candidate("g", "560.94", Gpu)).IsBlocked);
        Assert.False(Eval(p, Candidate("g", "560.69", Gpu)).IsBlocked);
        Assert.False(Eval(p, Candidate("g", "565.90", Gpu)).IsBlocked);
        // Version illisible : la règle ne s'applique pas.
        Assert.False(Eval(p, Candidate("g", null, Gpu)).IsBlocked);

        var exact = Policy("""{ "action": "block", "manufacturer": "Intel*", "versions": ["10.29.0.5152", "10.30.0.5152"], "reason": "x" }""");
        Assert.True(Eval(exact, Candidate("sst", "10.30.00.5152", Sst)).IsBlocked); // « 00 » = « 0 »
        Assert.False(Eval(exact, Candidate("sst", "10.30.0.5714", Sst)).IsBlocked);
    }

    // ------------------------------------------------------------------ priorité allow / block / pin

    [Fact]
    public void Explicit_allow_placed_before_a_general_block_wins()
    {
        var p = Policy("""
            { "id": "ok-552", "action": "allow", "hardwareId": "PCI\\VEN_10DE*", "versions": "552.44", "reason": "validé" },
            { "id": "no-nvidia", "action": "block", "hardwareId": "PCI\\VEN_10DE*", "reason": "NVIDIA géré à part" }
            """);
        var allowed = Eval(p, Candidate("g", "552.44", Gpu));
        Assert.False(allowed.IsBlocked);
        Assert.True(allowed.IsExplicitAllow);
        Assert.Contains("ok-552", allowed.Reason);

        var blocked = Eval(p, Candidate("g", "560.70", Gpu));
        Assert.True(blocked.IsBlocked);
        Assert.Contains("NVIDIA géré à part", blocked.Reason);
        Assert.Contains("no-nvidia", blocked.Reason);
    }

    [Fact]
    public void First_matching_rule_decides_and_shadowed_rules_are_reported()
    {
        var result = DriverPolicy.Parse("""
            { "name": "t", "rules": [
              { "id": "no-nvidia", "action": "block", "hardwareId": "PCI\\VEN_10DE*", "reason": "x" },
              { "id": "ok-552", "action": "allow", "hardwareId": "PCI\\VEN_10DE&DEV_2484*", "versions": "552.44" } ] }
            """);
        Assert.True(result.IsValid);
        Assert.True(result.Policy!.Evaluate(Candidate("g", "552.44", Gpu), Devices).IsBlocked);
        var warning = Assert.Single(result.Warnings, w => w.RuleNumber == 2);
        Assert.Contains("ne s'appliquera jamais", warning.Message);
        Assert.Contains("n°1", warning.Message);
    }

    [Fact]
    public void Pin_blocks_versions_above_the_ceiling_only()
    {
        var p = Policy("""
            { "id": "pin-gpu", "action": "pin", "hardwareId": "PCI\\VEN_10DE*", "pinVersion": "552.44", "reason": "Certifié logiciel CAO" },
            { "action": "block", "hardwareId": "PCI\\VEN_10DE*", "versions": "551.86", "reason": "version défectueuse" }
            """);
        var above = Eval(p, Candidate("g", "560.70", Gpu));
        Assert.True(above.IsBlocked);
        Assert.Contains("552.44", above.Reason);
        Assert.Contains("Certifié logiciel CAO", above.Reason);
        Assert.False(Eval(p, Candidate("g", "552.44", Gpu)).IsBlocked);
        Assert.False(Eval(p, Candidate("g", "537.13", Gpu)).IsBlocked);
        // Une version conforme au pin peut encore être bloquée par une règle suivante.
        Assert.True(Eval(p, Candidate("g", "551.86", Gpu)).IsBlocked);
        // Version illisible : impossible de garantir le plafond → bloqué.
        Assert.True(Eval(p, Candidate("g", "beta", Gpu)).IsBlocked);
    }

    // ------------------------------------------------------------------ expiration

    [Fact]
    public void Expired_rules_are_ignored_and_date_only_expiry_covers_the_whole_day()
    {
        var p = Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE*", "expires": "2030-06-15", "reason": "temporaire" }""");
        var c = Candidate("g", "560.70", Gpu);
        Assert.True(Eval(p, c, new DateTime(2030, 6, 14, 9, 0, 0)).IsBlocked);
        Assert.True(Eval(p, c, new DateTime(2030, 6, 15, 23, 59, 0)).IsBlocked);
        Assert.False(Eval(p, c, new DateTime(2030, 6, 16, 0, 0, 1)).IsBlocked);
    }

    [Fact]
    public void Already_expired_rule_produces_a_warning()
    {
        var result = DriverPolicy.Parse("""{ "name": "t", "rules": [ { "action": "block", "title": "*", "expires": "2001-01-01", "reason": "x" } ] }""");
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.RuleNumber == 1 && w.Message.Contains("expirée"));
        Assert.False(result.Policy!.Evaluate(Candidate("g", "1", Gpu), Devices).IsBlocked);
    }

    // ------------------------------------------------------------------ réglages généraux

    [Fact]
    public void Settings_restrict_sources_firmware_optional_and_missing_only()
    {
        var sources = Policy("""{ "action": "allow", "title": "*", "reason": "tout" }""", """{ "allowedSources": ["LocalRepository", "Dell"] }""");
        Assert.False(sources.AllowsSource(DriverSourceKind.WindowsUpdate));
        Assert.True(sources.AllowsSource(DriverSourceKind.Dell));
        // allowedSources n'est jamais levé par une règle allow.
        Assert.True(Eval(sources, Candidate("g", "1", Gpu)).IsBlocked);

        var settings = DriverPolicy.Combine(Policy("""{ "action": "block", "title": "rien", "reason": "x" }""",
            """{ "allowFirmware": false, "allowOptional": false }"""), null);
        Assert.True(Eval(settings, Candidate("BIOS 1.20", "1.20", kind: CandidateKind.Firmware)).IsBlocked);
        Assert.True(Eval(settings, Candidate("Opt", "1", Gpu, kind: CandidateKind.Optional)).IsBlocked);
        Assert.False(Eval(settings, Candidate("Upd", "1", Gpu)).IsBlocked);

        var missing = Policy("""{ "action": "block", "title": "rien", "reason": "x" }""", """{ "onlyMissingDrivers": true }""");
        Assert.True(Eval(missing, Candidate("Upd", "1", Gpu)).IsBlocked);
        Assert.False(Eval(missing, Candidate("Manquant", "1", Gpu, kind: CandidateKind.MissingDriver)).IsBlocked);
    }

    [Fact]
    public void Minimum_age_blocks_recent_updates_unless_explicitly_allowed()
    {
        var p = Policy("""{ "action": "allow", "title": "*Urgent*", "reason": "correctif de sécurité" }""", """{ "minimumDriverAgeDays": 30 }""");
        var now = new DateTime(2026, 11, 25);
        var recent = Eval(p, Candidate("Realtek Audio", "6.0.9600.1", Gpu, date: now.AddDays(-5)), now);
        Assert.True(recent.IsBlocked);
        Assert.Contains("30 jours", recent.Reason);
        Assert.False(Eval(p, Candidate("Realtek Audio", "6.0.9500.1", Gpu, date: now.AddDays(-45)), now).IsBlocked);
        // Pilote manquant : pas de recul imposé.
        Assert.False(Eval(p, Candidate("NIC", "1", Gpu, kind: CandidateKind.MissingDriver, date: now), now).IsBlocked);
        // Exception explicite.
        Assert.False(Eval(p, Candidate("Correctif Urgent", "2", Gpu, date: now), now).IsBlocked);
    }

    // ------------------------------------------------------------------ fusion avec la liste de référence

    [Fact]
    public void Organization_rules_take_precedence_over_reference_blocklist()
    {
        var reference = Policy("""{ "id": "ref-sst", "action": "block", "manufacturer": "Intel*", "versions": "10.29.0.5152", "reason": "BSOD signalés" }""", layer: PolicyLayer.Reference);
        var sst = Candidate("Intel SST", "10.29.0.5152", Sst);

        var withoutOrg = DriverPolicy.Combine(null, reference);
        var d = Eval(withoutOrg, sst);
        Assert.True(d.IsBlocked);
        Assert.Contains("liste de référence", d.Reason);

        var allowing = DriverPolicy.Combine(Policy("""{ "action": "allow", "hardwareId": "PCI\\VEN_8086&DEV_A0C8*", "reason": "testé" }"""), reference);
        Assert.False(Eval(allowing, sst).IsBlocked);

        var ignoring = DriverPolicy.Combine(Policy("""{ "action": "block", "title": "rien", "reason": "x" }""", """{ "ignoreReferenceRules": ["ref-sst"] }"""), reference);
        Assert.False(Eval(ignoring, sst).IsBlocked);

        var disabled = DriverPolicy.Combine(Policy("""{ "action": "block", "title": "rien", "reason": "x" }""", """{ "useReferenceBlocklist": false }"""), reference);
        Assert.False(Eval(disabled, sst).IsBlocked);

        // Le blocage de l'organisation passe avant la liste de référence.
        var orgBlock = DriverPolicy.Combine(Policy("""{ "id": "org", "action": "block", "title": "*SST*", "reason": "géré par le fournisseur" }"""), reference);
        Assert.Contains("géré par le fournisseur", Eval(orgBlock, sst).Reason);
    }

    [Fact]
    public void Embedded_reference_list_is_valid_and_targets_official_notices()
    {
        var reference = PolicyLoader.LoadEmbeddedReference();
        Assert.True(reference.IsValid, string.Join(" | ", reference.Errors));
        Assert.Empty(reference.Warnings);
        Assert.All(reference.Policy!.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.StartsWith("https://", r.SourceUrl);
            Assert.Equal(PolicyLayer.Reference, r.Layer);
        });
        Assert.True(reference.Policy.Settings.IsDefault);
        // Intel SST 10.30.0.5152 bloqué, version corrigée 10.30.0.5714 autorisée.
        var effective = DriverPolicy.Combine(null, reference.Policy);
        Assert.True(effective.Evaluate(Candidate("Intel - System - 10.30.0.5152", "10.30.0.5152", Sst, manufacturer: "Intel"), Devices).IsBlocked);
        Assert.False(effective.Evaluate(Candidate("Intel - System - 10.30.0.5714", "10.30.0.5714", Sst, manufacturer: "Intel"), Devices).IsBlocked);
    }

    [Fact]
    public void Generated_template_is_valid_and_targets_no_real_hardware()
    {
        var result = DriverPolicy.Parse(PolicyTemplate.Create(new DateTime(2026, 11, 25)));
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.Policy!.Rules.Count);
        Assert.Equal("2026-11-25", result.Policy.UpdatedAt);
        var noRules = DriverPolicy.Combine(result.Policy, null);
        Assert.All(result.Policy.Rules, r => Assert.False(r.Matches(Candidate("NVIDIA GeForce", "1.5", Gpu), Devices)));
        Assert.All(result.Policy.Rules, r => Assert.False(r.Matches(Candidate("Intel SST", "1.5", Sst), Devices)));
        Assert.False(noRules.AllowsSource(DriverSourceKind.Nvidia));
    }

    // ------------------------------------------------------------------ politique invalide

    [Theory]
    [InlineData("""{ "rules": [ { "action": "block", "title": "*", "reason": "x" }, { "action": "block", "hardwareIds": "PCI*" } ] }""", 2, "hardwareIds")]
    [InlineData("""{ "rules": [ { "action": "ban", "title": "*" } ] }""", 1, "Action inconnue")]
    [InlineData("""{ "rules": [ { "action": "block", "reason": "x" } ] }""", 1, "Aucun critère")]
    [InlineData("""{ "rules": [ { "action": "block", "title": "*", "versionMin": "abc", "reason": "x" } ] }""", 1, "version illisible")]
    [InlineData("""{ "rules": [ { "action": "block", "title": "*", "versionMin": "2.0", "versionMax": "1.0", "reason": "x" } ] }""", 1, "supérieure")]
    [InlineData("""{ "rules": [ { "action": "pin", "title": "*", "reason": "x" } ] }""", 1, "pinVersion")]
    [InlineData("""{ "rules": [ { "action": "block", "source": "Toshiba", "reason": "x" } ] }""", 1, "Toshiba")]
    [InlineData("""{ "rules": [ { "action": "block", "title": "*", "expires": "bientôt", "reason": "x" } ] }""", 1, "expires")]
    [InlineData("""{ "rules": [ { "action": "block", "title": "", "reason": "x" } ] }""", 1, "vide")]
    public void Invalid_rules_are_reported_with_their_number(string json, int ruleNumber, string expected)
    {
        var result = DriverPolicy.Parse(json);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.RuleNumber == ruleNumber && e.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"Règle n°{ruleNumber}", result.Errors.First(e => e.RuleNumber == ruleNumber).ToString());
    }

    [Theory]
    [InlineData("""{ "schema": 99, "rules": [] }""", "non pris en charge")]
    [InlineData("""{ "settings": { "allowedSources": [] } }""", "vide")]
    [InlineData("""{ "settings": { "allowedSources": ["Ebay"] } }""", "Ebay")]
    [InlineData("""{ "settings": { "minimumDriverAgeDays": -1 } }""", "minimumDriverAgeDays")]
    [InlineData("""{ "rules": [ { "action": "block", "title": "*" } """, "ligne")]
    [InlineData("", "vide")]
    [InlineData("""[1, 2]""", "")]
    public void Invalid_policies_are_rejected_with_a_clear_message(string json, string expected)
    {
        var result = DriverPolicy.Parse(json);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comments_trailing_commas_and_numeric_version_are_accepted()
    {
        var result = DriverPolicy.Parse("""
            // politique du siège
            { "name": "Siège", "version": 12, /* révision */ "rules": [ { "action": "block", "title": "*", "reason": "gel", }, ], }
            """);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Equal("12", result.Policy!.Version);
        Assert.Equal("« Siège » v12", result.Policy.DisplayName);
    }

    // ------------------------------------------------------------------ application à une analyse

    [Fact]
    public void Blocked_candidates_stay_visible_hidden_ones_are_removed_and_allowed_sources_take_over()
    {
        var policy = DriverPolicy.Combine(Policy("""
            { "id": "wu-gpu", "action": "block", "hardwareId": "PCI\\VEN_10DE*", "source": "WindowsUpdate", "reason": "via le catalogue uniquement" },
            { "id": "sst", "action": "block", "manufacturer": "Intel*", "hide": true, "reason": "géré à part" }
            """), null);
        var wuGpu = Candidate("NVIDIA WU", "560.70", Gpu);
        var catalogGpu = Candidate("NVIDIA Catalogue", "552.44", Gpu, DriverSourceKind.MicrosoftCatalog);
        var sst = Candidate("Intel SST", "10.30.0.5714", Sst, manufacturer: "Intel");

        var (merged, decisions) = DriverScanEngine.ApplyPolicy(new[] { wuGpu, catalogGpu, sst }, Devices, policy);

        // Le candidat autorisé (catalogue) passe devant le candidat bloqué (WU, pourtant plus prioritaire).
        var kept = Assert.Single(merged);
        Assert.Same(catalogGpu, kept);
        Assert.Null(kept.BlockedReason);
        Assert.Equal("via le catalogue uniquement (règle « wu-gpu »)", wuGpu.BlockedReason);
        // Candidat masqué : retiré, mais tracé dans les décisions.
        Assert.Contains(decisions, d => ReferenceEquals(d.Candidate, sst) && d.Decision.Verdict == PolicyVerdict.Hidden);

        // Sans alternative autorisée, le candidat bloqué reste affiché avec son motif.
        var (alone, _) = DriverScanEngine.ApplyPolicy(new[] { Candidate("NVIDIA WU", "560.70", Gpu) }, Devices, policy);
        Assert.NotNull(Assert.Single(alone).BlockedReason);
    }

    [Fact]
    public void Blocked_alternatives_are_never_attempted()
    {
        var policy = DriverPolicy.Combine(Policy("""{ "action": "block", "hardwareId": "PCI\\VEN_10DE*", "versions": "551.86", "reason": "x" }"""), null);
        var main = Candidate("Cat 552.44", "552.44", Gpu, DriverSourceKind.MicrosoftCatalog);
        main.Alternatives.Add(Candidate("Cat 551.86", "551.86", null, DriverSourceKind.MicrosoftCatalog));
        main.Alternatives.Add(Candidate("Cat 537.13", "537.13", null, DriverSourceKind.MicrosoftCatalog));
        policy.Apply(new List<DriverCandidate> { main }, Devices);
        Assert.Equal(new[] { "537.13" }, main.Alternatives.Select(a => a.Version));
    }

    // ------------------------------------------------------------------ installation

    [Fact]
    public async Task Installer_refuses_blocked_candidates_even_when_selected()
    {
        var blocked = Candidate("NVIDIA WU", "560.70", Gpu);
        blocked.BlockedReason = "Écrans bleus constatés (règle « nvidia-560 »)";
        var installer = new DriverInstaller(new Settings(), new SystemInfo());
        var outcomes = await installer.InstallAsync(new[] { blocked }, Devices, new InstallOptions { DryRun = true });
        var outcome = Assert.Single(outcomes);
        Assert.Equal(InstallStatus.Blocked, outcome.Status);
        Assert.Equal(blocked.BlockedReason, outcome.Message);
        Assert.False(outcome.Succeeded);
    }

    // ------------------------------------------------------------------ rapports

    [Fact]
    public void Reports_expose_blocked_reason_and_applied_policy()
    {
        var org = Policy("""{ "id": "no-gpu", "action": "block", "hardwareId": "PCI\\VEN_10DE*", "reason": "Gel GPU" }""");
        var loaded = new LoadedPolicy { Effective = DriverPolicy.Combine(org, null), Organization = org, OrganizationOrigin = @"\\srv\deploiement\driver-policy.json" };
        var (merged, decisions) = DriverScanEngine.ApplyPolicy(new[] { Candidate("NVIDIA WU", "560.70", Gpu) }, Devices, loaded.Effective);
        var scan = new ScanResult
        {
            System = new SystemInfo { ComputerName = "PC-POL" },
            Devices = Devices.ToList(),
            Candidates = merged,
            Policy = loaded,
            PolicyDecisions = decisions,
        };

        var report = ScanReport.From(scan);
        Assert.Equal("Gel GPU (règle « no-gpu »)", Assert.Single(report.Candidates).BlockedReason);
        Assert.Equal("Test", report.Policy!.Name);
        Assert.Equal("7", report.Policy.Version);
        Assert.Equal(1, report.Policy.BlockedCandidates);
        Assert.Contains("\"blockedReason\"", report.ToJson());

        var html = ReportWriter.ToHtml(scan);
        Assert.Contains("Bloqué par la politique", html);
        Assert.Contains("Gel GPU", html);
        Assert.Contains("Test v7", html);
    }

    // ------------------------------------------------------------------ chargement, cache, console centrale

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sp-policy-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static PolicyLoader Loader(TempDir dir, Settings settings, HttpClient? http = null) => new(settings, http)
    {
        RefreshReferenceList = false,
        CacheFile = dir.File("policy-cache.json"),
        ReferenceCacheFile = dir.File("known-bad-drivers.json"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private const string ValidPolicy = """{ "name": "Parc", "version": "3", "rules": [ { "id": "r1", "action": "block", "title": "*GeForce*", "reason": "gel" } ] }""";

    [Fact]
    public async Task Loader_reads_a_file_caches_it_and_falls_back_to_cache_when_the_new_one_is_invalid()
    {
        using var dir = new TempDir();
        var file = dir.File("driver-policy.json");
        File.WriteAllText(file, ValidPolicy);
        var settings = new Settings { PolicySource = file };

        var first = await Loader(dir, settings).LoadAsync();
        Assert.Equal("Parc", first.Organization!.Name);
        Assert.False(first.FromCache);
        Assert.Empty(first.Messages);
        Assert.True(File.Exists(dir.File("policy-cache.json")));
        Assert.True(first.Reference!.Rules.Count > 0); // liste de référence intégrée toujours présente
        Assert.True(first.Effective.Rules.Count > first.Organization.Rules.Count);

        // Politique cassée sur le partage : l'analyse continue avec la politique précédente.
        File.WriteAllText(file, """{ "name": "Parc", "version": "4", "rules": [ { "action": "blok", "title": "*" } ] }""");
        var second = await Loader(dir, settings).LoadAsync();
        Assert.True(second.FromCache);
        Assert.Equal("3", second.Organization!.Version);
        var message = Assert.Single(second.Messages);
        Assert.Contains("invalide", message);
        Assert.Contains("n°1", message);

        // Partage injoignable : idem.
        File.Delete(file);
        var third = await Loader(dir, settings).LoadAsync();
        Assert.True(third.FromCache);
        Assert.Contains("injoignable", Assert.Single(third.Messages));
    }

    [Fact]
    public async Task Loader_without_cache_falls_back_to_no_policy_and_without_source_ignores_cache()
    {
        using var dir = new TempDir();
        var broken = dir.File("broken.json");
        File.WriteAllText(broken, "{ pas du json");
        var loaded = await Loader(dir, new Settings { PolicySource = broken }).LoadAsync();
        Assert.Null(loaded.Organization);
        Assert.Contains("Aucune politique précédente", Assert.Single(loaded.Messages));

        File.WriteAllText(dir.File("ok.json"), ValidPolicy);
        await Loader(dir, new Settings { PolicySource = dir.File("ok.json") }).LoadAsync(); // remplit le cache
        var none = await Loader(dir, new Settings()).LoadAsync();
        Assert.Null(none.Organization);
        Assert.Empty(none.Messages);
    }

    [Fact]
    public async Task Override_source_is_not_cached()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("essai.json"), ValidPolicy);
        var loader = new PolicyLoader(new Settings())
        {
            SourceOverride = dir.File("essai.json"), UseCache = false, RefreshReferenceList = false,
            CacheFile = dir.File("policy-cache.json"), ReferenceCacheFile = dir.File("ref.json"),
        };
        Assert.NotNull((await loader.LoadAsync()).Organization);
        Assert.False(File.Exists(dir.File("policy-cache.json")));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Central_console_policy_uses_bearer_token_and_204_means_no_policy()
    {
        using var dir = new TempDir();
        var status = HttpStatusCode.OK;
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(ValidPolicy) });
        var settings = new Settings { ServerUrl = "https://spokapilote.exemple.fr/", ServerToken = "jeton-de-test" };

        var loaded = await Loader(dir, settings, new HttpClient(handler)).LoadAsync();
        Assert.Equal("Parc", loaded.Organization!.Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://spokapilote.exemple.fr/api/policy", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("jeton-de-test", request.Headers.Authorization.Parameter);
        Assert.True(File.Exists(dir.File("policy-cache.json")));

        status = HttpStatusCode.NoContent;
        var none = await Loader(dir, settings, new HttpClient(handler)).LoadAsync();
        Assert.Null(none.Organization);
        Assert.False(File.Exists(dir.File("policy-cache.json"))); // l'ancienne politique est oubliée

        status = HttpStatusCode.Unauthorized;
        var denied = await Loader(dir, settings, new HttpClient(handler)).LoadAsync();
        Assert.Contains("jeton", Assert.Single(denied.Messages));
    }

    [Fact]
    public async Task Token_is_not_sent_to_another_host_and_plain_http_is_refused_except_localhost()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidPolicy) });
        var settings = new Settings
        {
            ServerUrl = "https://console.exemple.fr", ServerToken = "secret",
            PolicySource = "https://autre.exemple.fr/driver-policy.json",
        };
        Assert.NotNull((await Loader(dir, settings, new HttpClient(handler)).LoadAsync()).Organization);
        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);

        var plain = await Loader(dir, new Settings { PolicySource = "http://intranet.exemple.fr/p.json" }, new HttpClient(handler)).LoadAsync();
        Assert.Contains("HTTPS obligatoire", Assert.Single(plain.Messages));
        Assert.Single(handler.Requests); // aucune requête envoyée

        Assert.True(PolicyLoader.IsAllowedPolicyUrl(new Uri("http://localhost:5000/api/policy")));
        Assert.True(PolicyLoader.IsAllowedPolicyUrl(new Uri("http://127.0.0.1/api/policy")));
        Assert.False(PolicyLoader.IsAllowedPolicyUrl(new Uri("http://192.168.1.10/api/policy")));
    }

    [Fact]
    public async Task Reference_list_copy_is_used_only_when_newer_than_embedded()
    {
        using var dir = new TempDir();
        var embedded = PolicyLoader.LoadEmbeddedReference().Policy!;
        File.WriteAllText(dir.File("known-bad-drivers.json"),
            """{ "name": "ref", "version": "ancienne", "updatedAt": "2000-01-01", "rules": [] }""");
        var old = await Loader(dir, new Settings()).LoadAsync();
        Assert.Equal(embedded.Version, old.Reference!.Version);
        Assert.Equal("intégrée", old.ReferenceOrigin);

        File.WriteAllText(dir.File("known-bad-drivers.json"),
            """{ "name": "ref", "version": "future", "updatedAt": "2099-01-01", "rules": [ { "id": "ref-x", "action": "block", "title": "X", "reason": "x" } ] }""");
        var newer = await Loader(dir, new Settings()).LoadAsync();
        Assert.Equal("future", newer.Reference!.Version);
        Assert.Contains("raw.githubusercontent.com", newer.ReferenceOrigin);
    }

    /// <summary>
    /// Copie de la liste de référence téléchargée chaque jour depuis GitHub (non signée) : un commit erroné ou malveillant qui
    /// y place des règles « allow », « pin » ou « hide », des réglages ou une section « rollout » ne peut RIEN assouplir.
    /// </summary>
    [Fact]
    public async Task A_downloaded_reference_list_can_only_add_blocks_never_lift_organization_settings()
    {
        using var dir = new TempDir();
        var embedded = PolicyLoader.LoadEmbeddedReference().Policy!;
        File.WriteAllText(dir.File("driver-policy.json"), """
            { "name": "Parc", "version": "1", "settings": { "onlyMissingDrivers": true, "minimumDriverAgeDays": 30, "allowOptional": false },
              "rules": [ { "id": "org-gel", "action": "block", "title": "Conexant*", "reason": "Gel" } ] }
            """);
        File.WriteAllText(dir.File("known-bad-drivers.json"), """
            { "name": "ref", "version": "piégée", "updatedAt": "2099-01-01",
              "settings": { "useReferenceBlocklist": false, "allowFirmware": true },
              "rollout": { "decisions": true, "holdUnknown": false },
              "rules": [
                { "id": "ref-tout", "action": "allow", "title": "*", "reason": "tout autoriser" },
                { "id": "ref-pin", "action": "pin", "title": "*", "pinVersion": "999.0" },
                { "id": "ref-masque", "action": "block", "hide": true, "title": "Realtek*", "reason": "masqué" },
                { "id": "ref-nouveau", "action": "block", "title": "Contoso Camera*", "versions": "2.4.0.1", "reason": "BSOD" }
              ] }
            """);
        var loaded = await Loader(dir, new Settings { PolicySource = dir.File("driver-policy.json") }).LoadAsync();
        var reference = loaded.Reference!;
        Assert.Equal("piégée", reference.Version);
        Assert.All(reference.Rules, r => Assert.Equal(PolicyAction.Block, r.ParsedAction));
        Assert.DoesNotContain(reference.Rules, r => r.Hide);
        Assert.Null(reference.Rollout);
        Assert.True(reference.Settings.IsDefault);
        // Les blocages de la liste intégrée restent en vigueur même si la copie téléchargée les a retirés.
        Assert.All(embedded.Rules, r => Assert.Contains(reference.Rules, x => x.Id == r.Id));
        Assert.Contains("complétée par la liste intégrée", loaded.ReferenceOrigin);

        var effective = loaded.Effective;
        var now = new DateTime(2026, 11, 26);
        // Réglages de l'organisation toujours appliqués malgré l'« allow » de la copie.
        Assert.Equal("onlyMissingDrivers", effective.Evaluate(Candidate("NVIDIA - Display - 31.0.15.5244", "31.0.15.5244", Gpu), Devices, now).Setting);
        Assert.True(effective.Evaluate(Candidate("Pilote optionnel", "1.0", Gpu, kind: CandidateKind.Optional), Devices, now).IsBlocked);
        var missing = Candidate("Realtek Audio", "6.0.9600.1", Gpu, kind: CandidateKind.MissingDriver);
        // « hide » ignoré : bloqué mais affiché.
        Assert.Equal(PolicyVerdict.Blocked, effective.Evaluate(missing, Devices, now).Verdict);
        Assert.True(effective.Evaluate(Candidate("Contoso Camera", "2.4.0.1", Gpu, kind: CandidateKind.MissingDriver), Devices, now).IsBlocked);
        // La règle « pin » de la copie n'a pas été reprise (une version illisible n'est pas bloquée par elle).
        Assert.False(effective.Evaluate(Candidate("Pilote manquant", null, Gpu, kind: CandidateKind.MissingDriver), Devices, now).IsBlocked);
        // Le blocage de l'organisation reste prioritaire, et la liste reste active malgré « useReferenceBlocklist » dans la copie.
        Assert.Contains("Gel", effective.Evaluate(Candidate("Conexant Audio", "1.0", Gpu, kind: CandidateKind.MissingDriver), Devices, now).Reason);
        Assert.True(loaded.ReferenceRuleCount > embedded.Rules.Count);
    }

    [Fact]
    public void Reference_layer_rules_other_than_block_are_dropped_with_a_warning_even_when_combined_directly()
    {
        var parsed = DriverPolicy.Parse("""
            { "name": "ref", "rules": [ { "id": "a", "action": "allow", "title": "*" }, { "id": "b", "action": "block", "title": "X*", "reason": "x" } ] }
            """, PolicyLayer.Reference);
        Assert.True(parsed.IsValid);
        Assert.Equal(["b"], parsed.Policy!.Rules.Select(r => r.Id));
        Assert.Contains(parsed.Warnings, w => w.Message.Contains("n'accepte que des règles « block »"));

        // Même une politique lue comme « organisation » mais passée comme liste de référence n'apporte que ses blocages.
        var org = Policy("", """{ "onlyMissingDrivers": true }""");
        var notReference = Policy("""{ "action": "allow", "title": "*", "reason": "x" }""");
        Assert.True(DriverPolicy.Combine(org, notReference).Evaluate(Candidate("Pilote", "1.0", Gpu), Devices).IsBlocked);
    }

    [Fact]
    public void Reference_list_host_is_not_a_trusted_driver_host()
    {
        Assert.False(new TrustPolicy(new Settings()).IsTrustedHost(new Uri(PolicyLoader.ReferenceListUrl)));
    }
}
