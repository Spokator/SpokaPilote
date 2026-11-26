using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpokaPilote.Server.Fleet;

namespace SpokaPilote.Server.Groups;

/// <summary>Condition d'une règle automatique : champ du poste, opérateur, valeur.</summary>
public sealed class GroupCondition
{
    /// <summary>name, manufacturer, model, os ou subnet.</summary>
    public string Field { get; set; } = "";
    /// <summary>startsWith, contains, equals, wildcard (champs texte) ou inSubnet (sous-réseau).</summary>
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Règles automatiques d'un groupe : un poste en fait partie s'il remplit toutes (all) ou une (any) des conditions.</summary>
public sealed class GroupRules
{
    public string Match { get; set; } = GroupMatcher.MatchAll;
    public List<GroupCondition> Conditions { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Conditions.Count == 0;
}

/// <summary>Groupe de postes enregistré : membres ajoutés à la main (clés internes des postes) et règles automatiques.</summary>
public sealed record GroupRecord(long Id, string Name, string? Description, GroupRules Rules, IReadOnlySet<long> ManualMembers,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class GroupValidationException(string message) : Exception(message);

/// <summary>
/// Évaluation des règles de groupe (sans état, testable seule). Comparaisons insensibles à la casse ; un groupe sans
/// condition ne contient que ses membres ajoutés à la main.
/// </summary>
public static class GroupMatcher
{
    public const string MatchAll = "all";
    public const string MatchAny = "any";
    public const int MaxConditions = 20;
    public const int MaxValueLength = 200;

    /// <summary>Champs disponibles et leur libellé (console).</summary>
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["name"] = "Nom du poste",
        ["manufacturer"] = "Constructeur",
        ["model"] = "Modèle",
        ["os"] = "Windows",
        ["subnet"] = "Sous-réseau (adresse du dernier rapport)",
    };

    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        ["startsWith"] = "commence par",
        ["contains"] = "contient",
        ["equals"] = "est égal à",
        ["wildcard"] = "correspond au motif",
        ["inSubnet"] = "est dans",
    };

    /// <summary>Vérifie et normalise des règles reçues de la console ; lève GroupValidationException (message en français).</summary>
    public static GroupRules Normalize(GroupRules? rules)
    {
        if (rules is null) return new GroupRules();
        var match = (rules.Match ?? MatchAll).Trim().ToLowerInvariant();
        if (match is not (MatchAll or MatchAny)) throw new GroupValidationException("Combinaison des conditions inconnue : « all » (toutes) ou « any » (au moins une).");
        var list = rules.Conditions ?? new List<GroupCondition>();
        if (list.Count > MaxConditions) throw new GroupValidationException($"{MaxConditions} conditions au plus par groupe.");
        var normalized = new List<GroupCondition>();
        foreach (var c in list)
        {
            var field = Fields.Keys.FirstOrDefault(f => f.Equals(c.Field?.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new GroupValidationException($"Champ de condition inconnu : « {c.Field} ».");
            var op = Operators.Keys.FirstOrDefault(o => o.Equals(c.Operator?.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw new GroupValidationException($"Opérateur inconnu : « {c.Operator} ».");
            var value = (c.Value ?? "").Trim();
            if (value.Length == 0) throw new GroupValidationException($"Condition « {Fields[field]} » : valeur vide.");
            if (value.Length > MaxValueLength) throw new GroupValidationException($"Condition « {Fields[field]} » : valeur trop longue ({MaxValueLength} caractères au plus).");
            if (field == "subnet")
            {
                if (op != "inSubnet") throw new GroupValidationException("Le sous-réseau s'utilise avec l'opérateur « est dans ».");
                foreach (var part in SplitSubnets(value))
                    if (!TryParseSubnet(part, out _, out _))
                        throw new GroupValidationException($"Sous-réseau invalide : « {part} » (exemples : 10.1.20.0/24, 192.168.1.10, fd00::/64).");
            }
            else if (op == "inSubnet")
            {
                throw new GroupValidationException("L'opérateur « est dans » ne s'applique qu'au sous-réseau.");
            }
            normalized.Add(new GroupCondition { Field = field, Operator = op, Value = value });
        }
        return new GroupRules { Match = match, Conditions = normalized };
    }

    /// <summary>Le poste remplit-il les règles ? (false pour des règles vides : le groupe n'a alors que des membres manuels)</summary>
    public static bool Matches(GroupRules rules, StoredMachine machine)
    {
        if (rules.IsEmpty) return false;
        return rules.Match == MatchAny
            ? rules.Conditions.Any(c => MatchesCondition(c, machine))
            : rules.Conditions.All(c => MatchesCondition(c, machine));
    }

    public static bool MatchesCondition(GroupCondition condition, StoredMachine machine)
    {
        var s = machine.Report.System;
        if (condition.Field == "subnet")
            return machine.LastIp is { } ip && IPAddress.TryParse(ip, out var address) &&
                   SplitSubnets(condition.Value).Any(part => InSubnet(address, part));
        IEnumerable<string?> candidates = condition.Field switch
        {
            "name" => [machine.Name],
            "manufacturer" => [s.Manufacturer],
            "model" => [s.Model, s.FriendlyModel, s.DisplayModel],
            "os" => [$"{s.OsCaption} {s.OsDisplayVersion}".Trim(), s.OsVersion],
            _ => [],
        };
        return candidates.Any(text => !string.IsNullOrWhiteSpace(text) && MatchesText(text.Trim(), condition.Operator, condition.Value));
    }

    private static bool MatchesText(string text, string op, string value) => op switch
    {
        "startsWith" => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
        "contains" => text.Contains(value, StringComparison.OrdinalIgnoreCase),
        "equals" => text.Equals(value, StringComparison.OrdinalIgnoreCase),
        "wildcard" => Wildcard(text, value),
        _ => false,
    };

    /// <summary>Motif « * » (n'importe quelle suite de caractères) et « ? » (un caractère), sur toute la valeur.</summary>
    public static bool Wildcard(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try
        {
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Plusieurs sous-réseaux peuvent être donnés dans une condition, séparés par des virgules ou des espaces.</summary>
    public static IEnumerable<string> SplitSubnets(string value) =>
        value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>« 10.1.20.0/24 », « 10.1.20.7 » (une adresse) ou IPv6 « fd00::/64 ».</summary>
    public static bool TryParseSubnet(string text, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)) return false;
        address = Normalize(address);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 1) prefix = max;
        else if (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max) return false;
        network = address;
        return true;
    }

    public static bool InSubnet(IPAddress address, string subnet)
    {
        if (!TryParseSubnet(subnet, out var network, out var prefix)) return false;
        address = Normalize(address);
        if (address.AddressFamily != network.AddressFamily) return false;
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
            if (a[i] != n[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (n[full] & mask);
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Membres effectifs d'un groupe : ajoutés à la main ou désignés par les règles.</summary>
    public static bool IsMember(GroupRecord group, StoredMachine machine) =>
        group.ManualMembers.Contains(machine.Id) || Matches(group.Rules, machine);

    /// <summary>Groupes de chaque poste (clé interne du poste → identifiants des groupes).</summary>
    public static Dictionary<long, List<long>> GroupsByMachine(IReadOnlyList<GroupRecord> groups, IEnumerable<StoredMachine> machines) =>
        machines.ToDictionary(m => m.Id, m => groups.Where(g => IsMember(g, m)).Select(g => g.Id).ToList());

    /// <summary>Description lisible des règles (« Nom du poste commence par PC-COMPTA ET Constructeur contient Dell »).</summary>
    public static string Describe(GroupRules rules) => rules.IsEmpty ? "" :
        string.Join(rules.Match == MatchAny ? " OU " : " ET ",
            rules.Conditions.Select(c => $"{Fields.GetValueOrDefault(c.Field, c.Field)} {Operators.GetValueOrDefault(c.Operator, c.Operator)} « {c.Value} »"));
}
