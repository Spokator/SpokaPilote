using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Repository;

namespace SpokaPilote.Core.Install;

/// <summary>Une image d'un fichier WIM (sortie de dism /Get-WimInfo).</summary>
public sealed record WimImageInfo(
    int Index,
    string Name,
    string? Description,
    long? SizeBytes,
    string? Architecture,
    Version? Version,
    string? Edition,
    string? Installation,
    IReadOnlyList<string> Languages)
{
    public string ArchitectureLabel => Architecture switch { "amd64" => "x64", "arm64" => "ARM64", null => "?", _ => Architecture };
}

/// <summary>Pilote tiers présent dans une image (sortie de dism /Get-Drivers).</summary>
public sealed record ImageDriver(string PublishedName, string OriginalFileName, bool Inbox, string? ClassName, string? Provider, string? Date, string? Version);

/// <summary>Verdict du contrôle préalable d'un INF du dépôt.</summary>
public sealed record InfCheck(string Path, bool Accepted, string Reason, string? Provider = null, string? Class = null, string? Version = null);

/// <summary>Résultat DISM pour un INF (« Installing 1 of 3 - C:\...\a.inf: The driver package was successfully installed. »).</summary>
public sealed record DismDriverResult(string InfPath, bool Success, string Message);

public sealed class InjectionReport
{
    public string Target { get; set; } = "";
    public string? Architecture { get; set; }
    public Version? OsVersion { get; set; }
    public List<string> Repositories { get; set; } = new();
    public List<InfCheck> Checks { get; set; } = new();
    public List<DismDriverResult> Results { get; set; } = new();
    public int DriversInImage { get; set; }
    public bool Committed { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? DismLog { get; set; }
    public string? ReportFile { get; set; }
    public TimeSpan Duration { get; set; }

    public IEnumerable<InfCheck> Accepted => Checks.Where(c => c.Accepted);
    public IEnumerable<InfCheck> Refused => Checks.Where(c => !c.Accepted);
    public int InjectedCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => !r.Success);
}

/// <summary>
/// Injection de pilotes dans une image Windows avec DISM.exe (outil Windows intégré), pour la masterisation et le
/// déploiement : (a) image WIM (install.wim, boot.wim, winpe.wim) montée, enrichie puis enregistrée ;
/// (b) Windows hors ligne (disque d'un autre poste, VHD monté) via /Image:.
/// Avant toute injection, chaque INF du dépôt est contrôlé : catalogue signé valide et section de modèles applicable
/// à l'architecture et à la version de l'image. Seuls les INF acceptés sont transmis à DISM.
/// DISM est lancé avec /English : sorties non localisées (les analyseurs comprennent aussi le français).
/// </summary>
public static class ImageInjector
{
    private static string Dism => ProcessRunner.SystemTool("dism.exe");

    // ------------------------------------------------------------------ arguments DISM (testables)

    internal static string Quote(string path) => "\"" + Path.TrimEndingDirectorySeparator(path) + "\"";

    /// <summary>Racine d'un Windows hors ligne : « D: » ou « D:\ » doit rester « D:\ » pour DISM.</summary>
    internal static string ImageRoot(string path)
    {
        var full = Path.GetFullPath(path.Length == 2 && path[1] == ':' ? path + "\\" : path);
        return full.Length == 3 ? full : Path.TrimEndingDirectorySeparator(full);
    }

    internal static string WimInfoArgs(string wim, int? index = null) =>
        $"/English /Get-WimInfo /WimFile:{Quote(wim)}{(index is null ? "" : $" /Index:{index}")}";

    internal static string MountArgs(string wim, int index, string mountDir) =>
        $"/English /Mount-Image /ImageFile:{Quote(wim)} /Index:{index} /MountDir:{Quote(mountDir)}";

    internal static string UnmountArgs(string mountDir, bool commit) =>
        $"/English /Unmount-Image /MountDir:{Quote(mountDir)} {(commit ? "/Commit" : "/Discard")}";

    /// <summary>Syntaxe documentée : un seul /Driver: (dossier ou INF) par appel, /Recurse facultatif.</summary>
    internal static string AddDriverArgs(string imageRoot, string driver, bool recurse, string? logPath)
    {
        var sb = new StringBuilder($"/English /Image:{ImageArg(imageRoot)}");
        if (logPath is not null) sb.Append($" /LogPath:{Quote(logPath)}");
        sb.Append($" /Add-Driver /Driver:{Quote(driver)}");
        if (recurse) sb.Append(" /Recurse");
        return sb.ToString();
    }

    internal static string GetDriversArgs(string imageRoot) => $"/English /Image:{ImageArg(imageRoot)} /Get-Drivers";

    /// <summary>« D:\ » est passé tel quel (un guillemet après une barre oblique inverse serait mal interprété).</summary>
    private static string ImageArg(string root)
    {
        root = ImageRoot(root);
        return root.Length == 3 ? root : Quote(root);
    }

    /// <summary>
    /// Appels DISM nécessaires pour n'ajouter QUE les INF acceptés, avec le moins d'appels possible :
    /// un sous-dossier entièrement accepté → /Driver:&lt;dossier&gt; /Recurse ; un dossier dont tous les INF directs sont
    /// acceptés → /Driver:&lt;dossier&gt; (sans /Recurse) ; sinon un appel par INF accepté.
    /// </summary>
    internal static List<(string Driver, bool Recurse)> PlanAddDriverCalls(string root, IReadOnlyList<InfCheck> checks)
    {
        var calls = new List<(string, bool)>();
        var normalized = checks.Select(c => (Check: c, Path: Path.GetFullPath(c.Path))).ToList();
        void Plan(string dir)
        {
            var prefix = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
            var under = normalized.Where(c => c.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (under.Count == 0 || !under.Any(c => c.Check.Accepted)) return;
            if (under.All(c => c.Check.Accepted))
            {
                calls.Add((Path.TrimEndingDirectorySeparator(dir), true));
                return;
            }
            var own = under.Where(c => string.Equals(Path.GetDirectoryName(c.Path), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count > 0 && own.All(c => c.Check.Accepted)) calls.Add((Path.TrimEndingDirectorySeparator(dir), false));
            else calls.AddRange(own.Where(c => c.Check.Accepted).Select(c => (c.Check.Path, false)));

            var subdirs = under.Except(own)
                .Select(c => c.Path[prefix.Length..].Split(Path.DirectorySeparatorChar)[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subdirs) Plan(Path.Combine(dir, sub));
        }
        Plan(Path.GetFullPath(root));
        return calls;
    }

    // ------------------------------------------------------------------ analyse des sorties DISM (EN et FR)

    /// <summary>Clé normalisée : lettres ASCII uniquement, en minuscules (« Nom publié » → « nompubli »), insensible aux accents mal décodés.</summary>
    internal static string NormalizeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static IEnumerable<(string Key, string Value, string Raw)> KeyValues(string output)
    {
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var idx = raw.IndexOf(':');
            if (idx <= 0) { yield return ("", raw.Trim(), raw); continue; }
            yield return (NormalizeKey(raw[..idx]), raw[(idx + 1)..].Trim(), raw);
        }
    }

    internal static List<WimImageInfo> ParseWimInfo(string output)
    {
        var images = new List<WimImageInfo>();
        Dictionary<string, string>? current = null;
        List<string>? languages = null;
        var inLanguages = false;

        void Flush()
        {
            if (current is null) return;
            images.Add(Build(current, languages ?? new List<string>()));
        }

        foreach (var (key, value, raw) in KeyValues(output))
        {
            if (key == "index")
            {
                Flush();
                current = new Dictionary<string, string> { ["index"] = value };
                languages = new List<string>();
                inLanguages = false;
                continue;
            }
            if (current is null) continue;
            if (inLanguages)
            {
                if (raw.Length > 0 && char.IsWhiteSpace(raw[0]) && raw.Trim().Length > 0)
                {
                    if (!raw.Trim().StartsWith('<')) languages!.Add(raw.Trim());
                    continue;
                }
                inLanguages = false;
            }
            if (key is "languages" or "langues")
            {
                inLanguages = true;
                if (value.Length > 0 && !value.StartsWith('<')) languages!.Add(value);
                continue;
            }
            if (key.Length > 0 && !current.ContainsKey(key)) current[key] = value;
        }
        Flush();
        return images;
    }

    private static WimImageInfo Build(Dictionary<string, string> v, List<string> languages)
    {
        // « <undefined> » / « <non défini> » : valeur absente (image qui n'est pas un Windows, ex. dossier capturé).
        string? Get(params string[] keys) => keys.Select(k => v.TryGetValue(k, out var s) && s.Length > 0 && !s.StartsWith('<') ? s : null).FirstOrDefault(s => s is not null);

        Version? version = null;
        if (Version.TryParse(Get("version"), out var parsed))
        {
            var spBuild = int.TryParse(Get("servicepackbuild", "builddudservicepack", "buildduservicepack"), out var b) ? b : -1;
            version = spBuild >= 0 && parsed.Build >= 0 && parsed.Revision < 0 ? new Version(parsed.Major, parsed.Minor, parsed.Build, spBuild) : parsed;
        }
        var sizeDigits = new string((Get("size", "taille") ?? "").Where(char.IsDigit).ToArray());
        return new WimImageInfo(
            Index: int.TryParse(Get("index"), out var index) ? index : 0,
            Name: Get("name", "nom") ?? "",
            Description: Get("description"),
            SizeBytes: long.TryParse(sizeDigits, out var size) ? size : null,
            Architecture: NormalizeArchitecture(Get("architecture")),
            Version: version,
            Edition: Get("edition", "dition"),
            Installation: Get("installation"),
            Languages: languages);
    }

    /// <summary>Architecture DISM (x64, x86, ARM64) → nomenclature des INF (amd64, x86, arm64).</summary>
    internal static string? NormalizeArchitecture(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "x64" or "amd64" => "amd64",
        "x86" => "x86",
        "arm64" => "arm64",
        "arm" => "arm",
        var other => other,
    };

    internal static List<ImageDriver> ParseDrivers(string output)
    {
        var list = new List<ImageDriver>();
        Dictionary<string, string>? current = null;

        void Flush()
        {
            if (current is null) return;
            string? Get(params string[] keys) => keys.Select(k => current.TryGetValue(k, out var s) ? s : null).FirstOrDefault(s => s is not null);
            var inbox = (Get("inbox", "botederception") ?? "").Trim().ToLowerInvariant();
            list.Add(new ImageDriver(
                Get("publishedname", "nompubli") ?? "",
                Get("originalfilename", "nomdufichierdorigine", "nomdefichierdorigine") ?? "",
                inbox is "yes" or "oui",
                Get("classname", "nomdelaclasse", "nomdeclasse"),
                Get("providername", "nomdufournisseur"),
                Get("date"),
                Get("version")));
        }

        var started = false;
        foreach (var (key, value, _) in KeyValues(output))
        {
            if (key is "publishedname" or "nompubli")
            {
                Flush();
                current = new Dictionary<string, string> { [key] = value };
                started = true;
                continue;
            }
            if (started && current is not null && key.Length > 0) current[key] = value;
        }
        Flush();
        return list;
    }

    private static readonly Regex AddDriverLine = new(
        @"^\s*(?:Installing|Installation(?:\s+de)?)\s+(\d+)\s+(?:of|sur)\s+(\d+)\s+-\s+(.+?\.inf)\s*:\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Lignes « Installing n of m - &lt;inf&gt;: &lt;message&gt; » (ou « Installation de n sur m - ... »).</summary>
    internal static List<DismDriverResult> ParseAddDriver(string output)
    {
        var results = new List<DismDriverResult>();
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var m = AddDriverLine.Match(line);
            if (!m.Success) continue;
            var message = m.Groups[4].Value.Trim();
            var lower = message.ToLowerInvariant();
            var ok = !lower.Contains("error") && !lower.Contains("erreur") &&
                     (lower.Contains("success") || lower.Contains("réussi") || lower.Contains("a été installé") || lower.Contains("installed"));
            results.Add(new DismDriverResult(m.Groups[3].Value.Trim(), ok, message));
        }
        return results;
    }

    /// <summary>Code d'erreur DISM lisible (« Error: 0x800f081f » / « Erreur : 87 ») et message qui suit.</summary>
    public static string DescribeFailure(ProcessResult result)
    {
        var lines = result.Combined.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var idx = lines.FindIndex(l => Regex.IsMatch(l, @"^(Error|Erreur)\s*:", RegexOptions.IgnoreCase));
        if (idx >= 0)
        {
            var detail = lines.Skip(idx + 1).FirstOrDefault(l => !l.StartsWith("The DISM log", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Le fichier journal", StringComparison.OrdinalIgnoreCase));
            return $"{lines[idx]}{(detail is null ? "" : " — " + detail)}";
        }
        if (result.TimedOut) return "délai dépassé";
        return $"code {result.ExitCode}";
    }

    // ------------------------------------------------------------------ informations

    public static async Task<List<WimImageInfo>> GetWimInfoAsync(string wim, CancellationToken ct = default)
    {
        EnsureAdministrator();
        EnsureWimFile(wim, forWrite: false);
        var result = await RunDismAsync(WimInfoArgs(wim), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Lecture de {Path.GetFileName(wim)} impossible : {DescribeFailure(result)}");
        return ParseWimInfo(result.StdOut);
    }

    public static async Task<WimImageInfo> GetWimIndexInfoAsync(string wim, int index, CancellationToken ct = default)
    {
        EnsureAdministrator();
        EnsureWimFile(wim, forWrite: false);
        var result = await RunDismAsync(WimInfoArgs(wim, index), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Index {index} de {Path.GetFileName(wim)} illisible : {DescribeFailure(result)}");
        return ParseWimInfo(result.StdOut).FirstOrDefault(i => i.Index == index)
               ?? throw new InvalidOperationException($"Index {index} introuvable dans {Path.GetFileName(wim)}.");
    }

    public static async Task<List<ImageDriver>> GetImageDriversAsync(string imageRoot, CancellationToken ct = default)
    {
        EnsureAdministrator();
        var result = await RunDismAsync(GetDriversArgs(imageRoot), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Liste des pilotes de l'image impossible : {DescribeFailure(result)}");
        return ParseDrivers(result.StdOut);
    }

    /// <summary>Sortie brute de /Get-MountedWimInfo (images montées sur ce poste).</summary>
    public static async Task<string> GetMountedImagesAsync(CancellationToken ct = default)
    {
        EnsureAdministrator();
        var result = await RunDismAsync("/English /Get-MountedWimInfo", TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return result.StdOut;
    }

    internal static bool HasMountedImages(string mountedOutput) =>
        Regex.IsMatch(mountedOutput, @"^\s*Mount Dir\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>Nettoie les points de montage orphelins (après un plantage ou une coupure).</summary>
    public static async Task<ProcessResult> CleanupMountPointsAsync(CancellationToken ct = default)
    {
        EnsureAdministrator();
        return await RunDismAsync("/English /Cleanup-Mountpoints", TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Architecture et version d'un Windows hors ligne, lues dans son noyau (en-tête PE et version de fichier de
    /// ntoskrnl.exe) : aucune dépendance à la langue de DISM.
    /// </summary>
    public static (string Architecture, Version Version)? ReadOfflineWindows(string root)
    {
        var kernel = Path.Combine(ImageRoot(root), "Windows", "System32", "ntoskrnl.exe");
        if (!File.Exists(kernel)) return null;
        var arch = ReadPeArchitecture(kernel);
        var fvi = FileVersionInfo.GetVersionInfo(kernel);
        var version = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
        return arch is null ? null : (arch, version);
    }

    internal static string? ReadPeArchitecture(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return null; // « MZ »
        fs.Position = 0x3C;
        var peOffset = br.ReadInt32();
        if (peOffset <= 0 || peOffset + 6 > fs.Length) return null;
        fs.Position = peOffset;
        if (br.ReadUInt32() != 0x00004550) return null; // « PE\0\0 »
        return br.ReadUInt16() switch
        {
            0x8664 => "amd64",
            0xAA64 => "arm64",
            0x014C => "x86",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ contrôle préalable

    /// <summary>Chaque INF du dépôt : lisible, déclare des périphériques pour cette architecture / version, catalogue signé valide.</summary>
    public static List<InfCheck> VerifyRepository(string repository, string architecture, Version osVersion, IProgress<string>? progress = null)
    {
        var checks = new List<InfCheck>();
        var files = DriverRepository.SafeEnumerate(repository, "*.inf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var n = 0;
        foreach (var path in files)
        {
            if (++n % 50 == 0) progress?.Report($"Contrôle des pilotes : {n}/{files.Count}...");
            checks.Add(CheckInf(path, architecture, osVersion));
        }
        return checks;
    }

    internal static InfCheck CheckInf(string path, string architecture, Version osVersion)
    {
        InfInfo inf;
        try { inf = InfParser.Parse(path); }
        catch (Exception ex) { return new InfCheck(path, false, $"INF illisible : {ex.Message}"); }

        if (inf.Models.Count == 0)
            return new InfCheck(path, false, "aucun périphérique déclaré", inf.Provider, inf.Class, inf.DriverVersion);
        if (!inf.Models.Any(m => InfParser.DecorationApplies(m.Decoration, architecture, osVersion)))
        {
            var declared = inf.Models.Select(m => DecorationArchitecture(m.Decoration)).Distinct().OrderBy(a => a);
            return new InfCheck(path, false, $"ne convient pas à l'image {architecture} {osVersion.ToString(3)} (déclare : {string.Join(", ", declared)})",
                inf.Provider, inf.Class, inf.DriverVersion);
        }
        var (signed, reason) = PackageVerifier.VerifyInfSignature(path, inf, architecture);
        return new InfCheck(path, signed, signed ? "signé, architecture compatible" : reason, inf.Provider, inf.Class, inf.DriverVersion);
    }

    private static string DecorationArchitecture(string decoration)
    {
        if (string.IsNullOrEmpty(decoration)) return "x86";
        var platform = decoration.Split('.')[0];
        return platform.Length > 2 ? platform[2..].ToLowerInvariant() : "x86";
    }

    // ------------------------------------------------------------------ injection

    /// <summary>Image WIM : montage, ajout des pilotes contrôlés, enregistrement ; en cas d'erreur, abandon des modifications et nettoyage.</summary>
    public static async Task<InjectionReport> InjectIntoWimAsync(string wim, int index, IReadOnlyList<string> repositories, bool dryRun = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAdministrator();
        wim = Path.GetFullPath(wim);
        EnsureWimFile(wim, forWrite: !dryRun);
        var watch = Stopwatch.StartNew();
        var report = new InjectionReport { Target = $"{wim} (index {index})", Repositories = repositories.Select(Path.GetFullPath).ToList() };

        progress?.Report($"Lecture de l'index {index} de {Path.GetFileName(wim)}...");
        var info = await GetWimIndexInfoAsync(wim, index, ct).ConfigureAwait(false);
        report.Architecture = info.Architecture;
        report.OsVersion = info.Version;
        progress?.Report($"Image : {info.Name} — {info.ArchitectureLabel}, Windows {info.Version}");
        if (info.Architecture is null || info.Version is null)
            return Finish(report, watch, false, "Architecture ou version de l'image illisibles : injection annulée.");

        Verify(report, progress);
        if (!report.Accepted.Any())
            return Finish(report, watch, false, "Aucun pilote du dépôt ne convient à cette image (signature ou architecture) : rien à injecter.");
        if (dryRun)
            return Finish(report, watch, true, $"Simulation : {report.Accepted.Count()} INF seraient injectés, {report.Refused.Count()} refusés. Image non modifiée.");

        var mountDir = Path.Combine(AppPaths.Ensure(Path.Combine(AppPaths.Root, "Mount")), $"{DateTime.Now:yyyyMMdd-HHmmss}-{index}");
        Directory.CreateDirectory(mountDir);
        var mounted = false;
        try
        {
            progress?.Report($"Montage de l'image dans {mountDir} (quelques minutes)...");
            var mount = await RunDismAsync(MountArgs(wim, index, mountDir), TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
            if (mount.ExitCode != 0)
                return Finish(report, watch, false, $"Montage impossible : {DescribeFailure(mount)}");
            mounted = true;

            await AddDriversAsync(mountDir, report, progress, ct).ConfigureAwait(false);
            if (report.InjectedCount == 0)
                throw new InvalidOperationException(report.Message.Length > 0 ? report.Message : "aucun pilote n'a pu être ajouté");

            await CountDriversAsync(mountDir, report, ct).ConfigureAwait(false);

            progress?.Report("Enregistrement de l'image (/Unmount-Image /Commit, plusieurs minutes)...");
            var commit = await RunDismAsync(UnmountArgs(mountDir, commit: true), TimeSpan.FromMinutes(90), CancellationToken.None).ConfigureAwait(false);
            if (commit.ExitCode != 0)
                throw new InvalidOperationException($"Enregistrement de l'image impossible : {DescribeFailure(commit)}");
            mounted = false;
            report.Committed = true;
            var message = $"{report.InjectedCount} pilote(s) injecté(s) dans {Path.GetFileName(wim)} (index {index})" +
                          (report.FailedCount > 0 ? $", {report.FailedCount} refusé(s) par DISM" : "") +
                          (report.Refused.Any() ? $", {report.Refused.Count()} écarté(s) au contrôle" : "") + ".";
            return Finish(report, watch, report.FailedCount == 0, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Injection dans {wim} (index {index}) interrompue", ex);
            report.Committed = false;
            return Finish(report, watch, false, $"Injection annulée, image inchangée : {ex.Message}");
        }
        finally
        {
            if (mounted) await DiscardAsync(mountDir, progress).ConfigureAwait(false);
            TryDeleteDirectory(mountDir);
        }
    }

    /// <summary>Windows hors ligne (ex. disque d'un autre poste branché en D:) : /Image:D:\ /Add-Driver.</summary>
    public static async Task<InjectionReport> InjectIntoOfflineAsync(string windowsRoot, IReadOnlyList<string> repositories, bool dryRun = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAdministrator();
        var root = ImageRoot(windowsRoot);
        var watch = Stopwatch.StartNew();
        var report = new InjectionReport { Target = root, Repositories = repositories.Select(Path.GetFullPath).ToList() };

        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.Equals(Path.GetPathRoot(root), systemRoot, StringComparison.OrdinalIgnoreCase) && root.Length <= 3)
            return Finish(report, watch, false, $"{root} est le Windows en cours d'exécution : utilisez « spokapilote install » pour ce poste.");
        if (!File.Exists(Path.Combine(root, "Windows", "System32", "config", "SYSTEM")))
            return Finish(report, watch, false, $"Aucune installation de Windows trouvée dans {root} (dossier Windows\\System32\\config absent).");

        var detected = ReadOfflineWindows(root);
        if (detected is null)
            return Finish(report, watch, false, $"Architecture du Windows de {root} illisible (ntoskrnl.exe).");
        report.Architecture = detected.Value.Architecture;
        report.OsVersion = detected.Value.Version;
        progress?.Report($"Windows hors ligne : {root} — {detected.Value.Architecture}, version {detected.Value.Version}");

        Verify(report, progress);
        if (!report.Accepted.Any())
            return Finish(report, watch, false, "Aucun pilote du dépôt ne convient à ce Windows (signature ou architecture) : rien à injecter.");
        if (dryRun)
            return Finish(report, watch, true, $"Simulation : {report.Accepted.Count()} INF seraient injectés, {report.Refused.Count()} refusés. Windows non modifié.");

        await AddDriversAsync(root, report, progress, ct).ConfigureAwait(false);
        if (report.InjectedCount > 0) await CountDriversAsync(root, report, ct).ConfigureAwait(false);
        report.Committed = report.InjectedCount > 0;
        var message = report.InjectedCount > 0
            ? $"{report.InjectedCount} pilote(s) injecté(s) dans {root}" + (report.FailedCount > 0 ? $", {report.FailedCount} refusé(s) par DISM" : "") +
              (report.Refused.Any() ? $", {report.Refused.Count()} écarté(s) au contrôle" : "") + "."
            : $"Aucun pilote injecté : {report.Message}";
        return Finish(report, watch, report.InjectedCount > 0 && report.FailedCount == 0, message);
    }

    private static void Verify(InjectionReport report, IProgress<string>? progress)
    {
        foreach (var repo in report.Repositories)
        {
            if (!Directory.Exists(repo)) throw new DirectoryNotFoundException($"Dépôt introuvable : {repo}");
            progress?.Report($"Contrôle des pilotes de {repo} (signature, architecture {report.Architecture})...");
            report.Checks.AddRange(VerifyRepository(repo, report.Architecture!, report.OsVersion!, progress));
        }
        var accepted = report.Accepted.Count();
        progress?.Report($"{report.Checks.Count} INF contrôlé(s) : {accepted} accepté(s), {report.Checks.Count - accepted} écarté(s).");
        foreach (var refused in report.Refused) Log.Info($"INF écarté : {refused.Path} — {refused.Reason}");
    }

    /// <summary>
    /// Ajoute les INF acceptés. Dépôt entièrement accepté : un seul appel /Driver:&lt;dépôt&gt; /Recurse. Sinon, les appels
    /// sont planifiés dossier par dossier (voir <see cref="PlanAddDriverCalls"/>) pour que DISM n'ajoute jamais un INF écarté.
    /// </summary>
    private static async Task AddDriversAsync(string imageRoot, InjectionReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var logPath = Path.Combine(AppPaths.Logs, $"dism-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        report.DismLog = logPath;
        var calls = report.Repositories
            .SelectMany(repo => PlanAddDriverCalls(repo, report.Checks.Where(c => IsUnder(c.Path, repo)).ToList()))
            .ToList();

        var call = 0;
        foreach (var (driver, recurse) in calls)
        {
            ct.ThrowIfCancellationRequested();
            call++;
            progress?.Report($"Ajout des pilotes [{call}/{calls.Count}] : {driver}{(recurse ? " (/Recurse)" : "")}...");
            var result = await RunDismAsync(AddDriverArgs(imageRoot, driver, recurse, logPath), TimeSpan.FromMinutes(120), ct).ConfigureAwait(false);
            var lines = ParseAddDriver(result.StdOut);
            report.Results.AddRange(lines);
            foreach (var l in lines.Where(l => !l.Success)) Log.Warn($"DISM a refusé {l.InfPath} : {l.Message}");
            if (lines.Count == 0 && result.ExitCode != 0)
            {
                report.Message = $"DISM /Add-Driver : {DescribeFailure(result)}";
                // Erreur d'initialisation de la session (image illisible, droits...) : inutile de poursuivre.
                if (report.Results.Count == 0) break;
            }
            if (lines.Count > 0) progress?.Report($"  {lines.Count(l => l.Success)} ajouté(s), {lines.Count(l => !l.Success)} refusé(s) par DISM.");
        }
    }

    private static async Task CountDriversAsync(string imageRoot, InjectionReport report, CancellationToken ct)
    {
        try
        {
            report.DriversInImage = (await GetImageDriversAsync(imageRoot, ct).ConfigureAwait(false)).Count(d => !d.Inbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"Comptage des pilotes de l'image impossible : {ex.Message}");
        }
    }

    private static async Task DiscardAsync(string mountDir, IProgress<string>? progress)
    {
        progress?.Report("Démontage sans enregistrer (/Unmount-Image /Discard)...");
        var discard = await RunDismAsync(UnmountArgs(mountDir, commit: false), TimeSpan.FromMinutes(30), CancellationToken.None).ConfigureAwait(false);
        if (discard.ExitCode == 0) return;
        Log.Warn($"Démontage impossible ({DescribeFailure(discard)}) : nettoyage des points de montage.");
        progress?.Report("Nettoyage des points de montage (/Cleanup-Mountpoints)...");
        await RunDismAsync("/English /Cleanup-Mountpoints", TimeSpan.FromMinutes(30), CancellationToken.None).ConfigureAwait(false);
    }

    private static InjectionReport Finish(InjectionReport report, Stopwatch watch, bool success, string message)
    {
        report.Success = success;
        report.Message = message;
        report.Duration = watch.Elapsed;
        try
        {
            var file = Path.Combine(AppPaths.Reports, $"injection-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                report.Target,
                report.Architecture,
                osVersion = report.OsVersion?.ToString(),
                report.Repositories,
                report.Success,
                report.Committed,
                report.Message,
                report.DriversInImage,
                duration = report.Duration.ToString(@"hh\:mm\:ss"),
                report.DismLog,
                injected = report.Results.Where(r => r.Success).Select(r => r.InfPath),
                rejectedByDism = report.Results.Where(r => !r.Success).Select(r => new { r.InfPath, r.Message }),
                refusedBeforeInjection = report.Refused.Select(r => new { r.Path, r.Reason }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            report.ReportFile = file;
        }
        catch (Exception ex)
        {
            Log.Debug($"Rapport d'injection non enregistré : {ex.Message}");
        }
        (success ? (Action<string>)Log.Info : Log.Warn)($"Injection {report.Target} : {message}");
        return report;
    }

    // ------------------------------------------------------------------ utilitaires

    private static async Task<ProcessResult> RunDismAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Log.Info($"dism {arguments}");
        var result = await ProcessRunner.RunAsync(Dism, arguments, timeout: timeout, ct: ct).ConfigureAwait(false);
        Log.Debug($"dism (code {result.ExitCode}) :\n{CleanOutput(result.Combined)}");
        if (result.ExitCode != 0) Log.Warn($"dism {arguments.Split(' ').Skip(1).FirstOrDefault()} : {DescribeFailure(result)}");
        return result;
    }

    /// <summary>Sortie DISM sans les barres de progression (« [====   12.0%   ] ») ni les lignes vides.</summary>
    internal static string CleanOutput(string output) =>
        string.Join("\n", output.Replace("\r", "").Split('\n')
            .Where(l => l.Trim().Length > 0 && !Regex.IsMatch(l, @"^\s*\[[=\s]*\d+(?:[.,]\d+)?%[=\s]*\]\s*$")));

    private static void EnsureAdministrator()
    {
        if (!Elevation.IsAdministrator())
            throw new UnauthorizedAccessException("DISM nécessite une console administrateur (clic droit > Exécuter en tant qu'administrateur).");
    }

    private static void EnsureWimFile(string wim, bool forWrite)
    {
        if (!File.Exists(wim)) throw new FileNotFoundException($"Image introuvable : {wim}");
        var ext = Path.GetExtension(wim).ToLowerInvariant();
        if (forWrite && ext == ".esd")
            throw new NotSupportedException("Une image .esd (compressée) ne peut pas être modifiée : convertissez-la d'abord en .wim " +
                                            "(dism /Export-Image /SourceImageFile:install.esd /SourceIndex:n /DestinationImageFile:install.wim /Compress:max).");
        if (forWrite && ext == ".swm")
            throw new NotSupportedException("Une image fractionnée (.swm) ne peut pas être modifiée : travaillez sur le install.wim d'origine puis refractionnez-le.");
        if (forWrite && File.GetAttributes(wim).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"{Path.GetFileName(wim)} est en lecture seule (copié depuis un DVD/ISO ?) : retirez l'attribut lecture seule.");
    }

    private static bool IsUnder(string path, string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Debug($"Dossier de montage non supprimé ({dir}) : {ex.Message}");
        }
    }
}
