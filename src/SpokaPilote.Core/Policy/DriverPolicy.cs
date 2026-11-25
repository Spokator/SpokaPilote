using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Policy;

/// <summary>
/// Politique de pilotes d'une organisation (fichier <c>driver-policy.json</c>, format documenté dans docs/POLITIQUE.md).
/// <para>
/// Ordre d'évaluation d'un candidat (le premier verdict rendu l'emporte) :
/// <list type="number">
/// <item>« allowedSources » : une source non autorisée n'est même pas interrogée ; aucune règle ne peut la réautoriser.</item>
/// <item>Les règles, dans l'ordre du fichier (règles de l'organisation, puis liste noire de référence SpokaPilote).
///   Les règles expirées sont ignorées. Une règle « block » ou « allow » qui correspond décide et arrête l'évaluation ;
///   une règle « pin » ne décide que si la version proposée dépasse la version figée (blocage), sinon l'évaluation continue.</item>
/// <item>Les réglages généraux (allowFirmware, allowOptional, onlyMissingDrivers, minimumDriverAgeDays) : seule une règle
///   « allow » explicite placée avant les lève.</item>
/// </list>
/// Pour qu'une exception (« allow ») l'emporte sur un blocage plus général, placez-la AVANT ce blocage.
/// </para>
/// </summary>
public sealed class DriverPolicy
{
    /// <summary>Version du format de fichier gérée par cette version de SpokaPilote.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Version du format (« schema »). Un fichier d'un format plus récent est refusé.</summary>
    public int Schema { get; set; } = CurrentSchema;
    /// <summary>Révision de la politique, choisie par l'IT (ex. « 2026.11-3 » ou 3) : affichée dans les rapports.</summary>
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? Version { get; set; }
    public string? Name { get; set; }
    /// <summary>Date de dernière modification (ISO 8601, ex. « 2026-11-25 »).</summary>
    public string? UpdatedAt { get; set; }
    public string? Description { get; set; }
    /// <summary>Note libre ignorée par SpokaPilote.</summary>
    public string? Comment { get; set; }
    public PolicySettings Settings { get; set; } = new();
    public List<PolicyRule> Rules { get; set; } = new();

    [JsonIgnore] public PolicyLayer Layer { get; private set; } = PolicyLayer.Organization;
    [JsonIgnore] public bool IsEmpty => Rules.Count == 0 && Settings.IsDefault;

    private bool _validated;
    private HashSet<DriverSourceKind>? _allowedSources;

    /// <summary>Libellé court pour les journaux et rapports (« Parc Siège » v3).</summary>
    [JsonIgnore]
    public string DisplayName => $"« {Name ?? "sans nom"} »{(string.IsNullOrWhiteSpace(Version) ? "" : " v" + Version)}";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Une faute de frappe dans un critère (« hardwareIds », « versionmax »...) élargirait silencieusement la règle :
        // toute propriété inconnue rend le fichier invalide.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------ lecture et validation

    /// <summary>Lit et valide un fichier de politique. Ne lève jamais d'exception : les problèmes sont dans le résultat.</summary>
    public static PolicyParseResult Parse(string json, PolicyLayer layer = PolicyLayer.Organization)
    {
        var result = new PolicyParseResult();
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new PolicyIssue("Le fichier est vide."));
            return result;
        }
        DriverPolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<DriverPolicy>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            result.Errors.Add(TranslateJsonError(ex));
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            result.Errors.Add(new PolicyIssue($"Contenu illisible : {ex.Message}"));
            return result;
        }
        if (policy is null)
        {
            result.Errors.Add(new PolicyIssue("Le fichier ne contient pas d'objet JSON."));
            return result;
        }
        policy.Layer = layer;
        var validation = policy.Validate();
        result.Errors.AddRange(validation.Errors);
        result.Warnings.AddRange(validation.Warnings);
        result.Policy = policy;
        return result;
    }

    public static PolicyParseResult ParseFile(string path, PolicyLayer layer = PolicyLayer.Organization)
    {
        try
        {
            return Parse(File.ReadAllText(path), layer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var r = new PolicyParseResult();
            r.Errors.Add(new PolicyIssue($"Lecture impossible de {path} : {ex.Message}"));
            return r;
        }
    }

    /// <summary>Contrôle la cohérence et prépare l'évaluation (motifs, versions, dates). Numérote les règles (n°1, n°2...).</summary>
    public PolicyValidation Validate()
    {
        var v = new PolicyValidation();
        if (Schema < 1) v.Errors.Add(new PolicyIssue($"« schema » doit valoir au moins 1 (valeur : {Schema})."));
        else if (Schema > CurrentSchema)
            v.Errors.Add(new PolicyIssue($"Format {Schema} non pris en charge par cette version de SpokaPilote (format maximal : {CurrentSchema}). Mettez SpokaPilote à jour."));
        if (Layer == PolicyLayer.Organization && string.IsNullOrWhiteSpace(Name))
            v.Warnings.Add(new PolicyIssue("Aucun nom (« name ») : renseignez-le pour l'identifier dans les rapports."));
        if (UpdatedAt is not null && ParseDate(UpdatedAt) is null)
            v.Errors.Add(new PolicyIssue($"« updatedAt » illisible : « {UpdatedAt} » (format attendu : 2026-11-25)."));

        Settings ??= new PolicySettings();
        Rules ??= new List<PolicyRule>();
        Settings.Validate(v);
        _allowedSources = Settings.ParsedSources;

        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            if (rule is null)
            {
                v.Errors.Add(new PolicyIssue("Règle vide (null).", i + 1));
                Rules[i] = new PolicyRule();
                continue;
            }
            rule.Number = i + 1;
            rule.Layer = Layer;
            rule.Compile(v);
            if (rule.Id is { Length: > 0 } id)
            {
                if (ids.TryGetValue(id, out var first))
                    v.Warnings.Add(new PolicyIssue($"Identifiant « {id} » déjà utilisé par la règle n°{first}.", rule.Number, rule.Id));
                else ids[id] = rule.Number;
            }
        }

        if (v.Errors.Count == 0) AddShadowWarnings(v);
        _validated = v.Errors.Count == 0;
        return v;
    }

    /// <summary>Signale les règles qui ne s'appliqueront jamais car une règle plus générale placée avant décide toujours.</summary>
    private void AddShadowWarnings(PolicyValidation v)
    {
        for (var j = 1; j < Rules.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                var a = Rules[i];
                var b = Rules[j];
                if (a.ParsedAction == PolicyAction.Pin || a.ExpiresAt is not null || !a.Covers(b)) continue;
                var what = a.ParsedAction == b.ParsedAction ? "elle est redondante" : "elle ne s'appliquera jamais";
                v.Warnings.Add(new PolicyIssue(
                    $"{what} : la règle n°{a.Number} ({a.ActionLabel}), au moins aussi générale et placée avant, décide toujours en premier. " +
                    "Pour une exception, placez-la avant la règle générale.", b.Number, b.Id));
                break;
            }
        }
    }

    private static PolicyIssue TranslateJsonError(JsonException ex)
    {
        var where = ex.LineNumber is { } line ? $" (ligne {line + 1}, colonne {(ex.BytePositionInLine ?? 0) + 1})" : "";
        int? rule = null;
        if (ex.Path is { } path && Regex.Match(path, @"rules\[(\d+)\]", RegexOptions.IgnoreCase) is { Success: true } m)
            rule = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
        var message = ex.Message;
        string text;
        if (message.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase) &&
            Regex.Match(message, "'([^']+)'") is { Success: true } prop)
            text = $"Propriété inconnue « {prop.Groups[1].Value} »{where}. Vérifiez l'orthographe (voir « spokapilote policy init »).";
        else if (ex.Path is { } p2 && p2.Length > 2)
            text = $"JSON invalide ou valeur incorrecte dans « {p2.TrimStart('$', '.')} »{where} (guillemet, virgule ou accolade manquante ? type attendu : texte, nombre, true/false ou liste).";
        else
            text = $"JSON invalide{where} : {FirstSentence(message)}";
        return new PolicyIssue(text, rule);
    }

    private static string FirstSentence(string s)
    {
        var idx = s.IndexOf(". ", StringComparison.Ordinal);
        return idx > 0 ? s[..(idx + 1)] : s;
    }

    // ------------------------------------------------------------------ fusion

    /// <summary>
    /// Politique effective : réglages de l'organisation, puis ses règles, puis celles de la liste de référence
    /// (sauf si « useReferenceBlocklist » vaut false ou si l'identifiant figure dans « ignoreReferenceRules »).
    /// L'organisation est donc prioritaire : une règle « allow » de sa part lève un blocage de la liste de référence.
    /// </summary>
    public static DriverPolicy Combine(DriverPolicy? organization, DriverPolicy? reference)
    {
        if (organization is { _validated: false }) organization.EnsureValid();
        if (reference is { _validated: false }) reference.EnsureValid();
        var settings = organization?.Settings ?? new PolicySettings();
        var rules = new List<PolicyRule>();
        if (organization is not null) rules.AddRange(organization.Rules);
        if (reference is not null && settings.UseReferenceBlocklist)
            rules.AddRange(reference.Rules.Where(r => r.Id is null ||
                !settings.IgnoreReferenceRules.Contains(r.Id, StringComparer.OrdinalIgnoreCase)));
        return new DriverPolicy
        {
            Schema = CurrentSchema,
            Name = organization?.Name,
            Version = organization?.Version,
            UpdatedAt = organization?.UpdatedAt,
            Description = organization?.Description,
            Settings = settings,
            Rules = rules,
            Layer = organization?.Layer ?? PolicyLayer.Organization,
            _validated = true,
            _allowedSources = settings.ParsedSources,
        };
    }

    private void EnsureValid()
    {
        if (_validated) return;
        var v = Validate();
        if (v.Errors.Count > 0)
            throw new InvalidOperationException("Politique invalide : " + string.Join(" ; ", v.Errors));
    }

    // ------------------------------------------------------------------ évaluation

    /// <summary>La source peut-elle être interrogée ? (« allowedSources » absent = toutes.)</summary>
    public bool AllowsSource(DriverSourceKind kind)
    {
        EnsureValid();
        return _allowedSources is null || _allowedSources.Contains(kind);
    }

    /// <summary>Décision pour un candidat, compte tenu des périphériques qu'il cible (leurs ID matériels comptent pour « hardwareId »).</summary>
    public PolicyDecision Evaluate(DriverCandidate candidate, IReadOnlyList<DeviceInfo>? devices = null, DateTime? now = null)
    {
        EnsureValid();
        var at = now ?? DateTime.Now;
        var targets = devices is null
            ? new List<DeviceInfo>()
            : devices.Where(d => candidate.TargetDeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)).ToList();

        if (!AllowsSource(candidate.Source))
            return PolicyDecision.Block($"Source « {candidate.SourceLabel} » non autorisée par la politique (allowedSources)", setting: "allowedSources");

        foreach (var rule in Rules)
        {
            if (rule.IsExpired(at) || !rule.Matches(candidate, targets)) continue;
            switch (rule.ParsedAction)
            {
                case PolicyAction.Allow:
                    return PolicyDecision.Allow(rule);
                case PolicyAction.Block:
                    return PolicyDecision.Block(rule.Reason ?? "Pilote bloqué par la politique", rule);
                case PolicyAction.Pin:
                    var cmp = DriverVersion.Compare(candidate.Version, rule.PinVersion);
                    if (cmp is null)
                        return PolicyDecision.Block($"{rule.Reason ?? $"Version figée à {rule.PinVersion}"} — version proposée illisible ({candidate.Version ?? "inconnue"})", rule);
                    if (cmp > 0)
                        return PolicyDecision.Block($"{rule.Reason ?? "Version figée par la politique"} — version {candidate.Version} supérieure à la version figée {rule.PinVersion}", rule);
                    break; // version conforme : les règles suivantes peuvent encore s'appliquer
            }
        }

        var s = Settings;
        if (!s.AllowFirmware && candidate.Kind == CandidateKind.Firmware)
            return PolicyDecision.Block("Mises à jour BIOS / firmware interdites par la politique (allowFirmware)", setting: "allowFirmware");
        if (!s.AllowOptional && candidate.Kind == CandidateKind.Optional)
            return PolicyDecision.Block("Pilotes optionnels interdits par la politique (allowOptional)", setting: "allowOptional");
        if (s.OnlyMissingDrivers && candidate.Kind != CandidateKind.MissingDriver)
            return PolicyDecision.Block("La politique n'autorise que les pilotes manquants (onlyMissingDrivers)", setting: "onlyMissingDrivers");
        if (s.MinimumDriverAgeDays > 0 && candidate.Kind != CandidateKind.MissingDriver && candidate.Date is { } date)
        {
            var age = (at.Date - date.Date).TotalDays;
            if (age < s.MinimumDriverAgeDays)
                return PolicyDecision.Block(
                    $"Pilote daté du {date:dd/MM/yyyy} ({Math.Max(0, (int)age)} j) : la politique impose {s.MinimumDriverAgeDays} jours de recul (minimumDriverAgeDays)",
                    setting: "minimumDriverAgeDays");
        }
        return PolicyDecision.Default;
    }

    /// <summary>
    /// Applique la politique aux candidats d'une analyse : renseigne <see cref="DriverCandidate.BlockedReason"/>,
    /// retire de la liste les candidats d'une règle « hide », et écarte les alternatives bloquées.
    /// </summary>
    public List<CandidateDecision> Apply(List<DriverCandidate> candidates, IReadOnlyList<DeviceInfo> devices, DateTime? now = null)
    {
        var decisions = new List<CandidateDecision>();
        foreach (var c in candidates.ToList())
        {
            var decision = Evaluate(c, devices, now);
            decisions.Add(new CandidateDecision(c, decision));
            if (decision.IsBlocked) c.BlockedReason ??= decision.Reason;
            if (decision.Verdict == PolicyVerdict.Hidden) candidates.Remove(c);

            // Alternatives (autres paquets de la même source essayés en repli) : une version bloquée n'est jamais tentée.
            for (var i = c.Alternatives.Count - 1; i >= 0; i--)
            {
                var alt = c.Alternatives[i];
                if (alt.TargetDeviceIds.Count == 0) alt.TargetDeviceIds.AddRange(c.TargetDeviceIds);
                if (Evaluate(alt, devices, now).IsBlocked) c.Alternatives.RemoveAt(i);
            }
        }
        return decisions;
    }

    // ------------------------------------------------------------------ utilitaires

    internal static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }

    /// <summary>Motif à jokers (* = n'importe quelle suite, ? = un caractère), insensible à la casse, couvrant toute la valeur.</summary>
    internal static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
}

public enum PolicyLayer { Organization, Reference }

public enum PolicyAction { Block, Allow, Pin }

public enum PolicyVerdict { Allowed, Blocked, Hidden }

/// <summary>Réglages généraux de la politique.</summary>
public sealed class PolicySettings
{
    /// <summary>false = les mises à jour BIOS / firmware sont toujours bloquées.</summary>
    public bool AllowFirmware { get; set; } = true;
    /// <summary>false = les pilotes « optionnels » de Windows Update sont bloqués.</summary>
    public bool AllowOptional { get; set; } = true;
    /// <summary>Sources autorisées (noms DriverSourceKind : WindowsUpdate, MicrosoftCatalog, Dell, HP, Lenovo, LocalRepository, Nvidia, Intel, Amd). Absent = toutes.</summary>
    public List<string>? AllowedSources { get; set; }
    /// <summary>true = seuls les pilotes manquants (périphériques sans pilote) sont autorisés.</summary>
    public bool OnlyMissingDrivers { get; set; }
    /// <summary>Recul minimal (jours) avant d'installer une nouvelle version (ne s'applique pas aux pilotes manquants). 0 = aucun.</summary>
    public int MinimumDriverAgeDays { get; set; }
    /// <summary>Ajoute la liste noire de référence SpokaPilote après les règles de l'organisation (true par défaut).</summary>
    public bool UseReferenceBlocklist { get; set; } = true;
    /// <summary>Identifiants de règles de la liste de référence à ne pas appliquer.</summary>
    public List<string> IgnoreReferenceRules { get; set; } = new();
    public string? Comment { get; set; }

    [JsonIgnore] internal HashSet<DriverSourceKind>? ParsedSources { get; private set; }

    [JsonIgnore]
    public bool IsDefault => AllowFirmware && AllowOptional && AllowedSources is null && !OnlyMissingDrivers && MinimumDriverAgeDays == 0;

    internal void Validate(PolicyValidation v)
    {
        IgnoreReferenceRules ??= new List<string>();
        ParsedSources = null;
        if (MinimumDriverAgeDays is < 0 or > 3650)
            v.Errors.Add(new PolicyIssue($"« settings.minimumDriverAgeDays » doit être compris entre 0 et 3650 (valeur : {MinimumDriverAgeDays})."));
        if (AllowedSources is not null)
        {
            if (AllowedSources.Count == 0)
            {
                v.Errors.Add(new PolicyIssue("« settings.allowedSources » est vide : aucune source ne serait autorisée. Supprimez la clé pour toutes les autoriser."));
                return;
            }
            var set = new HashSet<DriverSourceKind>();
            foreach (var name in AllowedSources)
            {
                if (Enum.TryParse<DriverSourceKind>(name?.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind)) set.Add(kind);
                else v.Errors.Add(new PolicyIssue($"« settings.allowedSources » : source inconnue « {name} » (valeurs : {string.Join(", ", Enum.GetNames<DriverSourceKind>())})."));
            }
            ParsedSources = set;
        }
    }
}

/// <summary>
/// Règle : une action (block / allow / pin) et des critères combinés par ET. Un critère qui accepte une liste
/// (hardwareId, title, manufacturer, category, deviceName, source, kind, versions) est satisfait si l'une de ses valeurs correspond.
/// </summary>
public sealed class PolicyRule
{
    public string? Id { get; set; }
    /// <summary>block | allow | pin</summary>
    public string? Action { get; set; }
    /// <summary>Motif affiché à l'utilisateur (ex. « Écrans bleus constatés sur les Latitude 5420, ticket #4512 »).</summary>
    public string? Reason { get; set; }
    /// <summary>Date (incluse) après laquelle la règle est ignorée, ex. « 2026-12-31 ».</summary>
    public string? Expires { get; set; }
    /// <summary>block / pin : retire complètement le candidat de la liste au lieu de l'afficher comme bloqué.</summary>
    public bool Hide { get; set; }
    /// <summary>pin : version maximale autorisée (incluse).</summary>
    public string? PinVersion { get; set; }
    public string? Comment { get; set; }
    /// <summary>Source publique du signalement (liste de référence).</summary>
    public string? SourceUrl { get; set; }

    [JsonConverter(typeof(StringOrListConverter))] public List<string>? HardwareId { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Title { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Manufacturer { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Category { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? DeviceName { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Source { get; set; }
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Kind { get; set; }
    public string? VersionMin { get; set; }
    public string? VersionMax { get; set; }
    /// <summary>Versions exactes (liste).</summary>
    [JsonConverter(typeof(StringOrListConverter))] public List<string>? Versions { get; set; }

    /// <summary>Numéro de la règle dans son fichier (1 = première).</summary>
    [JsonIgnore] public int Number { get; internal set; }
    [JsonIgnore] public PolicyLayer Layer { get; internal set; }
    [JsonIgnore] public PolicyAction ParsedAction { get; private set; }
    [JsonIgnore] public DateTime? ExpiresAt { get; private set; }

    private Regex[]? _hw, _title, _manufacturer, _category, _device;
    private HashSet<DriverSourceKind>? _sources;
    private HashSet<CandidateKind>? _kinds;
    private Version? _min, _max;
    private List<Version>? _exact;

    [JsonIgnore] public string Label => Id is { Length: > 0 } ? $"« {Id} »" : $"n°{Number}";
    [JsonIgnore] public string DisplayRef => Layer == PolicyLayer.Reference ? $"liste de référence SpokaPilote, règle {Label}" : $"règle {Label}";
    [JsonIgnore] public string ActionLabel => ParsedAction switch { PolicyAction.Allow => "allow", PolicyAction.Pin => "pin", _ => "block" };

    public bool IsExpired(DateTime now) => ExpiresAt is { } e && now > e;

    /// <summary>Résumé lisible des critères (pour « policy show »).</summary>
    [JsonIgnore]
    public string CriteriaSummary
    {
        get
        {
            var parts = new List<string>();
            void Add(string name, List<string>? values) { if (values is { Count: > 0 }) parts.Add($"{name}={string.Join("|", values)}"); }
            Add("hardwareId", HardwareId);
            Add("title", Title);
            Add("manufacturer", Manufacturer);
            Add("category", Category);
            Add("deviceName", DeviceName);
            Add("source", Source);
            Add("kind", Kind);
            Add("versions", Versions);
            if (VersionMin is not null || VersionMax is not null) parts.Add($"version {VersionMin ?? "…"} → {VersionMax ?? "…"}");
            if (ParsedAction == PolicyAction.Pin) parts.Add($"figée à {PinVersion}");
            return string.Join(" ; ", parts);
        }
    }

    internal void Compile(PolicyValidation v)
    {
        void Error(string m) => v.Errors.Add(new PolicyIssue(m, Number, Id));
        void Warn(string m) => v.Warnings.Add(new PolicyIssue(m, Number, Id));

        switch (Action?.Trim().ToLowerInvariant())
        {
            case "block" or "bloquer" or "deny": ParsedAction = PolicyAction.Block; break;
            case "allow" or "autoriser": ParsedAction = PolicyAction.Allow; break;
            case "pin" or "figer": ParsedAction = PolicyAction.Pin; break;
            case null or "": Error("« action » manquante (block, allow ou pin)."); break;
            default: Error($"Action inconnue « {Action} » (valeurs : block, allow, pin)."); break;
        }

        Regex[]? Patterns(string name, List<string>? values)
        {
            if (values is null) return null;
            if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
            {
                Error($"« {name} » contient une valeur vide.");
                return null;
            }
            return values.Select(DriverPolicy.Glob).ToArray();
        }
        _hw = Patterns("hardwareId", HardwareId);
        _title = Patterns("title", Title);
        _manufacturer = Patterns("manufacturer", Manufacturer);
        _category = Patterns("category", Category);
        _device = Patterns("deviceName", DeviceName);

        HashSet<T>? Enums<T>(string name, List<string>? values) where T : struct, Enum
        {
            if (values is null) return null;
            var set = new HashSet<T>();
            foreach (var s in values)
            {
                if (Enum.TryParse<T>(s?.Trim(), ignoreCase: true, out var e) && Enum.IsDefined(e)) set.Add(e);
                else Error($"« {name} » : valeur inconnue « {s} » (valeurs : {string.Join(", ", Enum.GetNames<T>())}).");
            }
            return set;
        }
        _sources = Enums<DriverSourceKind>("source", Source);
        _kinds = Enums<CandidateKind>("kind", Kind);

        Version? Ver(string name, string? text)
        {
            if (text is null) return null;
            var parsed = DriverVersion.Parse(text);
            if (parsed is null) Error($"« {name} » : version illisible « {text} ».");
            return parsed;
        }
        _min = Ver("versionMin", VersionMin);
        _max = Ver("versionMax", VersionMax);
        if (_min is not null && _max is not null && _min > _max) Error($"« versionMin » ({VersionMin}) est supérieure à « versionMax » ({VersionMax}).");
        if (Versions is not null)
        {
            _exact = new List<Version>();
            if (Versions.Count == 0) Error("« versions » est une liste vide.");
            foreach (var text in Versions)
                if (Ver("versions", text) is { } parsed) _exact.Add(parsed);
        }

        if (ParsedAction == PolicyAction.Pin)
        {
            if (string.IsNullOrWhiteSpace(PinVersion)) Error("Une règle « pin » doit indiquer « pinVersion » (version maximale autorisée).");
            else Ver("pinVersion", PinVersion);
        }
        else if (PinVersion is not null && Action is not null) Warn("« pinVersion » n'a de sens que pour l'action « pin » : ignoré.");
        if (ParsedAction == PolicyAction.Allow && Hide) Warn("« hide » est ignoré pour une règle « allow ».");

        ExpiresAt = null;
        if (Expires is not null)
        {
            var date = DriverPolicy.ParseDate(Expires);
            if (date is null) Error($"« expires » illisible : « {Expires} » (format attendu : 2026-12-31).");
            else
            {
                // Une date sans heure couvre toute la journée.
                ExpiresAt = Expires.Trim().Length <= 10 ? date.Value.Date.AddDays(1).AddTicks(-1) : date;
                if (ExpiresAt < DateTime.Now) Warn($"Règle expirée depuis le {ExpiresAt:dd/MM/yyyy} : elle est ignorée.");
            }
        }

        if (!HasCriteria)
            Error("Aucun critère (hardwareId, title, manufacturer, category, deviceName, source, kind, versionMin/versionMax, versions) : " +
                  "la règle s'appliquerait à tous les pilotes. Pour tout viser volontairement, indiquez \"title\": \"*\".");
        if (ParsedAction != PolicyAction.Allow && string.IsNullOrWhiteSpace(Reason) && Layer == PolicyLayer.Organization)
            Warn("Aucun motif (« reason ») : un motif générique sera affiché aux techniciens.");
    }

    private bool HasCriteria => HardwareId is not null || Title is not null || Manufacturer is not null || Category is not null ||
                                DeviceName is not null || Source is not null || Kind is not null ||
                                VersionMin is not null || VersionMax is not null || Versions is not null;

    internal bool Matches(DriverCandidate c, IReadOnlyList<DeviceInfo> targets)
    {
        static bool Any(Regex[] patterns, IEnumerable<string?> values) =>
            values.Any(v => v is not null && patterns.Any(p => p.IsMatch(v)));

        if (_sources is not null && !_sources.Contains(c.Source)) return false;
        if (_kinds is not null && !_kinds.Contains(c.Kind)) return false;
        if (_hw is not null)
        {
            var ids = new List<string?> { c.MatchedHardwareId };
            foreach (var d in targets) { ids.AddRange(d.HardwareIds); ids.AddRange(d.CompatibleIds); }
            if (!Any(_hw, ids)) return false;
        }
        if (_title is not null && !Any(_title, new[] { c.Title })) return false;
        if (_manufacturer is not null && !Any(_manufacturer, targets.Select(d => d.Manufacturer).Prepend(c.Manufacturer))) return false;
        if (_category is not null && !Any(_category, targets.Select(d => d.ClassName).Prepend(c.Category))) return false;
        if (_device is not null && !Any(_device, targets.Select(d => d.Name).Concat(c.TargetDeviceNames))) return false;
        if (_min is not null || _max is not null || _exact is not null)
        {
            // Version illisible : les critères de version ne correspondent pas (la règle ne s'applique pas).
            var version = DriverVersion.Parse(c.Version);
            if (version is null) return false;
            if (_min is not null && version < _min) return false;
            if (_max is not null && version > _max) return false;
            if (_exact is not null && !_exact.Contains(version)) return false;
        }
        return true;
    }

    /// <summary>Vrai si tout candidat visé par <paramref name="other"/> l'est forcément par cette règle (estimation prudente).</summary>
    internal bool Covers(PolicyRule other)
    {
        static bool Patterns(List<string>? mine, List<string>? theirs, Regex[]? rx) =>
            mine is null || (theirs is not null && rx is not null && theirs.All(t => rx.Any(r => r.IsMatch(t))));
        static bool Set<T>(HashSet<T>? mine, HashSet<T>? theirs) => mine is null || (theirs is not null && theirs.IsSubsetOf(mine));

        if (!Patterns(HardwareId, other.HardwareId, _hw) || !Patterns(Title, other.Title, _title) ||
            !Patterns(Manufacturer, other.Manufacturer, _manufacturer) || !Patterns(Category, other.Category, _category) ||
            !Patterns(DeviceName, other.DeviceName, _device) || !Set(_sources, other._sources) || !Set(_kinds, other._kinds))
            return false;

        if (_exact is not null)
            return other._exact is not null && other._exact.All(_exact.Contains);
        var otherMin = other._exact is { Count: > 0 } ? other._exact.Min() : other._min;
        var otherMax = other._exact is { Count: > 0 } ? other._exact.Max() : other._max;
        if (_min is not null && (otherMin is null || otherMin < _min)) return false;
        if (_max is not null && (otherMax is null || otherMax > _max)) return false;
        return true;
    }
}

public sealed record PolicyDecision(PolicyVerdict Verdict, string? Reason, PolicyRule? Rule = null, string? Setting = null)
{
    public static readonly PolicyDecision Default = new(PolicyVerdict.Allowed, null);

    public bool IsBlocked => Verdict != PolicyVerdict.Allowed;
    public bool IsExplicitAllow => Verdict == PolicyVerdict.Allowed && Rule is not null;

    internal static PolicyDecision Allow(PolicyRule rule) =>
        new(PolicyVerdict.Allowed, $"Autorisé explicitement ({rule.DisplayRef}){(rule.Reason is null ? "" : " : " + rule.Reason)}", rule);

    internal static PolicyDecision Block(string reason, PolicyRule? rule = null, string? setting = null) =>
        new(rule?.Hide == true ? PolicyVerdict.Hidden : PolicyVerdict.Blocked,
            rule is null ? reason : $"{reason} ({rule.DisplayRef})", rule, setting);

    /// <summary>Texte pour l'affichage (« policy test »).</summary>
    public string Describe() => Verdict switch
    {
        PolicyVerdict.Hidden => $"masqué : {Reason}",
        PolicyVerdict.Blocked => $"bloqué : {Reason}",
        _ => Reason ?? "autorisé (aucune règle ne s'applique)",
    };
}

public sealed record CandidateDecision(DriverCandidate Candidate, PolicyDecision Decision);

/// <summary>Problème détecté dans une politique. <see cref="RuleNumber"/> = numéro de la règle (1 = première), null = global.</summary>
public sealed record PolicyIssue(string Message, int? RuleNumber = null, string? RuleId = null)
{
    public override string ToString() => RuleNumber is null
        ? Message
        : $"Règle n°{RuleNumber}{(string.IsNullOrEmpty(RuleId) ? "" : $" (« {RuleId} »)")} : {Message}";
}

public sealed class PolicyValidation
{
    public List<PolicyIssue> Errors { get; } = new();
    public List<PolicyIssue> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public sealed class PolicyParseResult
{
    /// <summary>Politique lue (null si le JSON est illisible). N'est utilisable que si <see cref="IsValid"/>.</summary>
    public DriverPolicy? Policy { get; set; }
    public List<PolicyIssue> Errors { get; } = new();
    public List<PolicyIssue> Warnings { get; } = new();
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

/// <summary>Accepte "valeur" ou ["valeur1", "valeur2"].</summary>
internal sealed class StringOrListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.String: return new List<string> { reader.GetString()! };
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException("Liste de textes attendue.");
                    list.Add(reader.GetString()!);
                }
                return list;
            default: throw new JsonException("Texte ou liste de textes attendu.");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}

/// <summary>Accepte "3" ou 3 (révision de la politique).</summary>
internal sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => RawText(reader),
        _ => throw new JsonException("Texte ou nombre attendu."),
    };

    private static string RawText(Utf8JsonReader reader) =>
        System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray());

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
