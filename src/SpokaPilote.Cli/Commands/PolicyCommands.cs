using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Cli;

/// <summary>Commandes « spokapilote policy ... » : politique de pilotes (approbation / liste noire).</summary>
internal static partial class Program
{
    private static async Task<int> PolicyAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0)?.ToLowerInvariant() ?? "show";
        switch (sub)
        {
            case "show" or "afficher": return await PolicyShowAsync(a, ct);
            case "check" or "verifier" or "vérifier": return await PolicyCheckAsync(a, ct);
            case "test" or "tester": return await PolicyTestAsync(a, ct);
            case "init" or "modele" or "modèle": return PolicyInit(a);
            default:
                WriteLine("Usage : spokapilote policy [show | check <fichier> | test | init <fichier>] [--policy <fichier|url>]", ConsoleColor.Yellow);
                return ExitUsage;
        }
    }

    /// <summary>Chargeur de politique de cette exécution : --policy remplace la source configurée (sans mise en cache).</summary>
    private static PolicyLoader CreatePolicyLoader(Args a, Settings settings)
    {
        var source = a.Value("policy");
        if (string.IsNullOrWhiteSpace(source)) return new PolicyLoader(settings);
        if (!IsUrl(source)) source = Path.GetFullPath(source);
        WriteLine($"Politique de pilotes imposée pour cette exécution : {source}", ConsoleColor.DarkCyan);
        return new PolicyLoader(settings) { SourceOverride = source, UseCache = false };
    }

    private static bool IsUrl(string s) =>
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ligne « Politique » de l'en-tête des analyses (scan / install / policy test).</summary>
    private static void PrintPolicySummary(LoadedPolicy? policy)
    {
        if (policy is null) return;
        WriteLine($"Politique : {policy.Summary}", ConsoleColor.White);
        foreach (var m in policy.Messages) WriteLine($"  ⚠ {m}", ConsoleColor.Yellow);
    }

    // ------------------------------------------------------------------ show

    private static async Task<int> PolicyShowAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        Banner();
        var loader = CreatePolicyLoader(a, settings);
        var policy = await loader.LoadAsync(ct);

        WriteLine("\nPolitique de l'organisation", ConsoleColor.Cyan);
        var source = loader.ResolveSource();
        if (source is null)
        {
            WriteLine("  Aucune source configurée (PolicySource et ServerUrl vides dans settings.json).", ConsoleColor.Gray);
            WriteLine("  → spokapilote config set PolicySource \\\\srv\\deploiement\\driver-policy.json", ConsoleColor.DarkGray);
        }
        else
        {
            WriteLine($"  Source     : {source}", ConsoleColor.Gray);
        }
        foreach (var m in policy.Messages) WriteLine($"  ⚠ {m}", ConsoleColor.Yellow);

        if (policy.Organization is { } org)
        {
            WriteLine($"  Nom        : {org.Name ?? "(sans nom)"}{(org.Version is null ? "" : $"  · version {org.Version}")}{(org.UpdatedAt is null ? "" : $"  · mise à jour {org.UpdatedAt}")}", ConsoleColor.White);
            if (policy.FromCache) WriteLine($"  Origine    : cache local du {policy.CachedAt:dd/MM/yyyy HH:mm} ({loader.CacheFile})", ConsoleColor.Yellow);
            if (org.Description is not null) WriteLine($"  {org.Description}", ConsoleColor.Gray);
            PrintSettings(org.Settings);
            PrintRules(org.Rules);
        }
        else if (source is not null)
        {
            WriteLine("  Aucune politique d'organisation en vigueur.", ConsoleColor.Gray);
        }

        WriteLine("\nListe de référence SpokaPilote (versions signalées par Microsoft / les constructeurs)", ConsoleColor.Cyan);
        if (policy.Organization?.Settings.UseReferenceBlocklist == false)
            WriteLine("  Désactivée par la politique de l'organisation (useReferenceBlocklist = false).", ConsoleColor.Yellow);
        if (policy.Reference is { } reference)
        {
            WriteLine($"  Version    : {reference.Version ?? "?"} ({reference.UpdatedAt}) — {policy.ReferenceOrigin}", ConsoleColor.Gray);
            var ignored = policy.Organization?.Settings.IgnoreReferenceRules ?? new List<string>();
            if (ignored.Count > 0) WriteLine($"  Règles écartées par l'organisation : {string.Join(", ", ignored)}", ConsoleColor.Gray);
            PrintRules(reference.Rules);
        }
        else
        {
            WriteLine("  Indisponible.", ConsoleColor.Yellow);
        }

        WriteLine("\nOrdre d'évaluation : sources autorisées → règles de l'organisation → liste de référence → réglages généraux.", ConsoleColor.DarkGray);
        WriteLine("La première règle « block » ou « allow » qui correspond décide ; « pin » bloque si la version dépasse la version figée.", ConsoleColor.DarkGray);
        return ExitOk;
    }

    private static void PrintSettings(PolicySettings s)
    {
        WriteLine("  Réglages   :", ConsoleColor.White);
        WriteLine($"    sources autorisées      : {(s.AllowedSources is null ? "toutes" : string.Join(", ", s.AllowedSources))}", ConsoleColor.Gray);
        WriteLine($"    BIOS / firmware         : {(s.AllowFirmware ? "autorisés" : "interdits")}", ConsoleColor.Gray);
        WriteLine($"    pilotes optionnels      : {(s.AllowOptional ? "autorisés" : "interdits")}", ConsoleColor.Gray);
        WriteLine($"    pilotes manquants seuls : {(s.OnlyMissingDrivers ? "oui" : "non")}", ConsoleColor.Gray);
        WriteLine($"    recul minimal           : {(s.MinimumDriverAgeDays > 0 ? $"{s.MinimumDriverAgeDays} jour(s)" : "aucun")}", ConsoleColor.Gray);
    }

    private static void PrintRules(IReadOnlyList<PolicyRule> rules)
    {
        if (rules.Count == 0)
        {
            WriteLine("  Aucune règle.", ConsoleColor.Gray);
            return;
        }
        WriteLine($"  Règles ({rules.Count}) :", ConsoleColor.White);
        foreach (var r in rules)
        {
            var expired = r.IsExpired(DateTime.Now);
            var color = expired ? ConsoleColor.DarkGray : r.ParsedAction switch
            {
                PolicyAction.Allow => ConsoleColor.Green,
                PolicyAction.Pin => ConsoleColor.Cyan,
                _ => ConsoleColor.Red,
            };
            var flags = (r.Hide ? " · masqué" : "") +
                        (r.ExpiresAt is { } e ? (expired ? $" · EXPIRÉE le {e:dd/MM/yyyy}" : $" · jusqu'au {e:dd/MM/yyyy}") : "");
            WriteLine($"   {r.Number,3}. [{r.ActionLabel,-5}] {r.Id ?? "(sans id)"}{flags}", color);
            WriteLine($"        si   : {r.CriteriaSummary}", ConsoleColor.Gray);
            if (r.Reason is not null) WriteLine($"        motif: {r.Reason}", ConsoleColor.Gray);
            if (r.SourceUrl is not null) WriteLine($"        avis : {r.SourceUrl}", ConsoleColor.DarkGray);
        }
    }

    // ------------------------------------------------------------------ check

    private static async Task<int> PolicyCheckAsync(Args a, CancellationToken ct)
    {
        var file = a.Positional(1) ?? a.Value("policy");
        if (file is null)
        {
            WriteLine("Usage : spokapilote policy check <driver-policy.json | https://...>", ConsoleColor.Yellow);
            return ExitUsage;
        }

        string? json;
        try
        {
            var loader = new PolicyLoader(LoadSettings(a)) { RefreshReferenceList = false, UseCache = false };
            var (content, noPolicy) = await loader.FetchAsync(IsUrl(file) ? file : Path.GetFullPath(file), ct);
            if (noPolicy)
            {
                WriteLine($"{file} : la console répond « aucune politique » (HTTP 204).", ConsoleColor.Yellow);
                return ExitOk;
            }
            json = content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteLine($"✗ Lecture impossible : {ex.Message}", ConsoleColor.Red);
            return ExitError;
        }

        var result = DriverPolicy.Parse(json!);
        WriteLine($"Vérification de {file}", ConsoleColor.White);
        foreach (var e in result.Errors) WriteLine($"  ✗ {e}", ConsoleColor.Red);
        foreach (var w in result.Warnings) WriteLine($"  ⚠ {w}", ConsoleColor.Yellow);

        if (!result.IsValid)
        {
            var faulty = result.Errors.Where(e => e.RuleNumber is not null).Select(e => e.RuleNumber!.Value).Distinct().OrderBy(n => n).ToList();
            WriteLine($"\n✗ Politique INVALIDE : {result.Errors.Count} erreur(s){(faulty.Count > 0 ? $" (règle(s) n°{string.Join(", n°", faulty)})" : "")}.", ConsoleColor.Red);
            WriteLine("  Déployée telle quelle, elle serait ignorée par les postes (qui garderaient leur politique précédente).", ConsoleColor.Gray);
            return ExitError;
        }

        var p = result.Policy!;
        WriteLine($"\n✓ Politique valide : {p.DisplayName}, {p.Rules.Count} règle(s){(result.Warnings.Count > 0 ? $", {result.Warnings.Count} avertissement(s)" : "")}.", ConsoleColor.Green);
        PrintSettings(p.Settings);
        PrintRules(p.Rules);
        return ExitOk;
    }

    // ------------------------------------------------------------------ test

    private static async Task<int> PolicyTestAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        Banner();
        WriteLine("Simulation de la politique de pilotes sur ce poste (rien n'est installé).", ConsoleColor.Gray);
        var engine = new DriverScanEngine(settings, policyLoader: CreatePolicyLoader(a, settings));
        var lastStage = "";
        var scan = await engine.ScanAsync(new ScanOptions
        {
            DeepScan = a.Flag("deep"),
            IncludeOptional = true,
            OnlySources = ParseSources(a.Value("sources")),
        }, new Progress<ScanProgress>(p =>
        {
            if (p.Stage != lastStage || p.Percent is null) WriteLine($"› {p.Message}", ConsoleColor.Gray);
            lastStage = p.Stage;
        }), ct);

        WriteLine($"\nMachine   : {scan.System.ComputerName} — {scan.System.Manufacturer} {scan.System.DisplayModel}", ConsoleColor.White);
        WriteLine($"Sources   : {string.Join(", ", scan.SourcesUsed)}", ConsoleColor.White);
        foreach (var (src, err) in scan.SourceErrors) WriteLine($"  ⚠ {src} indisponible : {err}", ConsoleColor.Yellow);
        PrintPolicySummary(scan.Policy);
        if (scan.Policy is { } policy)
        {
            var skipped = Enum.GetValues<DriverSourceKind>().Where(k => !policy.Effective.AllowsSource(k)).ToList();
            if (skipped.Count > 0) WriteLine($"  Sources non interrogées (allowedSources) : {string.Join(", ", skipped)}", ConsoleColor.Gray);
        }

        var decisions = scan.PolicyDecisions;
        if (decisions.Count == 0)
        {
            WriteLine("\nAucun pilote proposé : rien à évaluer.", ConsoleColor.Green);
            return ExitOk;
        }

        string Line(CandidateDecision d)
        {
            var c = d.Candidate;
            var devices = c.TargetDeviceNames.Count > 0 ? $" → {string.Join(", ", c.TargetDeviceNames.Distinct().Take(2))}" : "";
            return $"{c.Title} {c.Version} [{c.SourceLabel}, {c.KindLabel}]{devices}";
        }

        var blocked = decisions.Where(d => d.Decision.IsBlocked).ToList();
        var explicitAllow = decisions.Where(d => d.Decision.IsExplicitAllow).ToList();
        var allowed = decisions.Where(d => !d.Decision.IsBlocked && !d.Decision.IsExplicitAllow).ToList();

        WriteLine($"\nBloqués ({blocked.Count}) :", ConsoleColor.White);
        foreach (var d in blocked)
        {
            WriteLine($"  ⛔ {Line(d)}", ConsoleColor.Red);
            WriteLine($"      {(d.Decision.Verdict == PolicyVerdict.Hidden ? "masqué" : "bloqué")} par la politique : {d.Decision.Reason}", ConsoleColor.DarkYellow);
        }
        WriteLine($"\nAutorisés explicitement ({explicitAllow.Count}) :", ConsoleColor.White);
        foreach (var d in explicitAllow)
        {
            WriteLine($"  ✓ {Line(d)}", ConsoleColor.Green);
            WriteLine($"      {d.Decision.Reason}", ConsoleColor.DarkGray);
        }
        WriteLine($"\nAutorisés, aucune règle ne s'applique ({allowed.Count}) :", ConsoleColor.White);
        foreach (var d in allowed) WriteLine($"  · {Line(d)}", ConsoleColor.Gray);

        WriteLine($"\nBilan : {blocked.Count} bloqué(s), {explicitAllow.Count + allowed.Count} installable(s).", ConsoleColor.Cyan);
        return ExitOk;
    }

    // ------------------------------------------------------------------ init

    private static int PolicyInit(Args a)
    {
        var file = a.Positional(1);
        if (file is null)
        {
            WriteLine("Usage : spokapilote policy init <driver-policy.json> [--force]", ConsoleColor.Yellow);
            return ExitUsage;
        }
        var path = Path.GetFullPath(file);
        if (File.Exists(path) && !a.Flag("force"))
        {
            WriteLine($"{path} existe déjà (ajoutez --force pour l'écraser).", ConsoleColor.Red);
            return ExitError;
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, PolicyTemplate.Create(DateTime.Today));
        WriteLine($"✓ Modèle de politique créé : {path}", ConsoleColor.Green);
        WriteLine("  1. Adaptez-le (les règles d'exemple utilisent des identifiants fictifs « XXXX » et ne visent aucun matériel).", ConsoleColor.Gray);
        WriteLine($"  2. Vérifiez-le    : spokapilote policy check \"{path}\"", ConsoleColor.Gray);
        WriteLine($"  3. Testez-le      : spokapilote policy test --policy \"{path}\"", ConsoleColor.Gray);
        WriteLine("  4. Déployez-le    : spokapilote config set PolicySource \\\\srv\\deploiement\\driver-policy.json", ConsoleColor.Gray);
        return ExitOk;
    }
}
