using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SpokaPilote.Core.Printers;

/// <summary>
/// Profil d'imprimantes : « configurer une fois, déployer partout ».
/// <para>
/// Format JSON (commentaires // autorisés) :
/// <code>
/// {
///   "version": 1,
///   "name": "Imprimantes du groupe",
///   "removeUnlisted": false,              // retire les imprimantes réseau non listées (prudence : désactivé par défaut)
///   "printers": [                          // imprimantes de tous les postes (sauf condition « when »)
///     { "name": "Copieur RDC", "address": "10.0.1.25", "driverName": "KX DRIVER for Universal Printing",
///       "location": "Rez-de-chaussée", "comment": "Kyocera TASKalfa", "setDefault": true }
///   ],
///   "groups": [                            // imprimantes propres à un site, un service...
///     { "name": "Site de Lyon", "when": { "subnets": [ "10.2.0.0/16" ] },
///       "printers": [ { "name": "Lyon - Accueil", "address": "imp-lyon-01.corp.local", "model": "HP LaserJet M507" } ] },
///     { "name": "Comptabilité", "when": { "computerNames": [ "COMPTA-*" ] },
///       "printers": [ { "connection": "\\\\srv-print\\Compta-Couleur" } ] }
///   ]
/// }
/// </code>
/// Une condition est remplie si TOUS ses critères renseignés le sont, chaque critère étant rempli par AU MOINS une
/// valeur de sa liste (sous-réseau du poste, motif de nom d'ordinateur avec jokers * et ?). Les conditions du groupe et
/// celles de l'imprimante s'additionnent.
/// </para>
/// </summary>
public sealed class PrinterProfile
{
    /// <summary>
    /// Version la plus récente comprise : 2 ajoute les critères Active Directory (adSites, organizationalUnits).
    /// Un profil sans ces critères reste en version 1, lisible par toutes les versions.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Version 1 par défaut (compatibilité) ; 2 si le profil utilise les critères Active Directory.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Version minimale qu'exige le contenu du profil : 2 dès qu'une condition utilise un critère Active Directory.
    /// Une version antérieure ignorerait ce critère (et installerait l'imprimante partout) : elle refuse donc un
    /// profil de version 2, ce qui est le comportement sûr.
    /// </summary>
    [JsonIgnore]
    public int RequiredVersion =>
        AllEntries().Any(x => (x.Group?.When?.UsesDirectory ?? false) || (x.Entry.When?.UsesDirectory ?? false)) ||
        Groups.Any(g => g.When?.UsesDirectory ?? false) ? 2 : 1;
    /// <summary>Nom lisible du profil (affiché dans les rapports).</summary>
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// Retire du poste les imprimantes réseau (port TCP/IP ou IPP) absentes des imprimantes applicables à ce poste.
    /// Les imprimantes virtuelles/locales et les connexions partagées ne sont jamais retirées.
    /// </summary>
    public bool RemoveUnlisted { get; set; }
    /// <summary>Imprimantes communes (éventuellement conditionnées individuellement).</summary>
    public List<PrinterProfileEntry> Printers { get; set; } = new();
    /// <summary>Groupes d'imprimantes partageant une même condition (site, service...).</summary>
    public List<PrinterProfileGroup> Groups { get; set; } = new();

    /// <summary>Renseigné lors d'un export : poste d'origine et date.</summary>
    public string? ExportedFrom { get; set; }
    public DateTime? ExportedAt { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PrinterProfile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PrinterProfile>(json, JsonOptions)
                   ?? throw new FormatException("Profil d'imprimantes vide.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Profil d'imprimantes illisible (JSON invalide{(ex.LineNumber is { } l ? $", ligne {l + 1}" : "")}) : {ex.Message}", ex);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Toutes les imprimantes du profil, avec la condition de leur groupe.</summary>
    public IEnumerable<(PrinterProfileEntry Entry, PrinterProfileGroup? Group)> AllEntries() =>
        Printers.Select(p => (p, (PrinterProfileGroup?)null))
            .Concat(Groups.SelectMany(g => (g.Printers ?? new()).Select(p => (p, (PrinterProfileGroup?)g))));

    /// <summary>Imprimantes à installer sur ce poste (conditions du groupe ET de l'imprimante remplies).</summary>
    public List<PrinterProfileEntry> ApplicableEntries(PrinterProfileContext context) =>
        AllEntries()
            .Where(x => (x.Group?.When?.Matches(context) ?? true) && (x.Entry.When?.Matches(context) ?? true))
            .Select(x => x.Entry)
            .ToList();

    /// <summary>Vérifie le profil : erreurs bloquantes et avertissements.</summary>
    public PrinterProfileValidation Validate()
    {
        var v = new PrinterProfileValidation();
        if (Version is < 1 or > CurrentVersion)
            v.Errors.Add($"Version de profil {Version} non prise en charge (attendue : {CurrentVersion}).");
        else if (Version < RequiredVersion)
            v.Errors.Add($"Les critères Active Directory (adSites, organizationalUnits) exigent « \"version\": {RequiredVersion} » : " +
                         "une version antérieure les ignorerait et installerait l'imprimante sur tous les postes.");

        foreach (var (g, i) in Groups.Select((g, i) => (g, i)))
        {
            var label = $"groupe « {g.Name ?? $"n° {i + 1}"} »";
            if (g.Printers is null || g.Printers.Count == 0) v.Warnings.Add($"{label} : aucune imprimante.");
            g.When?.Validate(label, v);
        }

        var all = AllEntries().ToList();
        if (all.Count == 0) v.Warnings.Add("Le profil ne contient aucune imprimante.");
        foreach (var (entry, group) in all)
        {
            var label = $"imprimante « {entry.EffectiveName ?? "(sans nom)"} »{(group is null ? "" : $" ({group.Name})")}";
            entry.Validate(label, v);
        }

        // Un même nom ne peut désigner deux imprimantes que si leurs conditions s'excluent (sites différents) :
        // impossible à prouver, d'où un simple avertissement sauf s'il n'y a aucune condition.
        foreach (var dup in all.Where(x => x.Entry.EffectiveName is not null)
                     .GroupBy(x => x.Entry.EffectiveName!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var unconditional = dup.Count(x => x.Group?.When is null && x.Entry.When is null);
            if (unconditional > 1) v.Errors.Add($"Nom « {dup.Key} » utilisé plusieurs fois sans condition.");
            else v.Warnings.Add($"Nom « {dup.Key} » présent plusieurs fois : vérifiez que les conditions s'excluent.");
        }

        if (all.Count(x => x.Entry.SetDefault && x.Group?.When is null && x.Entry.When is null) > 1)
            v.Warnings.Add("Plusieurs imprimantes sans condition sont marquées « setDefault » : la dernière l'emportera.");
        return v;
    }
}

public sealed class PrinterProfileGroup
{
    public string? Name { get; set; }
    public PrinterProfileCondition? When { get; set; }
    public List<PrinterProfileEntry> Printers { get; set; } = new();
}

public sealed class PrinterProfileEntry
{
    /// <summary>Nom de la file d'impression Windows (facultatif pour une connexion partagée).</summary>
    public string? Name { get; set; }
    /// <summary>Adresse IP ou nom DNS de l'imprimante (imprimante réseau directe).</summary>
    public string? Address { get; set; }
    /// <summary>
    /// Connexion partagée d'un serveur d'impression (\\serveur\file), exclusive de <see cref="Address"/>.
    /// Ajoutée pour l'utilisateur qui exécute le déploiement (Add-Printer -ConnectionName).
    /// </summary>
    public string? Connection { get; set; }
    /// <summary>Modèle (ex. « KYOCERA TASKalfa 3253ci ») : aide à choisir le pilote si l'imprimante ne répond pas.</summary>
    public string? Model { get; set; }
    /// <summary>Pilote préféré (nom exact Windows). Absent ou indisponible : choix automatique.</summary>
    public string? DriverName { get; set; }
    public string? Location { get; set; }
    public string? Comment { get; set; }
    /// <summary>Définit l'imprimante par défaut (de l'utilisateur qui exécute le déploiement).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SetDefault { get; set; }
    /// <summary>true = partagée, false = non partagée, absent = laissé tel quel.</summary>
    public bool? Shared { get; set; }
    /// <summary>Utilise exclusivement le pilote IPP de Windows (aucun téléchargement).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IppOnly { get; set; }
    /// <summary>URL IPP explicite (par défaut http://adresse:631/ipp/print).</summary>
    public string? IppUrl { get; set; }
    /// <summary>« raw » (9100), « lpr » ou « ipp ». Absent = automatique (raw, ou IPP si c'est le seul pilote trouvé).</summary>
    public string? Protocol { get; set; }
    /// <summary>Port TCP du protocole RAW (9100 par défaut).</summary>
    public int? PortNumber { get; set; }
    /// <summary>Nom de la file LPR (« lp » par défaut).</summary>
    public string? LprQueue { get; set; }
    public PrinterProfileCondition? When { get; set; }

    [JsonIgnore] public bool IsConnection => !string.IsNullOrWhiteSpace(Connection);
    [JsonIgnore] public string? EffectiveName => IsConnection ? Connection!.Trim() : string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
    [JsonIgnore] public string EffectiveProtocol => IppOnly ? "ipp" : Protocol?.Trim().ToLowerInvariant() ?? "raw";
    [JsonIgnore] public int EffectivePortNumber => PortNumber ?? 9100;
    [JsonIgnore] public string EffectiveLprQueue => string.IsNullOrWhiteSpace(LprQueue) ? "lp" : LprQueue.Trim();
    [JsonIgnore] public string EffectiveIppUrl => !string.IsNullOrWhiteSpace(IppUrl) ? IppUrl.Trim() : $"http://{FormatHost(Address?.Trim() ?? "")}:631/ipp/print";

    private static string FormatHost(string host) => IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    internal void Validate(string label, PrinterProfileValidation v)
    {
        var hasAddress = !string.IsNullOrWhiteSpace(Address);
        if (hasAddress == IsConnection)
        {
            v.Errors.Add($"{label} : indiquez soit « address » (IP ou nom DNS), soit « connection » (\\\\serveur\\file).");
            return;
        }
        if (IsConnection)
        {
            if (!PrinterProfileRules.IsValidConnection(Connection!))
                v.Errors.Add($"{label} : connexion « {Connection} » invalide (format attendu : \\\\serveur\\file).");
            if (!string.IsNullOrWhiteSpace(Name) && !Name.Trim().Equals(Connection!.Trim(), StringComparison.OrdinalIgnoreCase))
                v.Warnings.Add($"{label} : le nom d'une connexion partagée est imposé par le serveur (« name » ignoré).");
            if (DriverName is not null || IppOnly || Protocol is not null || Shared is not null)
                v.Warnings.Add($"{label} : pilote, protocole et partage d'une connexion sont gérés par le serveur d'impression (ignorés).");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Name)) v.Errors.Add($"{label} : « name » obligatoire.");
            else if (!PrinterProfileRules.IsValidPrinterName(Name)) v.Errors.Add($"{label} : nom invalide (220 caractères maximum, ni « \\ » ni « , »).");
            if (!PrinterProfileRules.IsValidHost(Address!)) v.Errors.Add($"{label} : adresse « {Address} » invalide (IP ou nom DNS attendu).");
            if (Protocol is not null && EffectiveProtocol is not ("raw" or "lpr" or "ipp"))
                v.Errors.Add($"{label} : protocole « {Protocol} » inconnu (raw, lpr ou ipp).");
            if (IppOnly && Protocol is not null && Protocol.Trim().ToLowerInvariant() != "ipp")
                v.Errors.Add($"{label} : « ippOnly » est incompatible avec le protocole « {Protocol} ».");
            if (PortNumber is < 1 or > 65535) v.Errors.Add($"{label} : port {PortNumber} invalide.");
            if (LprQueue is not null && !Regex.IsMatch(LprQueue.Trim(), @"^[\w.\-]{1,64}$"))
                v.Errors.Add($"{label} : nom de file LPR « {LprQueue} » invalide.");
            if (IppUrl is not null && (!Uri.TryCreate(IppUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "ipp" or "ipps")))
                v.Errors.Add($"{label} : URL IPP « {IppUrl} » invalide.");
            if (DriverName is not null && (DriverName.Trim().Length == 0 || DriverName.Contains(',')))
                v.Errors.Add($"{label} : nom de pilote « {DriverName} » invalide.");
        }
        if (Location?.Length > 255 || Comment?.Length > 255) v.Errors.Add($"{label} : emplacement et commentaire limités à 255 caractères.");
        When?.Validate(label, v);
    }
}

/// <summary>Conditions d'application : tous les critères renseignés doivent être remplis.</summary>
public sealed class PrinterProfileCondition
{
    /// <summary>Sous-réseaux CIDR (ex. 10.1.0.0/16) : le poste doit avoir une adresse dans l'un d'eux.</summary>
    public List<string>? Subnets { get; set; }
    /// <summary>Motifs de nom d'ordinateur avec jokers * et ? (ex. « COMPTA-* ») : l'un d'eux doit correspondre.</summary>
    public List<string>? ComputerNames { get; set; }
    /// <summary>Motifs de nom d'ordinateur exclus (prioritaires).</summary>
    public List<string>? ExcludeComputerNames { get; set; }
    /// <summary>
    /// Sites Active Directory (jokers * et ?, ex. « PARIS-* ») : le site du poste doit correspondre à l'un d'eux.
    /// Profil de version 2. Site inconnu (poste hors domaine) : critère non rempli.
    /// </summary>
    public List<string>? AdSites { get; set; }
    /// <summary>
    /// Unités d'organisation (« OU=Compta,OU=Paris,DC=corp,DC=local » ou simplement « OU=Paris ») : le nom distinctif
    /// du compte ordinateur doit les contenir. Profil de version 2. Nom inconnu : critère non rempli.
    /// </summary>
    public List<string>? OrganizationalUnits { get; set; }

    /// <summary>Utilise un critère Active Directory (profil de version 2).</summary>
    [JsonIgnore]
    public bool UsesDirectory => AdSites is { Count: > 0 } || OrganizationalUnits is { Count: > 0 };

    public bool Matches(PrinterProfileContext context)
    {
        if (ExcludeComputerNames is { Count: > 0 } && ExcludeComputerNames.Any(p => PrinterProfileRules.WildcardMatch(context.ComputerName, p)))
            return false;
        if (ComputerNames is { Count: > 0 } && !ComputerNames.Any(p => PrinterProfileRules.WildcardMatch(context.ComputerName, p)))
            return false;
        if (Subnets is { Count: > 0 } && !Subnets.Any(s => context.Addresses.Any(a => PrinterProfileRules.CidrContains(s, a))))
            return false;
        if (AdSites is { Count: > 0 } && (context.AdSite is not { } site || !AdSites.Any(p => PrinterProfileRules.WildcardMatch(site, p))))
            return false;
        if (OrganizationalUnits is { Count: > 0 } &&
            (context.ComputerDistinguishedName is not { } dn || !OrganizationalUnits.Any(ou => PrinterProfileRules.DistinguishedNameContains(dn, ou))))
            return false;
        return true;
    }

    internal void Validate(string label, PrinterProfileValidation v)
    {
        foreach (var s in Subnets ?? new())
            if (!PrinterProfileRules.TryParseCidr(s, out _, out _))
                v.Errors.Add($"{label} : sous-réseau « {s} » invalide (format attendu : 10.1.0.0/16).");
        foreach (var p in (ComputerNames ?? new()).Concat(ExcludeComputerNames ?? new()))
            if (!Regex.IsMatch(p ?? "", @"^[A-Za-z0-9\-_.*?]{1,63}$"))
                v.Errors.Add($"{label} : motif de nom d'ordinateur « {p} » invalide (lettres, chiffres, - _ . et jokers * ?).");
        foreach (var p in AdSites ?? new())
            if (!Regex.IsMatch(p ?? "", @"^[A-Za-z0-9\-_.*?]{1,63}$"))
                v.Errors.Add($"{label} : site Active Directory « {p} » invalide (lettres, chiffres, - _ . et jokers * ?).");
        foreach (var ou in OrganizationalUnits ?? new())
            if (!PrinterProfileRules.IsValidDistinguishedNameFilter(ou))
                v.Errors.Add($"{label} : unité d'organisation « {ou} » invalide (format attendu : OU=Paris,DC=corp,DC=local).");
        if (Subnets is not { Count: > 0 } && ComputerNames is not { Count: > 0 } && ExcludeComputerNames is not { Count: > 0 } && !UsesDirectory)
            v.Warnings.Add($"{label} : condition « when » vide (toujours remplie).");
    }
}

public sealed class PrinterProfileValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Caractéristiques du poste utilisées pour évaluer les conditions.</summary>
public sealed class PrinterProfileContext
{
    public required string ComputerName { get; init; }
    public required IReadOnlyList<IPAddress> Addresses { get; init; }
    /// <summary>Site Active Directory du poste (null hors domaine ou inconnu).</summary>
    public string? AdSite { get; init; }
    /// <summary>Nom distinctif du compte ordinateur (« CN=PC01,OU=Compta,DC=corp,DC=local »), null s'il est inconnu.</summary>
    public string? ComputerDistinguishedName { get; init; }

    public static PrinterProfileContext Current()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                addresses.AddRange(nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => !IPAddress.IsLoopback(a)));
            }
        }
        catch (NetworkInformationException)
        {
            // pas d'information réseau : seules les conditions sur le nom d'ordinateur peuvent être remplies
        }
        var (site, dn) = ReadDirectoryInfo();
        return new PrinterProfileContext
        {
            ComputerName = Environment.MachineName,
            Addresses = addresses.Distinct().ToList(),
            AdSite = site,
            ComputerDistinguishedName = dn,
        };
    }

    /// <summary>
    /// Site AD et nom distinctif du poste, lus dans le registre (aucune requête réseau, fonctionne hors connexion) :
    /// site mémorisé par le service Netlogon (SiteName imposé, sinon DynamicSiteName), nom distinctif mémorisé par le
    /// dernier traitement des stratégies de groupe de l'ordinateur. Valeurs absentes sur un poste hors domaine.
    /// </summary>
    public static (string? Site, string? DistinguishedName) ReadDirectoryInfo()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default);
            using var netlogon = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Netlogon\Parameters");
            var site = Clean(netlogon?.GetValue("SiteName") as string) ?? Clean(netlogon?.GetValue("DynamicSiteName") as string);
            using var gp = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine");
            var dn = Clean(gp?.GetValue("Distinguished-Name") as string);
            return (site, dn);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim('\0', ' ');
    }

    public override string ToString() =>
        $"{ComputerName} ({string.Join(", ", Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))})" +
        (AdSite is null ? "" : $" · site AD {AdSite}");
}

/// <summary>Règles de validation et d'évaluation (pures, testables).</summary>
public static class PrinterProfileRules
{
    public static bool WildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool TryParseCidr(string? cidr, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 1) prefix = max;
        else if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, null, out prefix) || prefix > max) return false;
        network = ip;
        return true;
    }

    public static bool CidrContains(string cidr, IPAddress address)
    {
        if (!TryParseCidr(cidr, out var network, out var prefix)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily) return false;
        var a = network.GetAddressBytes();
        var b = address.GetAddressBytes();
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (a[i] != b[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    private static readonly Regex HostRegex = new(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant);

    public static bool IsValidHost(string host)
    {
        host = host.Trim();
        return IPAddress.TryParse(host, out _) ? !host.Contains('%') : HostRegex.IsMatch(host);
    }

    public static bool IsValidPrinterName(string name) =>
        name.Trim().Length is > 0 and <= 220 && !name.Contains('\\') && !name.Contains(',') && !name.Any(char.IsControl);

    /// <summary>Filtre d'unité d'organisation : composants OU=, DC= ou CN= séparés par des virgules.</summary>
    public static bool IsValidDistinguishedNameFilter(string? filter) =>
        !string.IsNullOrWhiteSpace(filter) && filter.Length <= 1024 &&
        Regex.IsMatch(filter.Trim(), @"^\s*(OU|DC|CN)\s*=\s*[^,=]+(\s*,\s*(OU|DC|CN)\s*=\s*[^,=]+)*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Le nom distinctif contient-il la suite de composants du filtre (« OU=Paris » ou « OU=Compta,OU=Paris,DC=corp,DC=local ») ?
    /// Comparaison sans tenir compte de la casse ni des espaces autour de « = » et « , ».
    /// </summary>
    public static bool DistinguishedNameContains(string distinguishedName, string filter)
    {
        static string Norm(string s) => "," + string.Join(",", s.Split(',').Select(p => Regex.Replace(p.Trim(), @"\s*=\s*", "="))) + ",";
        if (string.IsNullOrWhiteSpace(distinguishedName) || !IsValidDistinguishedNameFilter(filter)) return false;
        return Norm(distinguishedName).Contains(Norm(filter), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidConnection(string connection)
    {
        var m = Regex.Match(connection.Trim(), @"^\\\\(?<host>[^\\/]+)\\(?<queue>[^\\]+)$");
        return m.Success && IsValidHost(m.Groups["host"].Value) && IsValidPrinterName(m.Groups["queue"].Value);
    }
}
