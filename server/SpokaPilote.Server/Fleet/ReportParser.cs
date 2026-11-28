using System.Globalization;
using System.Text.Json;

namespace SpokaPilote.Server.Fleet;

/// <summary>Rapport refusé : le message est renvoyé tel quel au poste (HTTP 400).</summary>
public sealed class InvalidReportException(string message) : Exception(message);

/// <summary>
/// Lit un rapport JSON produit par « spokapilote scan --json » (format ScanReport, camelCase) sans dépendre de
/// SpokaPilote.Core, qui cible Windows. La lecture est tolérante : champs inconnus ignorés, casse indifférente,
/// champs facultatifs absents acceptés.
/// </summary>
public static class ReportParser
{
    /// <summary>Marqueurs d'ID matériel d'un périphérique USB qui ne répond pas : panne matérielle, pas un pilote manquant.</summary>
    private static readonly string[] HardwareFaultMarkers =
        ["DESCRIPTOR_FAILURE", "RESET_FAILURE", "SET_ADDRESS_FAILURE", "DESCRIPTOR_VALIDATION_FAILURE"];

    public const string HardwareFaultHint =
        "Problème matériel USB (câble, port, alimentation ou périphérique défectueux) : aucun pilote ne peut le corriger.";

    public static MachineReport Parse(string json, DateTimeOffset? receivedAt = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidReportException($"Le rapport n'est pas un JSON valide : {ex.Message}");
        }
        using (doc) return Parse(doc.RootElement, receivedAt);
    }

    public static MachineReport Parse(JsonElement root, DateTimeOffset? receivedAt = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidReportException("Le rapport doit être un objet JSON (format « spokapilote scan --json »).");
        if (Prop(root, "system") is not { ValueKind: JsonValueKind.Object } system)
            throw new InvalidReportException("Rapport incomplet : section « system » absente.");

        var snapshot = new SystemSnapshot
        {
            ComputerName = Str(system, "computerName")?.Trim() ?? "",
            MachineId = NormalizeMachineId(Str(system, "machineId")),
            Manufacturer = Str(system, "manufacturer"),
            Model = Str(system, "model"),
            FriendlyModel = Str(system, "friendlyModel"),
            SerialNumber = Str(system, "serialNumber"),
            BiosVersion = Str(system, "biosVersion"),
            OsCaption = Str(system, "osCaption"),
            OsDisplayVersion = Str(system, "osDisplayVersion"),
            OsVersion = Str(system, "osVersion"),
            Architecture = Str(system, "architecture"),
        };
        if (!IsValidMachineName(snapshot.ComputerName))
            throw new InvalidReportException("Rapport incomplet : nom du poste (system.computerName) absent ou invalide.");

        if (Prop(root, "devices") is not { ValueKind: JsonValueKind.Array } devices)
            throw new InvalidReportException("Rapport incomplet : liste « devices » absente. Régénérez-le avec « spokapilote scan --json ».");

        var report = new MachineReport
        {
            Name = snapshot.ComputerName.ToUpperInvariant(),
            Timestamp = Date(root, "timestamp") ?? receivedAt ?? DateTimeOffset.UtcNow,
            AppVersion = Str(root, "appVersion"),
            DurationSeconds = Prop(root, "durationSeconds") is { ValueKind: JsonValueKind.Number } d ? d.GetDouble() : 0,
            System = snapshot,
            Sources = Strings(Prop(root, "sources")),
        };

        if (Prop(root, "sourceErrors") is { ValueKind: JsonValueKind.Object } errors)
            foreach (var e in errors.EnumerateObject())
                report.SourceErrors[e.Name] = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() ?? "" : e.Value.ToString();

        if (Prop(root, "candidates") is { ValueKind: JsonValueKind.Array } candidates)
            foreach (var c in candidates.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                report.Candidates.Add(new CandidateEntry
                {
                    Title = Str(c, "title") ?? "",
                    Version = Str(c, "version"),
                    Source = Str(c, "source"),
                    Kind = Str(c, "kind"),
                    Category = Str(c, "category"),
                    Devices = Strings(Prop(c, "devices")),
                    DeviceIds = Strings(Prop(c, "deviceIds")),
                    BlockedReason = Str(c, "blockedReason"),
                    ItemId = Str(c, "itemId"),
                    // Repli si le périphérique visé est absent du rapport : l'ID matériel qui a permis l'association.
                    HardwareVendor = Rollout.RolloutKeys.HardwareVendor(Str(c, "matchedHardwareId")),
                });

        if (Prop(root, "installation") is { ValueKind: JsonValueKind.Array } installation)
            report.Installation = installation.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object).Select(o => new OutcomeEntry
            {
                Title = Str(o, "title") ?? "",
                Version = Str(o, "version"),
                Status = Str(o, "status") ?? "",
                Message = Str(o, "message"),
                RebootRequired = Prop(o, "rebootRequired") is { ValueKind: JsonValueKind.True },
                ItemId = Str(o, "itemId"),
            }).ToList();

        report.Health = ParseHealth(Prop(root, "health"));

        // Pilotes autorisés / bloqués par ID d'instance, pour savoir quels périphériques en erreur ont une solution.
        var allowed = new HashSet<string>(report.Candidates.Where(c => !c.IsBlocked).SelectMany(c => c.DeviceIds), StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(report.Candidates.Where(c => c.IsBlocked).SelectMany(c => c.DeviceIds), StringComparer.OrdinalIgnoreCase);

        // Matériel visé par chaque proposition (constructeur de la puce, classe du périphérique) : identité du pilote d'une source
        // à l'autre pour la diffusion par anneaux. Seuls les périphériques visés par une proposition sont lus.
        var targeted = new Dictionary<string, List<CandidateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in report.Candidates)
            if (c.DeviceIds.FirstOrDefault() is { } first)
            {
                if (!targeted.TryGetValue(first, out var list)) targeted[first] = list = new List<CandidateEntry>(1);
                list.Add(c);
            }

        foreach (var dev in devices.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
        {
            if (targeted.Count > 0 && Str(dev, "instanceId") is { } targetId && targeted.TryGetValue(targetId, out var aimed) &&
                Strings(Prop(dev, "hardwareIds")).Concat(Strings(Prop(dev, "compatibleIds"))).Select(Rollout.RolloutKeys.HardwareVendor)
                    .FirstOrDefault(v => v is not null) is { } vendor)
            {
                var deviceClass = Rollout.RolloutKeys.NormalizeClass(Str(dev, "className"));
                foreach (var c in aimed) { c.HardwareVendor = vendor; c.DeviceClass = deviceClass; }
            }

            // Un périphérique débranché (isPresent=false) ne compte pas.
            if (Prop(dev, "isPresent") is { ValueKind: JsonValueKind.False }) continue;
            report.DeviceCount++;

            var errorCode = Prop(dev, "errorCode") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var v) ? v : 0;
            if (!HasProblem(errorCode)) continue;

            var hardwareIds = Strings(Prop(dev, "hardwareIds"));
            var instanceId = Str(dev, "instanceId") ?? "";
            var hardwareFault = IsHardwareFault(hardwareIds);
            report.ProblemDevices.Add(new DeviceEntry
            {
                Name = Str(dev, "name") ?? instanceId,
                InstanceId = instanceId,
                HardwareIds = hardwareIds.Take(8).ToList(),
                ErrorCode = errorCode,
                StatusText = Str(dev, "statusText") ?? StatusText(errorCode),
                Hint = Str(dev, "hint") ?? (hardwareFault ? HardwareFaultHint : null),
                DriverVersion = Str(dev, "driverVersion"),
                DriverProvider = Str(dev, "driverProvider"),
                IsHardwareFault = hardwareFault,
                HasCandidate = allowed.Contains(instanceId),
                OnlyBlockedCandidates = !allowed.Contains(instanceId) && blocked.Contains(instanceId),
            });
        }
        return report;
    }

    /// <summary>
    /// Objet facultatif « health » (contrat partagé) : { score 0-100, grade "A".."E", findings [...] }. Lecture tolérante :
    /// valeurs hors bornes ramenées dans les bornes, gravité inconnue traitée comme « info », 200 constats au plus.
    /// </summary>
    internal static HealthInfo? ParseHealth(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } health) return null;
        var info = new HealthInfo();
        if (Prop(health, "score") is { ValueKind: JsonValueKind.Number } score && score.TryGetDouble(out var s) && double.IsFinite(s))
            info.Score = (int)Math.Round(Math.Clamp(s, 0, 100));
        var grade = Str(health, "grade")?.Trim().ToUpperInvariant();
        info.Grade = grade is "A" or "B" or "C" or "D" or "E" ? grade : null;
        if (Prop(health, "findings") is { ValueKind: JsonValueKind.Array } findings)
            info.Findings = findings.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.Object)
                .Take(200)
                .Select(f => new HealthFinding
                {
                    Severity = Str(f, "severity")?.Trim().ToLowerInvariant() switch
                    {
                        "critical" => "critical",
                        "warning" => "warning",
                        _ => "info",
                    },
                    Code = Truncate(Str(f, "code"), 100) ?? "",
                    Title = Truncate(Str(f, "title"), 300) ?? "",
                    Detail = Truncate(Str(f, "detail"), 2000),
                    DeviceId = Truncate(Str(f, "deviceId"), 400),
                })
                .OrderBy(f => f.Rank)
                .ToList();
        return info;
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max] + "…";

    /// <summary>MachineGuid au format GUID, en minuscules ; toute autre valeur est ignorée.</summary>
    public static string? NormalizeMachineId(string? value) =>
        Guid.TryParse(value?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : null;

    /// <summary>Même règle que SpokaPilote.Core (DeviceInfo.HasProblem) : erreur, sauf 0 (OK) et 22 (désactivé volontairement).</summary>
    public static bool HasProblem(int errorCode) => errorCode is not (0 or 22);

    public static bool IsHardwareFault(IEnumerable<string> hardwareIds) =>
        hardwareIds.Any(id => HardwareFaultMarkers.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Nom NetBIOS/DNS raisonnable : sert de clé et apparaît dans les URL de l'API.</summary>
    public static bool IsValidMachineName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string StatusText(int errorCode) => errorCode switch
    {
        0 => "Fonctionne correctement",
        1 => "Mal configuré (pilote absent ou incorrect)",
        3 => "Pilote endommagé ou mémoire insuffisante",
        10 => "Ne peut pas démarrer",
        12 => "Ressources insuffisantes",
        14 => "Redémarrage nécessaire",
        18 => "Pilote à réinstaller",
        22 => "Désactivé",
        28 => "Aucun pilote installé",
        31 => "Le pilote n'a pas pu être chargé",
        39 => "Pilote corrompu ou manquant",
        43 => "Arrêté suite à un problème signalé",
        45 => "Non connecté",
        52 => "Signature du pilote invalide",
        _ => $"Erreur {errorCode}",
    };

    // ------------------------------------------------------------------ lecture tolérante

    private static JsonElement? Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (obj.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement obj, string name) => Prop(obj, name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() is { Length: > 0 } v ? v : null,
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    private static DateTimeOffset? Date(JsonElement obj, string name) =>
        Str(obj, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static List<string> Strings(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
            : new List<string>();
}
