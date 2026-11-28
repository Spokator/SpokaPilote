using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>Droits accordés à un SID ; <see cref="Inherit"/> = hérités par les sous-dossiers et fichiers (dossiers uniquement).</summary>
internal sealed record AclRule(SecurityIdentifier Sid, FileSystemRights Rights, bool Inherit);

/// <summary>DACL d'un élément : protégée (sans héritage) ou non, règles explicites (« deny » comprises).</summary>
internal sealed record DaclState(bool IsProtected, IReadOnlyList<AclRule> ExplicitAllow, bool HasExplicitDeny);

/// <summary>Opérations de sécurité Windows sur le système de fichiers : injectables pour les tests.</summary>
internal interface IFileSecurity
{
    /// <summary>Propriétaire, ou null s'il est illisible (droits refusés).</summary>
    SecurityIdentifier? GetOwner(string path, bool isDirectory);
    DaclState? GetDacl(string path, bool isDirectory);
    void SetOwner(string path, bool isDirectory, SecurityIdentifier owner);
    /// <summary>DACL protégée (aucun héritage du parent) composée exactement de ces règles.</summary>
    void SetProtectedDacl(string path, bool isDirectory, IReadOnlyList<AclRule> rules);
    /// <summary>Retire toutes les règles explicites et rétablit l'héritage du parent.</summary>
    void ResetToInherited(string path, bool isDirectory);
    /// <summary>Crée le dossier avec cette DACL protégée dès sa création (aucune fenêtre avec les droits hérités).</summary>
    void CreateProtectedDirectory(string path, IReadOnlyList<AclRule> rules);
    /// <summary>Active SeTakeOwnershipPrivilege / SeRestorePrivilege (au mieux) : reprise d'éléments verrouillés par leur créateur.</summary>
    void EnableOwnershipPrivileges();
}

/// <summary>
/// Protection du dossier de données (%ProgramData%\SpokaPilote) des exécutions élevées. Les tâches planifiées et l'agent
/// tournent en SYSTEM : tout ce qu'ils lisent (configuration, historique, catalogues en cache) ou exécutent (installeurs
/// téléchargés) doit être hors de portée des utilisateurs standard. Disposition appliquée :
/// <list type="bullet">
/// <item>racine : DACL protégée, propriétaire Administrateurs ; SYSTEM et Administrateurs : contrôle total ; Utilisateurs :
/// lecture et exécution (settings.json, journaux, historique, sauvegardes, rapports : utiles à l'interface non élevée) ;</item>
/// <item>dossiers privés (<see cref="DataRootProtector.PrivateFolders"/> : Cache, Work, Mount, Quarantaine...) et jeton de la
/// console (<see cref="SecretStore.TokenFileName"/>) : SYSTEM et Administrateurs seulement ;</item>
/// <item>aucun « CREATOR OWNER », aucune écriture des utilisateurs nulle part ;</item>
/// <item>tout élément qui n'appartient ni à SYSTEM, ni aux Administrateurs, ni à TrustedInstaller est repris puis supprimé
/// (données régénérables) ou mis en quarantaine (configuration, historique, sauvegardes) ; les liens (jonctions, liens
/// symboliques) sont supprimés sans être suivis.</item>
/// </list>
/// Rien n'est fait en mode portable (clé FAT32/exFAT sans ACL : la clé du technicien est réputée de confiance) ni par
/// un processus non élevé (qui écrit alors dans %LocalAppData%\SpokaPilote, voir <see cref="AppLayout.UserRoot"/>).
/// </summary>
public static class DataProtection
{
    public static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    public static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal static readonly IReadOnlyList<AclRule> RootRules =
    [
        new(SystemSid, FileSystemRights.FullControl, true),
        new(AdministratorsSid, FileSystemRights.FullControl, true),
        new(UsersSid, FileSystemRights.ReadAndExecute, true),
    ];

    internal static readonly IReadOnlyList<AclRule> PrivateRules =
    [
        new(SystemSid, FileSystemRights.FullControl, true),
        new(AdministratorsSid, FileSystemRights.FullControl, true),
    ];

    private static readonly Lazy<bool> Elevated = new(Elevation.IsAdministrator);
    private static bool? _enforcedOverride;

    /// <summary>Processus élevé (administrateur ou SYSTEM).</summary>
    public static bool IsElevated => Elevated.Value;

    /// <summary>
    /// Contrôles actifs : processus élevé, hors mode portable. Un processus non élevé n'a rien à protéger (il n'exécute
    /// rien avec des droits supérieurs) ; une clé portable n'a souvent pas d'ACL.
    /// </summary>
    public static bool Enforced => _enforcedOverride ?? (IsElevated && AppPaths.Layout.Mode != DataLocationMode.Portable);

    /// <summary>Tests : force l'état des contrôles (null = calcul normal).</summary>
    internal static IDisposable OverrideEnforcedForTest(bool? value)
    {
        var previous = _enforcedOverride;
        _enforcedOverride = value;
        return new Restore(() => _enforcedOverride = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    internal static IFileSecurity Security { get; set; } = WindowsFileSecurity.Instance;

    public static bool IsTrustedOwner(SecurityIdentifier? sid) =>
        sid is not null && (sid == SystemSid || sid == AdministratorsSid || sid == TrustedInstallerSid);

    /// <summary>
    /// Démarrage de la CLI et de l'interface. Élevé (hors portable) : protège le dossier de données (contrôle rapide, reprise
    /// complète si nécessaire), déplace un ancien jeton en clair de settings.json, nettoie les dossiers de travail
    /// abandonnés et applique la rétention des sauvegardes. Portable : retire les anciens caches partagés entre postes.
    /// Ne lève jamais d'exception.
    /// </summary>
    public static ProtectionReport? OnStartup(bool forceFullPass = false)
    {
        try
        {
            var layout = AppPaths.Layout;
            if (layout.Mode == DataLocationMode.Portable)
            {
                RemoveLegacySharedCaches(layout);
                return null;
            }
            if (!IsElevated) return null;
            var report = Protect(layout.Root, forceFullPass);
            if (report is null) return null;
            Settings.MigrateLegacyToken();
            WorkDirectory.CleanupLeftovers();
            return report;
        }
        catch (Exception ex)
        {
            Log.Warn($"Protection du dossier de données impossible : {ex.Message}");
            return null;
        }
    }

    /// <summary>Protège le dossier de données (processus élevé). Null si le dossier ne ressemble pas à un dossier de données.</summary>
    public static ProtectionReport? Protect(string root, bool forceFullPass = false)
    {
        if (!LooksLikeDataRoot(root, AppPaths.Layout.Mode))
        {
            Log.Warn($"{root} ne ressemble pas à un dossier de données {AppIdentity.Current.ProductName} : droits non modifiés.");
            return null;
        }
        var report = new DataRootProtector(Security).Ensure(root, DateTime.Now, forceFullPass);
        if (report.FullPass) Log.Write(report.Quarantined.Count + report.Deleted.Count + report.LinksRemoved.Count > 0 ? LogLevel.Warning : LogLevel.Info, report.Summary(root));
        foreach (var q in report.Quarantined) Log.Warn($"Élément non créé par un administrateur mis en quarantaine (ignoré) : {q}");
        return report;
    }

    /// <summary>
    /// Garde-fou d'un SPOKAPILOTE_HOME mal réglé : jamais la racine d'un lecteur ; un dossier existant doit être vide ou
    /// contenir des données reconnaissables. La version installée (%ProgramData%\SpokaPilote) est toujours acceptée.
    /// </summary>
    internal static bool LooksLikeDataRoot(string root, DataLocationMode mode)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase)) return false;
        if (mode == DataLocationMode.Installed || !Directory.Exists(full)) return true;
        var entries = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).OfType<string>().ToList();
        string[] known = ["settings.json", DataRootProtector.MarkerFileName, "Logs", "History", "Cache", "Backups", "Reports", WorkDirectory.FolderName];
        return entries.Count == 0 || entries.Any(e => known.Contains(e, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Un processus élevé peut-il se fier à cet élément du dossier de données ? Oui s'il appartient à SYSTEM, aux
    /// Administrateurs ou à TrustedInstaller et n'est pas un lien. Toujours oui quand les contrôles sont inactifs.
    /// </summary>
    public static bool IsTrustedForElevatedUse(string path)
    {
        if (!Enforced) return true;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            return IsTrustedOwner(Security.GetOwner(path, (attributes & FileAttributes.Directory) != 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fichier écrit par un processus élevé : propriétaire Administrateurs s'il appartient à un autre compte (stratégie
    /// « propriétaire par défaut = créateur de l'objet »), pour que les exécutions SYSTEM s'y fient ensuite.
    /// </summary>
    public static void ClaimOwnership(string path)
    {
        if (!Enforced) return;
        try
        {
            var isDir = Directory.Exists(path);
            if (!IsTrustedOwner(Security.GetOwner(path, isDir))) Security.SetOwner(path, isDir, AdministratorsSid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug($"Propriétaire de {path} non modifié : {ex.Message}");
        }
    }

    /// <summary>Dossier privé (SYSTEM + Administrateurs) créé avec sa DACL quand les contrôles sont actifs, sinon dossier ordinaire.</summary>
    public static string EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path)) return path;
        if (Enforced) Security.CreateProtectedDirectory(path, PrivateRules);
        else Directory.CreateDirectory(path);
        return path;
    }

    // ------------------------------------------------------------------ écriture sûre

    /// <summary>
    /// Écrit un fichier sans jamais réutiliser un fichier existant : fichier temporaire neuf (nom aléatoire, CreateNew),
    /// puis remplacement de la cible. Contrôles actifs : propriétaire Administrateurs, et DACL protégée si
    /// <paramref name="privateFile"/> (jeton) dès la création.
    /// </summary>
    public static void WriteFileAtomically(string path, byte[] content, bool privateFile = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{RandomName(8)}.tmp";
        try
        {
            using (var fs = privateFile && Enforced
                       ? new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, PrivateFileSecurity())
                       : new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }
            ClaimOwnership(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* remplacé au prochain essai */ }
        }
    }

    /// <summary>Texte (UTF-8 sans BOM par défaut ; le préambule de l'encodage est écrit, comme File.WriteAllText).</summary>
    public static void WriteTextAtomically(string path, string content, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);
        WriteFileAtomically(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
    }

    private static FileSecurity PrivateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in PrivateRules)
            security.AddAccessRule(new FileSystemAccessRule(rule.Sid, rule.Rights, AccessControlType.Allow));
        return security;
    }

    internal static string RandomName(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    // ------------------------------------------------------------------ suppression sûre

    /// <summary>
    /// Supprime un fichier ou une arborescence sans jamais suivre une jonction ni un lien symbolique (le lien lui-même
    /// est retiré). Renvoie false si tout n'a pas pu être supprimé.
    /// </summary>
    public static bool DeleteTree(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var attributes = info.Exists || Directory.Exists(path) ? File.GetAttributes(path) : (FileAttributes)(-1);
            if ((int)attributes == -1) return true;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return true;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path, recursive: false); // retire la jonction, jamais sa cible
                return true;
            }
            var ok = true;
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) ok &= DeleteTree(child);
            if (ok) Directory.Delete(path, recursive: false);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Suppression incomplète de {path} : {ex.Message}");
            return false;
        }
    }

    /// <summary>Taille d'une arborescence (sans suivre les liens), 0 si illisible.</summary>
    public static long TreeSize(string path)
    {
        try
        {
            long total = 0;
            var dir = new DirectoryInfo(path);
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total += info is FileInfo f ? f.Length : TreeSize(info.FullName);
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ mode portable

    /// <summary>
    /// Versions 4.0.0 et antérieures : politique et décisions de diffusion en cache à la racine de la clé, partagées par
    /// tous les postes (un poste de l'anneau pilote y laissait « autorisé » pour un poste hors ligne de l'anneau général).
    /// Ces caches sont désormais par poste (<see cref="AppLayout.MachineRoot"/>) : les anciens sont simplement supprimés.
    /// </summary>
    internal static void RemoveLegacySharedCaches(AppLayout layout)
    {
        if (string.Equals(layout.Root, layout.MachineRoot, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var name in new[] { AppPaths.PolicyCacheFileName, AppPaths.RolloutDecisionsCacheFileName })
        {
            var legacy = Path.Combine(layout.Root, name);
            try
            {
                if (File.Exists(legacy))
                {
                    File.Delete(legacy);
                    Log.Info($"Ancien cache partagé entre postes supprimé : {legacy} (les caches sont désormais propres à chaque poste).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Ancien cache {legacy} non supprimé : {ex.Message}");
            }
        }
    }
}

/// <summary>Résultat d'une protection du dossier de données.</summary>
public sealed class ProtectionReport
{
    /// <summary>Reprise complète (premier démarrage élevé, droits modifiés depuis, ou --force).</summary>
    public bool FullPass { get; set; }
    public bool RootCreated { get; set; }
    public int Checked { get; set; }
    public int Reset { get; set; }
    public List<string> Deleted { get; } = new();
    public List<string> Quarantined { get; } = new();
    public List<string> LinksRemoved { get; } = new();
    public List<string> Errors { get; } = new();
    public string? QuarantineFolder { get; set; }

    public string Summary(string root) => !FullPass
        ? $"Dossier de données protégé : {root}."
        : $"Dossier de données protégé : {root} ({Checked} élément(s) contrôlé(s), {Reset} droit(s) rétabli(s), " +
          $"{Deleted.Count} supprimé(s), {Quarantined.Count} mis en quarantaine, {LinksRemoved.Count} lien(s) retiré(s)" +
          (Errors.Count > 0 ? $", {Errors.Count} erreur(s)" : "") + ").";
}

/// <summary>Application de la disposition décrite dans <see cref="DataProtection"/> (logique testable, opérations injectées).</summary>
internal sealed class DataRootProtector(IFileSecurity security)
{
    public const string MarkerFileName = ".protection";
    public const string QuarantineFolderName = "Quarantaine";
    internal const string MarkerContent = "SpokaPilote : dossier de données protégé (v1)";

    /// <summary>Dossiers réservés aux exécutions élevées : aucun accès des utilisateurs.</summary>
    internal static readonly string[] PrivateFolders = ["Cache", WorkDirectory.FolderName, "Downloads", "Extracted", "Mount", QuarantineFolderName, "Mises-a-jour"];
    /// <summary>Contenu régénérable : un élément suspect y est supprimé (ailleurs : mis en quarantaine).</summary>
    internal static readonly string[] RegenerableFolders = ["Cache", WorkDirectory.FolderName, "Downloads", "Extracted", "Mount", "Mises-a-jour"];
    /// <summary>Dossiers créés d'emblée : jamais créés ensuite par un processus non élevé.</summary>
    internal static readonly string[] StandardFolders = ["Cache", WorkDirectory.FolderName, "Logs", "History", "Backups", "Reports"];

    private string _root = "";
    private DateTime _now;
    private ProtectionReport _report = new();

    public ProtectionReport Ensure(string root, DateTime now, bool forceFullPass = false)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _now = now;
        _report = new ProtectionReport();

        if (Directory.Exists(_root) && (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0 &&
            !DataProtection.IsTrustedOwner(security.GetOwner(_root, true)))
        {
            // Dossier de données remplacé par une jonction créée par un utilisateur : le lien est retiré, jamais suivi.
            Directory.Delete(_root, recursive: false);
            _report.LinksRemoved.Add(_root);
        }
        if (!Directory.Exists(_root))
        {
            security.CreateProtectedDirectory(_root, DataProtection.RootRules);
            _report.RootCreated = true;
        }
        if (!forceFullPass && !_report.RootCreated && IsCompliant()) return _report;

        _report.FullPass = true;
        security.EnableOwnershipPrivileges();
        // 1. Racine d'abord : dès que sa DACL est protégée, l'héritage retire aux utilisateurs l'écriture dans toute
        //    l'arborescence (la propagation ne suit pas les jonctions). Restent les éléments qu'ils possèdent : étape 2.
        if (!DataProtection.IsTrustedOwner(security.GetOwner(_root, true))) security.SetOwner(_root, true, DataProtection.AdministratorsSid);
        security.SetProtectedDacl(_root, true, DataProtection.RootRules);
        // 2. Chaque élément : liens retirés, éléments d'un autre propriétaire repris puis supprimés / mis en quarantaine.
        Walk(_root, depth: 0, topFolder: null);
        // 3. Dossiers standard et témoin.
        foreach (var name in StandardFolders)
        {
            var path = Path.Combine(_root, name);
            if (Directory.Exists(path)) continue;
            if (PrivateFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) security.CreateProtectedDirectory(path, DataProtection.PrivateRules);
            else Directory.CreateDirectory(path);
        }
        var marker = Path.Combine(_root, MarkerFileName);
        if (File.Exists(marker)) File.Delete(marker);
        File.WriteAllText(marker, MarkerContent);
        if (!DataProtection.IsTrustedOwner(security.GetOwner(marker, false))) security.SetOwner(marker, false, DataProtection.AdministratorsSid);
        return _report;
    }

    /// <summary>Contrôle rapide (une quinzaine de lectures) : racine, témoin, éléments de premier niveau.</summary>
    internal bool IsCompliant()
    {
        try
        {
            if (!DataProtection.IsTrustedOwner(security.GetOwner(_root, true))) return false;
            if (!Matches(security.GetDacl(_root, true), DataProtection.RootRules, isDirectory: true)) return false;
            var marker = Path.Combine(_root, MarkerFileName);
            if (!File.Exists(marker) || !DataProtection.IsTrustedOwner(security.GetOwner(marker, false)) || File.ReadAllText(marker) != MarkerContent) return false;
            foreach (var info in new DirectoryInfo(_root).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var isDir = info is DirectoryInfo;
                if (!DataProtection.IsTrustedOwner(security.GetOwner(info.FullName, isDir))) return false;
                if (!HasExpectedDacl(info.FullName, isDir, info.Name, depth: 0)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Walk(string dir, int depth, string? topFolder)
    {
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report.Errors.Add($"{Relative(dir)} : {ex.Message}");
            return;
        }

        foreach (var info in entries)
        {
            var path = info.FullName;
            var name = info.Name;
            var top = topFolder ?? name;
            var isDir = info is DirectoryInfo;
            _report.Checked++;
            try
            {
                if (depth == 0 && name.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase) && !isDir) continue; // récrit à la fin
                if (topFolder is not null && topFolder.Equals(QuarantineFolderName, StringComparison.OrdinalIgnoreCase)) continue;

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Jonction ou lien symbolique : retiré sans jamais être suivi (la cible n'est pas touchée).
                    if (isDir) Directory.Delete(path, recursive: false);
                    else File.Delete(path);
                    _report.LinksRemoved.Add(Relative(path));
                    continue;
                }

                if (!DataProtection.IsTrustedOwner(security.GetOwner(path, isDir)))
                {
                    // Reprise au mieux : même si elle échoue, un fichier reste supprimable ou déplaçable (contrôle total
                    // des administrateurs sur le dossier parent), et tout usage élevé le refuse (IsTrustedForElevatedUse).
                    try { TakeOver(path, isDir); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
                    {
                        _report.Errors.Add($"{Relative(path)} : reprise impossible ({ex.Message})");
                    }
                    if (!isDir)
                    {
                        if (IsRegenerable(top)) { File.Delete(path); _report.Deleted.Add(Relative(path)); }
                        else Quarantine(path);
                        continue;
                    }
                    // Dossier repris (propriétaire, droits) : son contenu est ensuite jugé élément par élément, car un
                    // dossier créé par l'interface non élevée d'une version antérieure contient souvent des fichiers légitimes.
                }
                else if (!HasExpectedDacl(path, isDir, name, depth))
                {
                    ApplyExpectedDacl(path, isDir, name, depth);
                    _report.Reset++;
                }

                // Images montées par DISM : seuls les éléments de premier niveau sont contrôlés.
                if (isDir && !(top.Equals("Mount", StringComparison.OrdinalIgnoreCase) && depth >= 1))
                    Walk(path, depth + 1, top);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _report.Errors.Add($"{Relative(path)} : {ex.Message}");
            }
        }
    }

    private void TakeOver(string path, bool isDir)
    {
        security.SetOwner(path, isDir, DataProtection.AdministratorsSid);
        var name = Path.GetFileName(path);
        var depth = Relative(path).Count(c => c == Path.DirectorySeparatorChar);
        ApplyExpectedDacl(path, isDir, name, depth);
        _report.Reset++;
    }

    private void Quarantine(string path)
    {
        var folder = Path.Combine(_root, QuarantineFolderName, _now.ToString("yyyyMMdd-HHmmss"));
        if (!Directory.Exists(Path.Combine(_root, QuarantineFolderName)))
            security.CreateProtectedDirectory(Path.Combine(_root, QuarantineFolderName), DataProtection.PrivateRules);
        var target = Path.Combine(folder, Relative(path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(path, target, overwrite: true);
        // Un déplacement garde les droits d'origine : ceux du dossier de quarantaine (sans accès des utilisateurs) sont
        // rétablis, au cas où le fichier contiendrait un secret (settings.json avec jeton).
        try { security.ResetToInherited(target, isDirectory: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { _report.Errors.Add($"{target} : {ex.Message}"); }
        _report.QuarantineFolder = folder;
        _report.Quarantined.Add($"{Relative(path)} → {target}");
    }

    private bool HasExpectedDacl(string path, bool isDir, string name, int depth)
    {
        var dacl = security.GetDacl(path, isDir);
        if (dacl is null) return false;
        if (ExpectedProtectedRules(isDir, name, depth) is { } rules) return Matches(dacl, rules, isDir);
        return !dacl.IsProtected && dacl.ExplicitAllow.Count == 0 && !dacl.HasExplicitDeny;
    }

    private void ApplyExpectedDacl(string path, bool isDir, string name, int depth)
    {
        if (ExpectedProtectedRules(isDir, name, depth) is { } rules) security.SetProtectedDacl(path, isDir, rules);
        else security.ResetToInherited(path, isDir);
    }

    /// <summary>Règles d'une DACL protégée attendue (dossier privé de premier niveau, jeton), null = héritage du parent.</summary>
    private static IReadOnlyList<AclRule>? ExpectedProtectedRules(bool isDir, string name, int depth)
    {
        if (depth != 0) return null;
        if (isDir && PrivateFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) return DataProtection.PrivateRules;
        if (!isDir && name.Equals(SecretStore.TokenFileName, StringComparison.OrdinalIgnoreCase)) return DataProtection.PrivateRules;
        return null;
    }

    internal static bool Matches(DaclState? dacl, IReadOnlyList<AclRule> expected, bool isDirectory)
    {
        if (dacl is null || !dacl.IsProtected || dacl.HasExplicitDeny) return false;
        static string Key(AclRule r, bool dir) => $"{r.Sid.Value}|{(dir && r.Inherit ? 1 : 0)}";
        var actual = dacl.ExplicitAllow.GroupBy(r => Key(r, isDirectory)).ToDictionary(g => g.Key, g => g.Aggregate((FileSystemRights)0, (a, r) => a | r.Rights));
        var wanted = expected.GroupBy(r => Key(r, isDirectory)).ToDictionary(g => g.Key, g => g.Aggregate((FileSystemRights)0, (a, r) => a | r.Rights));
        return actual.Count == wanted.Count && wanted.All(w => actual.TryGetValue(w.Key, out var rights) && Normalize(rights) == Normalize(w.Value));
    }

    /// <summary>Synchronize est ajouté par Windows à toute règle d'autorisation : ignoré dans la comparaison.</summary>
    private static FileSystemRights Normalize(FileSystemRights rights) => rights & ~FileSystemRights.Synchronize;

    private static bool IsRegenerable(string top) => RegenerableFolders.Contains(top, StringComparer.OrdinalIgnoreCase);

    private string Relative(string path) => Path.GetRelativePath(_root, path);
}

/// <summary>Implémentation Windows de <see cref="IFileSecurity"/> (API .NET de contrôle d'accès, privilèges via advapi32).</summary>
internal sealed class WindowsFileSecurity : IFileSecurity
{
    public static readonly WindowsFileSecurity Instance = new();

    private static FileSystemSecurity Read(string path, bool isDirectory, AccessControlSections sections) =>
        isDirectory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);

    private static void Write(string path, bool isDirectory, FileSystemSecurity security)
    {
        if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    public SecurityIdentifier? GetOwner(string path, bool isDirectory)
    {
        try
        {
            return Read(path, isDirectory, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public DaclState? GetDacl(string path, bool isDirectory)
    {
        try
        {
            var security = Read(path, isDirectory, AccessControlSections.Access);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();
            return new DaclState(security.AreAccessRulesProtected,
                rules.Where(r => r.AccessControlType == AccessControlType.Allow)
                    .Select(r => new AclRule((SecurityIdentifier)r.IdentityReference, r.FileSystemRights, r.InheritanceFlags != InheritanceFlags.None))
                    .ToList(),
                rules.Any(r => r.AccessControlType == AccessControlType.Deny));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void SetOwner(string path, bool isDirectory, SecurityIdentifier owner)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(owner);
        try
        {
            Write(path, isDirectory, security);
        }
        catch (InvalidOperationException) when (owner == DataProtection.AdministratorsSid)
        {
            // Compte sans droit de céder la propriété au groupe : SYSTEM (exécution en tâche planifiée).
            security.SetOwner(DataProtection.SystemSid);
            Write(path, isDirectory, security);
        }
    }

    public void SetProtectedDacl(string path, bool isDirectory, IReadOnlyList<AclRule> rules) =>
        Write(path, isDirectory, Build(isDirectory, rules));

    public void ResetToInherited(string path, bool isDirectory)
    {
        var security = Read(path, isDirectory, AccessControlSections.Access);
        foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
            security.RemoveAccessRuleSpecific(rule);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        Write(path, isDirectory, security);
    }

    public void CreateProtectedDirectory(string path, IReadOnlyList<AclRule> rules)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
        new DirectoryInfo(path).Create((DirectorySecurity)Build(isDirectory: true, rules));
        // Propriétaire par défaut : SYSTEM, ou Administrateurs pour un compte élevé (sauf stratégie « créateur de l'objet »).
        if (!DataProtection.IsTrustedOwner(GetOwner(path, true)))
        {
            try { SetOwner(path, true, DataProtection.AdministratorsSid); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Log.Debug($"Propriétaire de {path} non modifié : {ex.Message}"); }
        }
    }

    private static FileSystemSecurity Build(bool isDirectory, IReadOnlyList<AclRule> rules)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in rules)
            security.AddAccessRule(new FileSystemAccessRule(rule.Sid, rule.Rights,
                isDirectory && rule.Inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    // ------------------------------------------------------------------ privilèges

    public void EnableOwnershipPrivileges()
    {
        foreach (var privilege in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege" })
        {
            try { EnablePrivilege(privilege); }
            catch (Exception ex) { Log.Debug($"Privilège {privilege} non activé : {ex.Message}"); }
        }
    }

    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const int SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int length, IntPtr previous, IntPtr returnLength);

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
