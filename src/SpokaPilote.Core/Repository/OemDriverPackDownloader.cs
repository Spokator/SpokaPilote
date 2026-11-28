using System.ComponentModel;
using System.Net;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Repository;

public sealed record DriverPackDownloadResult(
    bool Success,
    string Folder,
    string Message,
    int InfCount = 0,
    int SignedInfCount = 0,
    IReadOnlyList<string>? Rejected = null,
    bool AlreadyPresent = false,
    int FirmwareInfCount = 0);

public sealed class DriverPackDownloadOptions
{
    /// <summary>Retélécharge même si le dépôt contient déjà ce pack.</summary>
    public bool Force { get; init; }
    /// <summary>Conserve le fichier téléchargé (sinon supprimé après extraction : plusieurs Go).</summary>
    public bool KeepDownload { get; init; }
}

/// <summary>
/// Télécharge un pack officiel dans le dépôt : &lt;dépôt&gt;\&lt;Fabricant&gt;\&lt;Modèle&gt;\&lt;WinXX-arch&gt;\.
/// Chaîne de contrôle : hôte officiel HTTPS (Downloader), empreinte du catalogue, signature Authenticode et éditeur
/// approuvé pour les .exe, extraction sans installation, puis contrôle des catalogues (.cat) des INF extraits.
/// Surface (MSI sans empreinte publiée) : lien relu sur la page officielle, taille annoncée vérifiée, signature
/// Authenticode « Microsoft Corporation » obligatoire, extraction par installation administrative (msiexec /a).
/// La source « Dépôt local » indexe ensuite automatiquement ces INF.
/// </summary>
public static class OemDriverPackDownloader
{
    public const string ManifestName = "spokapilote-pack.json";

    public static string TargetFolder(string repositoryRoot, DriverPackMatch match) =>
        Path.Combine(repositoryRoot, match.Pack.VendorName, AppPaths.SafeName(match.ModelLabel, 60), match.Pack.FolderOs);

    public static async Task<DriverPackDownloadResult> DownloadAsync(
        DriverPackMatch match,
        string repositoryRoot,
        Settings settings,
        DriverPackDownloadOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new DriverPackDownloadOptions();
        var pack = match.Pack;
        var folder = TargetFolder(repositoryRoot, match);

        if (!pack.HasHash && !pack.RequiresMicrosoftSignature)
            return new(false, folder, "Pack refusé : le catalogue ne fournit aucune empreinte pour ce fichier.");
        if (pack.Format is not ("cab" or "exe" or "zip") && !(pack.Format == "msi" && pack.RequiresMicrosoftSignature))
            return new(false, folder, $"Format de pack non pris en charge : .{pack.Format}");

        if (pack.RequiresMicrosoftSignature)
        {
            // Le MSI Surface est remplacé à chaque mise à jour : le lien est relu sur la page officielle.
            progress?.Report($"Vérification du lien sur la page officielle {pack.InfoUrl}...");
            try
            {
                pack = await SurfaceDriverPacks.RefreshAsync(pack, ct).ConfigureAwait(false);
            }
            catch (SurfacePackUnavailableException ex)
            {
                Log.Warn(ex.Message);
                return new(false, folder, ex.Message);
            }
            match = match with { Pack = pack };
        }

        if (!options.Force && ReadManifest(folder) is { } manifest && manifest.Matches(pack))
            return new(true, folder, $"Déjà présent dans le dépôt ({pack.Id} {pack.Version}) : {folder}", manifest.InfCount, manifest.SignedInfCount,
                AlreadyPresent: true, FirmwareInfCount: manifest.FirmwareInfCount);

        if (CheckFreeSpace(pack, repositoryRoot) is { } spaceError)
            return new(false, folder, spaceError);

        var trust = new TrustPolicy(settings);
        // Téléchargement, vérification et extraction dans un dossier de travail neuf et privé, supprimé à la fin.
        using var work = WorkDirectory.Create("pack");
        using var downloader = new Downloader(trust);
        progress?.Report($"Téléchargement de {pack.FileName} ({pack.SizeLabel}) depuis {new Uri(pack.Url).Host}...");
        var lastPercent = -1L;
        string file;
        try
        {
            file = await downloader.DownloadAsync(pack.Url, pack.FileName, pack.Sha256, pack.Sha1, pack.Md5,
                new Progress<DownloadProgress>(p =>
                {
                    if (p.Total is not > 0) return;
                    var percent = p.Received * 100 / p.Total.Value;
                    if (percent / 10 == lastPercent / 10) return;
                    lastPercent = percent;
                    progress?.Report($"Téléchargement {percent} % ({p.Received / 1048576.0:0} / {p.Total.Value / 1048576.0:0} Mo)");
                }), ct, directory: work.Path).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
        {
            var obsolete = $"Lien du pack obsolète ({(int)ex.StatusCode!} sur {new Uri(pack.Url).Host}) : le constructeur a retiré {pack.FileName}. " +
                          "Relancez « spokapilote driverpack list » pour obtenir la version actuelle.";
            Log.Warn(obsolete);
            return new(false, folder, obsolete);
        }

        if (pack.HasHash)
        {
            progress?.Report("Empreinte vérifiée (" + (pack.Sha256 is not null ? "SHA-256" : pack.Sha1 is not null ? "SHA-1" : "MD5") + ").");
        }
        else if (pack.Size is > 0 && new FileInfo(file).Length != pack.Size)
        {
            return Refuse(file, folder, $"Pack refusé : taille reçue {new FileInfo(file).Length} octets, {pack.Size} annoncés par la page officielle.");
        }

        // Signature : obligatoire et d'un éditeur approuvé pour un exécutable ; signature Microsoft Corporation
        // obligatoire pour un MSI Surface (aucune empreinte publiée) ; vérifiée si présente pour une archive.
        var sig = SignatureVerifier.Verify(file);
        if (pack.RequiresMicrosoftSignature)
        {
            if (!sig.IsValid) return Refuse(file, folder, $"Pack refusé : {sig.Detail}.");
            if (!IsMicrosoftCorporation(sig.Signer)) return Refuse(file, folder, $"Pack refusé : signé par {sig.Signer}, « Microsoft Corporation » attendu.");
            progress?.Report($"Taille conforme à la page officielle, signature valide : {TrustPolicy.ExtractRdn(sig.Signer ?? "", "CN") ?? sig.Signer}.");
        }
        else if (pack.Format == "exe")
        {
            if (!sig.IsValid) return Refuse(file, folder, $"Pack refusé : {sig.Detail}.");
            if (!trust.IsTrustedPublisher(sig.Signer)) return Refuse(file, folder, $"Pack refusé : éditeur non approuvé ({sig.Signer}).");
            progress?.Report($"Signature valide : {TrustPolicy.ExtractRdn(sig.Signer ?? "", "CN") ?? sig.Signer}.");
        }
        else if (sig.IsValid)
        {
            progress?.Report($"Archive signée : {TrustPolicy.ExtractRdn(sig.Signer ?? "", "CN") ?? sig.Signer}.");
        }
        else if (SignatureVerifier.GetSigner(file) is not null)
        {
            return Refuse(file, folder, $"Pack refusé : signature de l'archive invalide ({sig.Detail}).");
        }

        // Extraction dans un dossier au chemin court : l'arborescence des packs est profonde et les extracteurs
        // constructeurs échouent au-delà de 260 caractères (constaté avec un pack Dell).
        var staging = work.NewSubdirectory("pk");
        int infs, signed, firmware;
        List<string> rejected;
        try
        {
            progress?.Report($"Extraction dans {staging}...");
            await ExtractAsync(pack, file, staging, ct).ConfigureAwait(false);

            progress?.Report("Contrôle des signatures des pilotes extraits...");
            (infs, signed, rejected) = await Task.Run(() => VerifyInfs(staging, pack.Architecture), ct).ConfigureAwait(false);
            firmware = await Task.Run(() => CountFirmwareInfs(staging), ct).ConfigureAwait(false);
            if (infs == 0)
                return new(false, folder, "Aucun fichier INF dans le pack extrait (extraction incomplète ?).");
            if (signed == 0)
                return new(false, folder, $"Aucun INF signé dans le pack ({infs} INF) : pack refusé.", infs, 0, rejected);

            // Windows (pnputil, DISM) ne lit pas toujours les chemins de plus de 259 caractères.
            var longest = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f).Length).DefaultIfEmpty(0).Max();
            if (Path.GetFullPath(folder).Length + 1 + longest > 259)
                return new(false, folder, $"Chemin du dépôt trop long pour ce pack ({Path.GetFullPath(folder).Length + 1 + longest} caractères, limite Windows 259) : " +
                                          "choisissez un dépôt plus court, par exemple D:\\Pilotes ou \\\\srv\\pilotes.", infs, signed, rejected);

            await WriteManifestAsync(staging, match, sig, infs, signed, firmware, rejected, ct).ConfigureAwait(false);
            progress?.Report($"Copie dans le dépôt : {folder}...");
            await Task.Run(() => Publish(staging, folder), ct).ConfigureAwait(false);
            // Déplacé depuis le dossier de travail privé : droits du dépôt rétablis (héritage de son dossier parent).
            if (DataProtection.Enforced)
            {
                try { DataProtection.Security.ResetToInherited(folder, isDirectory: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Log.Warn($"Droits du dossier {folder} non rétablis : {ex.Message}"); }
            }
        }
        finally
        {
            TryDelete(staging);
        }

        if (options.KeepDownload)
        {
            // Le dossier de travail est supprimé à la fin : le fichier conservé est rangé à côté du pack dans le dépôt.
            var kept = Path.Combine(Path.GetDirectoryName(folder)!, Path.GetFileName(file));
            try { File.Copy(file, kept, overwrite: true); Log.Info($"Fichier du pack conservé : {kept}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Fichier du pack non conservé ({kept}) : {ex.Message}"); }
        }
        var message = $"{signed} pilote(s) INF signé(s) sur {infs} prêt(s) dans {folder}";
        if (firmware > 0) message += $" (dont {firmware} firmware(s) UEFI : installés seulement avec --include-firmware)";
        if (rejected.Count > 0) message += $" — {rejected.Count} INF sans signature valide (ils seront refusés à l'installation)";
        Log.Info($"Pack {pack.VendorName} {pack.Id} ({match.ModelLabel}, {pack.OsLabel}) : {message}");
        return new(true, folder, message, infs, signed, rejected, FirmwareInfCount: firmware);
    }

    /// <summary>
    /// Remplace le dossier du modèle dans le dépôt : renommage si même volume, sinon copie dans un dossier
    /// « .partiel » puis renommage (le dépôt ne contient jamais un pack à moitié copié).
    /// </summary>
    private static void Publish(string staging, string folder)
    {
        var parent = Path.GetDirectoryName(folder)!;
        Directory.CreateDirectory(parent);
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(staging)), Path.GetPathRoot(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
        var source = staging;
        if (!sameVolume)
        {
            source = folder + ".partiel";
            TryDelete(source);
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(source, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(source, folder);
        }
        catch
        {
            if (!sameVolume) TryDelete(source);
            throw;
        }
    }

    // ------------------------------------------------------------------ extraction

    /// <summary>Commande d'extraction (sans installation) propre à chaque constructeur ; null pour une archive extraite par SpokaPilote.</summary>
    internal static string? ExtractionArguments(OemDriverPack pack, string destination)
    {
        destination = Path.TrimEndingDirectorySeparator(destination);
        if (pack.Format != "exe") return null;
        return pack.Vendor switch
        {
            // Dell Command | Deploy : paquet DUP auto-extractible.
            // (/l : journal du DUP, utile au diagnostic).
            OemVendor.Dell => $"/s /e=\"{destination}\" /l=\"{Path.Combine(AppPaths.Logs, $"dell-{AppPaths.SafeName(pack.Id)}.log")}\"",
            // SoftPaq HP : /s silencieux, /e extraction seule, /f dossier cible.
            OemVendor.HP => $"/s /e /f \"{destination}\"",
            // Lenovo : installeur Inno Setup qui n'extrait que les fichiers avec /EXTRACT=YES.
            OemVendor.Lenovo => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{destination}\" /EXTRACT=\"YES\"",
            _ => throw new NotSupportedException($"Extraction non prise en charge pour {pack.Vendor}."),
        };
    }

    /// <summary>
    /// Codes de sortie d'une extraction réussie. Le wrapper SoftPaq HP (hpsoftpaqwrapper.exe) renvoie 1168 après une
    /// extraction seule complète (constaté sur sp170248 : 2,98 Go extraits, code 1168 à chaque essai).
    /// </summary>
    internal static int[] ExtractionSuccessCodes(OemDriverPack pack) => pack.Vendor == OemVendor.HP ? new[] { 0, 1168 } : new[] { 0 };

    /// <summary>Le sujet du certificat est-il celui de Microsoft Corporation (O= ou, à défaut, CN=) ?</summary>
    internal static bool IsMicrosoftCorporation(string? subject) =>
        subject is not null &&
        string.Equals(TrustPolicy.ExtractRdn(subject, "O") ?? TrustPolicy.ExtractRdn(subject, "CN"), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractAsync(OemDriverPack pack, string file, string destination, CancellationToken ct)
    {
        if (pack.Format == "msi")
        {
            await ExtractMsiAsync(pack, file, destination, ct).ConfigureAwait(false);
            return;
        }
        var args = ExtractionArguments(pack, destination);
        if (args is null)
        {
            // .cab / .zip : le fichier porte parfois une extension en majuscules (Dell « .CAB »).
            var normalized = file;
            if (!Path.GetExtension(file).Equals("." + pack.Format, StringComparison.Ordinal))
            {
                normalized = Path.ChangeExtension(file, pack.Format);
                File.Move(file, normalized, overwrite: true);
            }
            await Archive.ExtractAsync(normalized, destination, ct).ConfigureAwait(false);
            if (normalized != file) File.Move(normalized, file, overwrite: true);
            return;
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(file, args, Path.GetDirectoryName(file), TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException("L'extraction de ce pack demande les droits administrateur : relancez dans une console administrateur.");
        }
        if (result.TimedOut) throw new TimeoutException("Délai d'extraction du pack dépassé (60 min).");
        // Un autre code signale une extraction incomplète (constaté : un pack Dell s'arrête au premier fichier qu'il
        // ne peut pas écrire et renvoie 1 en laissant une partie des pilotes) : le pack est refusé plutôt que publié à moitié.
        if (!ExtractionSuccessCodes(pack).Contains(result.ExitCode))
            throw new IOException($"Extraction du pack incomplète (code {result.ExitCode})" +
                                  (pack.Vendor == OemVendor.Dell ? $", voir {Path.Combine(AppPaths.Logs, $"dell-{AppPaths.SafeName(pack.Id)}.log")}" : "") +
                                  (Elevation.IsAdministrator() ? "." : " ; si le problème persiste, relancez dans une console administrateur."));
    }

    /// <summary>
    /// MSI Surface : installation administrative (msiexec /a ... TARGETDIR=), qui copie les fichiers sans rien
    /// installer ni exécuter d'action personnalisée (méthode documentée par Microsoft pour WinPE et MDT).
    /// La copie administrative du .msi déposée à la racine est ensuite supprimée (inutile dans le dépôt).
    /// </summary>
    private static async Task ExtractMsiAsync(OemDriverPack pack, string file, string destination, CancellationToken ct)
    {
        var log = Path.Combine(AppPaths.Logs, $"msiexec-{AppPaths.SafeName(pack.Id, 60)}.log");
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("msiexec.exe"), SurfaceDriverPacks.MsiAdminArguments(file, destination, log),
            Path.GetDirectoryName(file), TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
        if (result.TimedOut) throw new TimeoutException("Délai d'extraction du MSI dépassé (60 min).");
        if (result.ExitCode != 0)
            throw new IOException($"Extraction du MSI impossible : {SurfaceDriverPacks.DescribeMsiExitCode(result.ExitCode)}, voir {log}" +
                                  (Elevation.IsAdministrator() ? "." : " ; relancez dans une console administrateur."));
        foreach (var copy in Directory.EnumerateFiles(destination, "*.msi", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(copy); } catch (IOException ex) { Log.Debug($"Copie administrative non supprimée ({copy}) : {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ contrôle

    /// <summary>INF de firmware (classe « Firmware », capsules UEFI) : comptés à part, jamais installés sans --include-firmware.</summary>
    internal static int CountFirmwareInfs(string folder)
    {
        var count = 0;
        foreach (var path in DriverRepository.SafeEnumerate(folder, "*.inf"))
        {
            try { if (InfParser.Parse(path).IsFirmware) count++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { /* déjà signalé par VerifyInfs */ }
        }
        return count;
    }

    /// <summary>Compte les INF et vérifie le catalogue de chacun (présent, signature valide). Le contrôle fin par périphérique reste fait à l'installation.</summary>
    internal static (int Infs, int Signed, List<string> Rejected) VerifyInfs(string folder, string architecture)
    {
        var infs = DriverRepository.SafeEnumerate(folder, "*.inf").ToList();
        var rejected = new List<string>();
        var signed = 0;
        foreach (var path in infs)
        {
            InfInfo inf;
            try { inf = InfParser.Parse(path); }
            catch (Exception ex)
            {
                rejected.Add($"{Path.GetRelativePath(folder, path)} : illisible ({ex.Message})");
                continue;
            }
            var (ok, reason) = PackageVerifier.VerifyInfSignature(path, inf, architecture);
            if (ok) signed++;
            else rejected.Add($"{Path.GetRelativePath(folder, path)} : {reason}");
        }
        return (infs.Count, signed, rejected);
    }

    private static string? CheckFreeSpace(OemDriverPack pack, string repositoryRoot)
    {
        if (pack.Size is not > 0) return null;
        try
        {
            // Contenu extrait : jusqu'à 4 fois la taille du pack (constaté : Dell 484 Mo → 1,98 Go, HP 890 Mo → 2,98 Go),
            // plus une marge. Le téléchargement et l'extraction se font dans un dossier de travail (%ProgramData%\SpokaPilote\Work),
            // puis le pack est déplacé (même volume) ou copié dans le dépôt.
            var extracted = (long)(pack.Size.Value * 4.5);
            var workDrive = new DriveInfo(Path.GetPathRoot(AppPaths.Work)!);
            var needWork = pack.Size.Value + extracted;
            if (workDrive.AvailableFreeSpace < needWork)
                return $"Espace disque insuffisant sur {workDrive.Name} : {needWork / 1073741824.0:0.0} Go nécessaires, {workDrive.AvailableFreeSpace / 1073741824.0:0.0} Go libres.";

            var full = Path.GetFullPath(repositoryRoot);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null; // partage réseau : espace non mesurable de façon fiable
            var repoDrive = new DriveInfo(Path.GetPathRoot(full)!);
            if (repoDrive.Name != workDrive.Name && repoDrive.AvailableFreeSpace < extracted)
                return $"Espace disque insuffisant sur {repoDrive.Name} : {extracted / 1073741824.0:0.0} Go nécessaires, {repoDrive.AvailableFreeSpace / 1073741824.0:0.0} Go libres.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // lecteur non mesurable : on tente quand même
        }
        return null;
    }

    private static DriverPackDownloadResult Refuse(string file, string folder, string message)
    {
        TryDeleteFile(file);
        Log.Warn(message);
        return new(false, folder, message);
    }

    // ------------------------------------------------------------------ manifeste

    internal sealed class PackManifest
    {
        public string CreatedBy { get; set; } = "SpokaPilote";
        public string Kind { get; set; } = "oem-driver-pack";
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public List<string> Models { get; set; } = new();
        public List<string> SystemIds { get; set; } = new();
        public string Os { get; set; } = "";
        public string Architecture { get; set; } = "";
        public List<string> OsReleases { get; set; } = new();
        public string PackId { get; set; } = "";
        public string? Version { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public long? Size { get; set; }
        public string? Sha256 { get; set; }
        public string? Sha1 { get; set; }
        public string? Md5 { get; set; }
        public string? Signer { get; set; }
        public DateTime Downloaded { get; set; }
        public int InfCount { get; set; }
        public int SignedInfCount { get; set; }
        /// <summary>INF de firmware (UEFI, contrôleurs) parmi les INF du pack : installés seulement sur demande.</summary>
        public int FirmwareInfCount { get; set; }
        /// <summary>Page officielle d'origine (Surface) ou notes de version.</summary>
        public string? InfoUrl { get; set; }
        public List<string> RejectedInfs { get; set; } = new();

        public bool Matches(OemDriverPack pack) =>
            PackId.Equals(pack.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Sha256 ?? Md5, pack.Sha256 ?? pack.Md5, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static PackManifest? ReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ManifestName);
            return File.Exists(path) ? JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Task WriteManifestAsync(string folder, DriverPackMatch match, SignatureResult sig, int infs, int signed, int firmware, List<string> rejected, CancellationToken ct)
    {
        var pack = match.Pack;
        var manifest = new PackManifest
        {
            Vendor = pack.VendorName,
            Model = match.ModelLabel,
            Models = pack.Systems.Select(s => s.Name).ToList(),
            SystemIds = pack.Systems.SelectMany(s => s.Ids).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Os = pack.Os,
            Architecture = pack.Architecture,
            OsReleases = pack.OsReleases,
            PackId = pack.Id,
            Version = pack.Version,
            ReleaseDate = pack.Date,
            Url = pack.Url,
            FileName = pack.FileName,
            Size = pack.Size,
            Sha256 = pack.Sha256,
            Sha1 = pack.Sha1,
            Md5 = pack.Md5,
            Signer = sig.IsValid ? sig.Signer : null,
            Downloaded = DateTime.Now,
            InfCount = infs,
            SignedInfCount = signed,
            FirmwareInfCount = firmware,
            InfoUrl = pack.InfoUrl,
            RejectedInfs = rejected,
        };
        return File.WriteAllTextAsync(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions), ct);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception ex) { Log.Debug($"Nettoyage impossible ({dir}) : {ex.Message}"); }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Log.Debug($"Suppression impossible ({file}) : {ex.Message}"); }
    }
}
