using System.Management;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Install;

/// <summary>Paquet de pilote tiers du magasin Windows (%windir%\INF\oemNN.inf + dossier FileRepository).</summary>
public sealed class DriverStorePackage
{
    /// <summary>Nom publié (oemNN.inf) : celui que pnputil utilise.</summary>
    public required string PublishedName { get; init; }
    /// <summary>Nom d'origine de l'INF (ex. nvhda.inf).</summary>
    public string? OriginalName { get; init; }
    /// <summary>Identifiant du paquet = nom du dossier dans FileRepository (ex. nvhda.inf_amd64_0123abcd).</summary>
    public string? PackageId { get; init; }
    public string? FolderPath { get; init; }
    public string? Provider { get; init; }
    public string? ClassName { get; init; }
    public string? ClassGuid { get; init; }
    public string? Version { get; init; }
    public DateTime? Date { get; init; }
    /// <summary>Taille des fichiers du paquet (dossier FileRepository).</summary>
    public long SizeBytes { get; init; }
    /// <summary>ID matériels déclarés par l'INF (toutes architectures).</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();
    /// <summary>L'INF a pu être lu (sinon aucune décision de nettoyage n'est prise).</summary>
    public bool InfParsed { get; init; } = true;
    /// <summary>Paquet utilisé par au moins un périphérique (connecté ou non), y compris comme INF d'extension.</summary>
    public bool InUse { get; init; }
    public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();

    public string SizeLabel => DriverStoreCleaner.FormatSize(SizeBytes);
    public string DisplayName => OriginalName is null ? PublishedName : $"{PublishedName} ({OriginalName})";
    public override string ToString() => $"{DisplayName} {Version} — {Provider}";
}

/// <summary>Décision de nettoyage pour un paquet.</summary>
public enum DriverStoreVerdict
{
    /// <summary>Ancienne version inutilisée, remplacée par une version plus récente présente : peut être supprimée.</summary>
    Candidate,
    /// <summary>Utilisé par un périphérique : jamais supprimé.</summary>
    InUse,
    /// <summary>Seule version de ce pilote dans le magasin.</summary>
    OnlyVersion,
    /// <summary>Version la plus récente de ce pilote (ou à égalité).</summary>
    Newest,
    /// <summary>Conservé pour pouvoir revenir en arrière (version précédente d'une classe sensible, annulation SpokaPilote).</summary>
    KeptForRollback,
    /// <summary>Classe exclue du nettoyage automatique (imprimantes, extensions non vérifiables).</summary>
    SensitiveClass,
    /// <summary>Les versions plus récentes ne prennent pas en charge tout le matériel de celle-ci.</summary>
    NotReplaced,
    /// <summary>Informations insuffisantes (INF illisible, version illisible, nom d'origine inconnu).</summary>
    Undetermined,
}

public sealed class DriverStoreEntry
{
    public required DriverStorePackage Package { get; init; }
    public DriverStoreVerdict Verdict { get; init; }
    /// <summary>Explication en français de la décision.</summary>
    public string Reason { get; init; } = "";
    /// <summary>Version plus récente qui remplace ce paquet (candidats uniquement).</summary>
    public DriverStorePackage? ReplacedBy { get; init; }
    public bool IsCandidate => Verdict == DriverStoreVerdict.Candidate;
}

public sealed class DriverStoreAnalysis
{
    public List<DriverStoreEntry> Entries { get; init; } = new();
    public List<DriverStoreEntry> Candidates => Entries.Where(e => e.IsCandidate).ToList();
    public long ReclaimableBytes => Entries.Where(e => e.IsCandidate).Sum(e => e.Package.SizeBytes);
    public long TotalBytes => Entries.Sum(e => e.Package.SizeBytes);
    /// <summary>false si l'utilisation des paquets d'extension n'a pas pu être vérifiée (Windows ancien) : ils sont alors exclus.</summary>
    public bool ExtensionUsageVerified { get; init; } = true;
    public string Summary =>
        $"{Entries.Count} paquet(s) tiers ({FormatSize(TotalBytes)}), {Entries.Count(e => e.Verdict == DriverStoreVerdict.InUse)} utilisé(s), " +
        $"{Candidates.Count} ancienne(s) version(s) supprimable(s) : {FormatSize(ReclaimableBytes)} libérable(s)";

    private static string FormatSize(long b) => DriverStoreCleaner.FormatSize(b);
}

public sealed class DriverStoreCleanOptions
{
    /// <summary>Simulation : rien n'est supprimé.</summary>
    public bool DryRun { get; init; }
    /// <summary>Copie chaque paquet (pnputil /export-driver) avant suppression : réinstallable avec « spokapilote restore ».</summary>
    public bool Backup { get; init; }
}

public sealed record DriverStoreRemoval(DriverStorePackage Package, bool Success, string Message);

public sealed class DriverStoreCleanResult
{
    public bool DryRun { get; init; }
    public List<DriverStoreRemoval> Removals { get; init; } = new();
    public long FreedBytes => Removals.Where(r => r.Success).Sum(r => r.Package.SizeBytes);
    public string? BackupFolder { get; init; }
    /// <summary>Identifiant de l'intervention enregistrée dans l'historique (null en simulation ou si rien n'a été supprimé).</summary>
    public string? InterventionId { get; init; }
    public bool Success => Removals.All(r => r.Success);
    public string Summary => DryRun
        ? $"Simulation : {Removals.Count} paquet(s) seraient supprimés ({DriverStoreCleaner.FormatSize(Removals.Sum(r => r.Package.SizeBytes))})."
        : $"{Removals.Count(r => r.Success)} paquet(s) supprimé(s), {DriverStoreCleaner.FormatSize(FreedBytes)} libéré(s)" +
          (Removals.Any(r => !r.Success) ? $", {Removals.Count(r => !r.Success)} échec(s)." : ".");
}

/// <summary>
/// Nettoyage du magasin de pilotes : liste des paquets tiers et suppression des anciennes versions INUTILISÉES
/// d'un même pilote (même INF d'origine + même fournisseur), remplacées par une version plus récente présente.
/// <para>Règles de prudence (aucune n'est désactivable) :</para>
/// <list type="bullet">
/// <item>jamais un paquet utilisé par un périphérique, connecté ou non (Win32_PnPSignedDriver, clés de pilote du
///   registre, et « pnputil /enum-drivers /devices » qui voit aussi les INF d'extension) ;</item>
/// <item>jamais la seule version d'un pilote, ni la plus récente ;</item>
/// <item>jamais une version que les plus récentes ne remplacent pas entièrement (ID matériels non couverts) ;</item>
/// <item>imprimantes : jamais (le spouleur utilise les pilotes sans périphérique Plug and Play visible) ;</item>
/// <item>classes sensibles (affichage, réseau, stockage, système, firmware, Bluetooth) : la version précédente est
///   conservée pour pouvoir revenir en arrière (Gestionnaire de périphériques > Restaurer le pilote) ;</item>
/// <item>versions antérieures à un paquet installé par une intervention SpokaPilote encore annulable : conservées
///   (l'annulation en a besoin) ;</item>
/// <item>suppression par « pnputil /delete-driver oemNN.inf » sans /force ni /uninstall : Windows refuse si le
///   paquet est utilisé.</item>
/// </list>
/// </summary>
public static partial class DriverStoreCleaner
{
    /// <summary>Classes jamais nettoyées automatiquement.</summary>
    public static readonly IReadOnlySet<string> NeverCleanClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Printer", "PrintQueue", "{4d36e979-e325-11ce-bfc1-08002be10318}", "{1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc}",
    };

    /// <summary>Classes dont la version précédente est conservée (retour arrière possible).</summary>
    public static readonly IReadOnlySet<string> KeepPreviousClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "{4d36e968-e325-11ce-bfc1-08002be10318}",
        "Net", "{4d36e972-e325-11ce-bfc1-08002be10318}",
        "SCSIAdapter", "{4d36e97b-e325-11ce-bfc1-08002be10318}",
        "HDC", "{4d36e96a-e325-11ce-bfc1-08002be10318}",
        "System", "{4d36e97d-e325-11ce-bfc1-08002be10318}",
        "Firmware", "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}",
        "Bluetooth", "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",
    };

    private const string ExtensionClassGuid = "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}";

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemInf();

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string FileRepository => Path.Combine(WindowsDir, "System32", "DriverStore", "FileRepository");
    internal static string FileRepositoryFolder => FileRepository;

    /// <summary>oemNN.inf → identifiant du paquet (historique des interventions : identité des paquets ajoutés).</summary>
    internal static Dictionary<string, string> ReadPackageIdsForHistory() => ReadPackageIds();

    // ------------------------------------------------------------------ analyse (logique pure, testable)

    /// <summary>
    /// Décide, pour chaque paquet, s'il peut être supprimé. <paramref name="history"/> : interventions SpokaPilote
    /// (les versions antérieures à un paquet encore annulable sont conservées).
    /// </summary>
    public static DriverStoreAnalysis Analyze(IReadOnlyList<DriverStorePackage> packages, IReadOnlyList<Intervention>? history = null,
        bool extensionUsageVerified = true)
    {
        var rollbackAdded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in history ?? Array.Empty<Intervention>())
            if (i.CanRollback)
                foreach (var p in i.AddedDriverPackages) rollbackAdded.TryAdd(p, i.Id);

        var entries = new List<DriverStoreEntry>();
        var groups = packages.GroupBy(p => (Name: (p.OriginalName ?? p.PublishedName).ToLowerInvariant(), Provider: (p.Provider ?? "").Trim().ToLowerInvariant()));
        foreach (var group in groups)
        {
            var list = group.ToList();
            // Référence pour le retour arrière : la version utilisée la plus récente, sinon la plus récente tout court.
            var reference = list.Where(p => p.InUse && DriverVersion.Parse(p.Version) is not null).OrderByDescending(p => p, NewerFirst.Instance).FirstOrDefault()
                            ?? list.Where(p => DriverVersion.Parse(p.Version) is not null).OrderByDescending(p => p, NewerFirst.Instance).FirstOrDefault();
            var previous = reference is null ? null : list
                .Where(p => !p.InUse && IsNewer(reference, p) == true)
                .OrderByDescending(p => p, NewerFirst.Instance).FirstOrDefault();

            foreach (var p in list)
                entries.Add(Decide(p, list, previous, rollbackAdded, extensionUsageVerified));
        }

        return new DriverStoreAnalysis
        {
            Entries = entries
                .OrderBy(e => e.Package.OriginalName ?? e.Package.PublishedName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(e => e.Package, NewerFirst.Instance)
                .ToList(),
            ExtensionUsageVerified = extensionUsageVerified,
        };
    }

    private static DriverStoreEntry Decide(DriverStorePackage p, List<DriverStorePackage> group, DriverStorePackage? previous,
        Dictionary<string, string> rollbackAdded, bool extensionUsageVerified)
    {
        DriverStoreEntry E(DriverStoreVerdict v, string reason, DriverStorePackage? by = null) => new() { Package = p, Verdict = v, Reason = reason, ReplacedBy = by };

        if (p.InUse)
            return E(DriverStoreVerdict.InUse, p.UsedBy.Count == 0 ? "Utilisé par un périphérique." : $"Utilisé par : {string.Join(", ", p.UsedBy.Distinct().Take(3))}{(p.UsedBy.Distinct().Count() > 3 ? "..." : "")}.");
        if (group.Count == 1)
            return E(DriverStoreVerdict.OnlyVersion, "Seule version de ce pilote dans le magasin : conservée.");
        if (p.OriginalName is null)
            return E(DriverStoreVerdict.Undetermined, "Nom d'origine de l'INF inconnu : impossible de le rapprocher de ses autres versions.");
        if (!p.InfParsed)
            return E(DriverStoreVerdict.Undetermined, "INF illisible : conservé par prudence.");
        if (DriverVersion.Parse(p.Version) is null)
            return E(DriverStoreVerdict.Undetermined, $"Version illisible ({p.Version ?? "absente"}) : conservé par prudence.");

        var newer = group.Where(q => !ReferenceEquals(q, p) && IsNewer(q, p) == true).OrderByDescending(q => q, NewerFirst.Instance).ToList();
        if (newer.Count == 0)
        {
            var tie = group.Any(q => !ReferenceEquals(q, p) && IsNewer(q, p) == false && IsNewer(p, q) == false);
            return E(DriverStoreVerdict.Newest, tie
                ? "Même version et même date qu'un autre paquet : aucun des deux n'est supprimé (impossible de dire lequel est remplacé)."
                : "Version la plus récente de ce pilote : conservée.");
        }

        var replacement = newer.FirstOrDefault(q => Covers(q, p));
        if (replacement is null)
            return E(DriverStoreVerdict.NotReplaced,
                $"Les versions plus récentes ({string.Join(", ", newer.Select(q => q.Version).Distinct())}) ne prennent pas en charge tout le matériel de celle-ci : conservée.");

        if (IsInClass(p, NeverCleanClasses))
            return E(DriverStoreVerdict.SensitiveClass,
                "Pilote d'imprimante : le spouleur l'utilise sans périphérique visible, il n'est jamais supprimé automatiquement (à vérifier dans Gestion de l'impression).", replacement);
        if (!extensionUsageVerified && (string.Equals(p.ClassGuid, ExtensionClassGuid, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(p.ClassName, "Extension", StringComparison.OrdinalIgnoreCase)))
            return E(DriverStoreVerdict.SensitiveClass,
                "INF d'extension : son utilisation ne peut pas être vérifiée sur cette version de Windows (pnputil /enum-drivers /devices indisponible) : conservé.", replacement);

        var addedNewer = group.FirstOrDefault(q => rollbackAdded.ContainsKey(q.PublishedName) && IsNewer(q, p) == true);
        if (addedNewer is not null)
            return E(DriverStoreVerdict.KeptForRollback,
                $"Nécessaire pour annuler l'intervention SpokaPilote {rollbackAdded[addedNewer.PublishedName]} (qui a installé {addedNewer.PublishedName} {addedNewer.Version}) : conservé.", replacement);

        if (ReferenceEquals(p, previous) && IsInClass(p, KeepPreviousClasses))
            return E(DriverStoreVerdict.KeptForRollback,
                $"Version précédente d'un pilote sensible ({p.ClassName}) : conservée pour pouvoir revenir en arrière (Gestionnaire de périphériques > Restaurer le pilote).", replacement);

        return E(DriverStoreVerdict.Candidate,
            $"Ancienne version inutilisée, remplacée par {replacement.PublishedName} {replacement.Version}{(replacement.Date is { } d ? $" du {d:dd/MM/yyyy}" : "")}.", replacement);
    }

    private static bool IsInClass(DriverStorePackage p, IReadOnlySet<string> classes) =>
        (p.ClassName is { } n && classes.Contains(n)) || (p.ClassGuid is { } g && classes.Contains(g));

    /// <summary>true si <paramref name="a"/> est plus récent que <paramref name="b"/> (version, puis date) ; null si incomparable.</summary>
    internal static bool? IsNewer(DriverStorePackage a, DriverStorePackage b)
    {
        var va = DriverVersion.Parse(a.Version);
        var vb = DriverVersion.Parse(b.Version);
        if (va is null || vb is null) return null;
        if (va != vb) return va > vb;
        if (a.Date is { } da && b.Date is { } db && da != db) return da > db;
        return false;
    }

    /// <summary>La version <paramref name="newer"/> déclare tous les ID matériels de <paramref name="older"/>.</summary>
    internal static bool Covers(DriverStorePackage newer, DriverStorePackage older)
    {
        if (!newer.InfParsed) return false;
        var set = new HashSet<string>(newer.HardwareIds, StringComparer.OrdinalIgnoreCase);
        return older.HardwareIds.All(set.Contains);
    }

    private sealed class NewerFirst : IComparer<DriverStorePackage>
    {
        public static readonly NewerFirst Instance = new();
        public int Compare(DriverStorePackage? x, DriverStorePackage? y)
        {
            if (x is null || y is null) return 0;
            var vx = DriverVersion.Parse(x.Version) ?? new Version(0, 0);
            var vy = DriverVersion.Parse(y.Version) ?? new Version(0, 0);
            var c = vx.CompareTo(vy);
            return c != 0 ? c : Nullable.Compare(x.Date, y.Date);
        }
    }

    // ------------------------------------------------------------------ inventaire du magasin (ce poste)

    /// <summary>Analyse du magasin de ce poste (lecture seule, ne nécessite pas les droits administrateur).</summary>
    public static DriverStoreAnalysis AnalyzeThisPc(IProgress<string>? progress = null)
    {
        var (packages, verified) = ListPackagesCore(progress);
        List<Intervention> history;
        try { history = InterventionHistory.List(); }
        catch (Exception ex) { Log.Debug($"Historique illisible : {ex.Message}"); history = new(); }
        return Analyze(packages, history, verified);
    }

    /// <summary>Paquets tiers du magasin de pilotes de ce poste.</summary>
    public static List<DriverStorePackage> ListPackages(IProgress<string>? progress = null) => ListPackagesCore(progress).Packages;

    private static (List<DriverStorePackage> Packages, bool ExtensionUsageVerified) ListPackagesCore(IProgress<string>? progress)
    {
        var infDir = Path.Combine(WindowsDir, "INF");
        progress?.Report("Lecture du magasin de pilotes...");
        var packageIds = ReadPackageIds();
        progress?.Report("Recherche des paquets utilisés par les périphériques...");
        var (usage, verified) = CollectUsage();

        var result = new List<DriverStorePackage>();
        string[] files;
        try { files = Directory.GetFiles(infDir, "oem*.inf"); }
        catch (Exception ex)
        {
            Log.Warn($"Lecture de {infDir} impossible : {ex.Message}");
            return (result, verified);
        }

        foreach (var oemPath in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var published = Path.GetFileName(oemPath);
            if (!OemInf().IsMatch(published)) continue;
            try
            {
                packageIds.TryGetValue(published, out var packageId);
                packageId ??= FindPackageByContent(oemPath);
                var folder = packageId is null ? null : Path.Combine(FileRepository, packageId);
                if (folder is not null && !Directory.Exists(folder)) folder = null;
                var original = OriginalNameFromPackageId(packageId);
                var infPath = folder is not null && original is not null && File.Exists(Path.Combine(folder, original)) ? Path.Combine(folder, original) : oemPath;

                InfInfo? info = null;
                try { info = InfParser.Parse(infPath); }
                catch (Exception ex) { Log.Debug($"INF illisible {infPath} : {ex.Message}"); }

                usage.TryGetValue(published, out var usedBy);
                result.Add(new DriverStorePackage
                {
                    PublishedName = published,
                    OriginalName = original,
                    PackageId = packageId,
                    FolderPath = folder,
                    Provider = info?.Provider,
                    ClassName = info?.Class,
                    ClassGuid = info?.ClassGuid,
                    Version = info?.DriverVersion,
                    Date = info?.DriverDate,
                    SizeBytes = folder is not null ? DirectorySize(folder) : new FileInfo(oemPath).Length,
                    HardwareIds = info?.Models.Select(m => m.HardwareId).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
                    InfParsed = info is not null,
                    InUse = usedBy is not null,
                    UsedBy = usedBy ?? new List<string>(),
                });
            }
            catch (Exception ex)
            {
                Log.Debug($"Paquet {published} ignoré : {ex.Message}");
            }
        }
        return (result, verified);
    }

    /// <summary>oemNN.inf → nom d'origine de l'INF (registre DriverDatabase). Vide si illisible.</summary>
    public static Dictionary<string, string> ReadOriginalInfNames() =>
        ReadPackageIds()
            .Select(kv => (kv.Key, Original: OriginalNameFromPackageId(kv.Value)))
            .Where(x => x.Original is not null)
            .ToDictionary(x => x.Key, x => x.Original!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// oemNN.inf → identifiant du paquet actif. La base est répartie entre la ruche DRIVERS
    /// (HKLM\DRIVERS\DriverDatabase, Windows 10 et suivants) et HKLM\SYSTEM\DriverDatabase : les deux sont lues.
    /// </summary>
    private static Dictionary<string, string> ReadPackageIds()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { @"DRIVERS\DriverDatabase\DriverInfFiles", @"SYSTEM\DriverDatabase\DriverInfFiles" })
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames().Where(n => OemInf().IsMatch(n) && !map.ContainsKey(n)))
                {
                    using var key = root.OpenSubKey(name);
                    var active = key?.GetValue("Active") as string ?? (key?.GetValue("") as string[])?.LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(active)) map[name] = active;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Base des pilotes illisible ({path}) : {ex.Message}");
            }
        }
        return map;
    }

    /// <summary>« nvhda.inf_amd64_0123abcd » → « nvhda.inf » (le nom peut contenir des « _ »).</summary>
    internal static string? OriginalNameFromPackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return null;
        var idx = packageId.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? packageId[..(idx + 4)].ToLowerInvariant() : null;
    }

    /// <summary>Repli sans registre : dossier FileRepository contenant un INF identique à oemNN.inf.</summary>
    private static string? FindPackageByContent(string oemPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(oemPath);
            foreach (var dir in Directory.EnumerateDirectories(FileRepository))
                foreach (var inf in Directory.EnumerateFiles(dir, "*.inf"))
                    if (new FileInfo(inf).Length == bytes.Length && File.ReadAllBytes(inf).AsSpan().SequenceEqual(bytes))
                        return Path.GetFileName(dir);
        }
        catch (Exception ex)
        {
            Log.Debug($"Recherche du paquet de {oemPath} impossible : {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Paquets utilisés → périphériques. Trois sources combinées : Win32_PnPSignedDriver (périphériques présents),
    /// clés de pilote du registre (périphériques déconnectés compris) et pnputil /enum-drivers /devices (INF d'extension).
    /// ExtensionUsageVerified = false si pnputil n'a pas pu fournir la liste.
    /// </summary>
    private static (Dictionary<string, List<string>> Usage, bool ExtensionUsageVerified) CollectUsage()
    {
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? inf, string name)
        {
            if (inf is null || !OemInf().IsMatch(inf)) return;
            if (!usage.TryGetValue(inf, out var list)) usage[inf] = list = new List<string>();
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceName, InfName FROM Win32_PnPSignedDriver");
            foreach (var o in searcher.Get())
                using (o) Add(o["InfName"]?.ToString(), o["DeviceName"]?.ToString() ?? "périphérique");
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des pilotes installés impossible : {ex.Message}");
        }

        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            foreach (var guid in classes?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var cls = classes!.OpenSubKey(guid);
                    foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        try
                        {
                            using var drv = cls!.OpenSubKey(sub);
                            if (drv?.GetValue("InfPath") is string inf)
                                Add(inf, $"{drv.GetValue("DriverDesc") as string ?? "périphérique"} (configuration enregistrée)");
                        }
                        catch { /* clé protégée */ }
                    }
                }
                catch { /* classe protégée */ }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Clés de pilotes illisibles : {ex.Message}");
        }

        var verified = ReadPnpUtilDevices(Add);
        return (usage, verified);
    }

    /// <summary>pnputil /enum-drivers /devices /format xml (Windows 11 et Windows 10 récents) : voit aussi les INF d'extension.</summary>
    private static bool ReadPnpUtilDevices(Action<string?, string> add)
    {
        var file = Path.Combine(AppPaths.Cache, $"driverstore-{Environment.ProcessId}.xml");
        try
        {
            var r = ProcessRunner.RunAsync(ProcessRunner.SystemTool("pnputil.exe"),
                $"/enum-drivers /devices /format xml /output-file \"{file}\"", timeout: TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            if (r.ExitCode != 0 || !File.Exists(file)) return false;
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                doc.Load(reader);
            var drivers = doc.SelectNodes("/PnpUtil/Driver");
            if (drivers is null) return false;
            foreach (XmlElement d in drivers)
            {
                var inf = d.GetAttribute("DriverName");
                foreach (XmlElement dev in d.SelectNodes("Devices/Device")?.OfType<XmlElement>() ?? Enumerable.Empty<XmlElement>())
                {
                    var name = dev["DeviceDescription"]?.InnerText ?? dev.GetAttribute("InstanceId");
                    var status = dev["Status"]?.InnerText;
                    add(inf, string.Equals(status, "Disconnected", StringComparison.OrdinalIgnoreCase) ? $"{name} (non connecté)" : name);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"pnputil /enum-drivers /devices indisponible : {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* sans gravité */ }
        }
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex)
        {
            Log.Debug($"Taille de {folder} illisible : {ex.Message}");
            return 0;
        }
    }

    public static string FormatSize(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} Ko" :
        bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.0} Mo" :
        $"{bytes / 1024.0 / 1024 / 1024:0.00} Go";

    // ------------------------------------------------------------------ suppression

    /// <summary>
    /// Supprime les candidats (et eux seuls) : l'utilisation est revérifiée juste avant, la suppression passe par
    /// « pnputil /delete-driver » sans /force, et l'opération est enregistrée dans l'historique des interventions.
    /// </summary>
    public static async Task<DriverStoreCleanResult> CleanAsync(IEnumerable<DriverStoreEntry> entries, DriverStoreCleanOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new DriverStoreCleanOptions();
        var selected = entries.Where(e => e.IsCandidate).Select(e => e.Package).ToList();
        if (options.DryRun || selected.Count == 0)
            return new DriverStoreCleanResult
            {
                DryRun = true,
                Removals = selected.Select(p => new DriverStoreRemoval(p, true, "simulation : rien n'a été supprimé")).ToList(),
            };
        if (!Elevation.IsAdministrator())
            throw new UnauthorizedAccessException("La suppression de paquets du magasin de pilotes nécessite une console administrateur.");

        progress?.Report("Nouvelle vérification des paquets utilisés...");
        var (usage, _) = CollectUsage();

        string? backup = null;
        if (options.Backup) backup = AppPaths.Ensure(Path.Combine(AppPaths.Backups, $"driverstore-{DateTime.Now:yyyyMMdd-HHmmss}"));

        var removals = new List<DriverStoreRemoval>();
        foreach (var p in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (usage.TryGetValue(p.PublishedName, out var users))
            {
                removals.Add(new DriverStoreRemoval(p, false, $"ignoré : désormais utilisé par {string.Join(", ", users.Take(2))}"));
                continue;
            }
            if (backup is not null)
            {
                progress?.Report($"Sauvegarde de {p.DisplayName}...");
                var target = AppPaths.Ensure(Path.Combine(backup, AppPaths.SafeName($"{Path.GetFileNameWithoutExtension(p.PublishedName)}_{p.OriginalName}")));
                var export = await PnpUtil.ExportDriverAsync(p.PublishedName, target, ct).ConfigureAwait(false);
                if (export.ExitCode != 0)
                {
                    removals.Add(new DriverStoreRemoval(p, false, $"non supprimé : sauvegarde impossible (pnputil code {export.ExitCode})"));
                    continue;
                }
            }
            progress?.Report($"Suppression de {p.DisplayName} {p.Version}...");
            var r = await PnpUtil.DeleteUnusedDriverAsync(p.PublishedName, ct).ConfigureAwait(false);
            removals.Add(r.ExitCode is 0 or PnpUtil.ErrorSuccessRebootRequired
                ? new DriverStoreRemoval(p, true, $"supprimé ({p.SizeLabel} libérés){(r.ExitCode == PnpUtil.ErrorSuccessRebootRequired ? ", redémarrage nécessaire" : "")}")
                : new DriverStoreRemoval(p, false, $"refusé par Windows (pnputil code {r.ExitCode}) : paquet probablement encore utilisé"));
            Log.Debug($"pnputil /delete-driver {p.PublishedName} : {r.ExitCode} {r.Combined.Trim()}");
        }

        string? interventionId = null;
        if (removals.Any(r => r.Success))
        {
            var intervention = new Intervention
            {
                Kind = InterventionKinds.DriverStoreClean,
                BackupFolder = backup,
                RemovedDriverPackages = removals.Where(r => r.Success).Select(r => r.Package.PublishedName).ToList(),
                Items = removals.Select(r => new InterventionItem
                {
                    Title = r.Package.DisplayName,
                    Version = r.Package.Version,
                    Source = r.Package.Provider ?? "",
                    Status = r.Success ? "Removed" : "Failed",
                    Message = r.Message,
                }).ToList(),
            };
            InterventionHistory.Save(intervention);
            interventionId = intervention.Id;
        }
        var result = new DriverStoreCleanResult { Removals = removals, BackupFolder = backup, InterventionId = interventionId };
        Log.Info($"Nettoyage du magasin de pilotes : {result.Summary}");
        return result;
    }
}
