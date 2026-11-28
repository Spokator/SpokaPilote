using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>Appartenance au groupe local Administrateurs (S-1-5-32-544) : injectable pour les tests.</summary>
internal interface IAdministratorsMembership
{
    /// <summary>Membres directs du groupe local Administrateurs (comptes et groupes), null s'ils sont illisibles.</summary>
    IReadOnlyCollection<SecurityIdentifier>? DirectMembers();

    /// <summary>
    /// Ce compte est-il membre du groupe local Administrateurs, y compris par un groupe imbriqué (groupe de domaine
    /// « Admins du domaine » membre des Administrateurs du poste) ? Null si la question ne peut pas être tranchée (compte
    /// introuvable, contrôleur de domaine injoignable) : le compte n'est alors pas reconnu.
    /// </summary>
    bool? IsMemberIncludingNested(SecurityIdentifier sid);

    /// <summary>Compte du processus courant s'il est élevé (administrateur qui exécute SpokaPilote), sinon null.</summary>
    SecurityIdentifier? ElevatedProcessUser();
}

/// <summary>
/// Propriétaires reconnus comme administrateurs. Sur un poste où l'UAC ne demande rien, où la stratégie « Propriétaire par
/// défaut des objets créés par les membres du groupe Administrateurs » vaut « Créateur de l'objet », ou pour un fichier
/// copié par le compte administrateur d'un technicien, le propriétaire d'un fichier écrit par un administrateur est son
/// compte (PC\Pierre, DOMAINE\technicien), pas le groupe Administrateurs. Un tel compte est reconnu s'il est membre du
/// groupe local Administrateurs :
/// <list type="bullet">
/// <item>membre direct (NetLocalGroupGetMembers sur le groupe S-1-5-32-544, dont le nom est localisé) ;</item>
/// <item>compte du processus courant quand il est élevé (l'administrateur qui exécute SpokaPilote maintenant) ;</item>
/// <item>membre par un groupe imbriqué (NetUserGetLocalGroups avec LG_INCLUDE_INDIRECT : « Admins du domaine » membre
/// des Administrateurs du poste), quand le domaine répond ; hors ligne, le compte n'est pas reconnu.</item>
/// </list>
/// Seuls les comptes et groupes d'un domaine de comptes (S-1-5-21-…, du poste ou d'un domaine Active Directory) ou
/// d'Entra ID (S-1-12-1-…) peuvent l'être : jamais un groupe générique (Tout le monde, Utilisateurs authentifiés,
/// INTERACTIF, Utilisateurs, CREATOR OWNER...), même ajouté par erreur au groupe Administrateurs, ni Invité, Utilisateurs
/// du domaine, Invités du domaine ou Ordinateurs du domaine. Réponses mémorisées pour la durée du processus.
/// </summary>
internal static class AdministratorOwners
{
    private static IAdministratorsMembership _membership = WindowsAdministratorsMembership.Instance;
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Lazy<HashSet<string>?> _direct = NewDirect();

    /// <summary>RID jamais reconnus : Invité, DefaultAccount, WDAGUtilityAccount, Utilisateurs / Invités / Ordinateurs / contrôleurs du domaine.</summary>
    private static readonly HashSet<uint> ExcludedRids = [501, 503, 504, 513, 514, 515, 516];

    internal static IAdministratorsMembership Membership
    {
        get => _membership;
        set
        {
            _membership = value;
            ResetCache();
        }
    }

    internal static void ResetCache()
    {
        Cache.Clear();
        _direct = NewDirect();
    }

    private static Lazy<HashSet<string>?> NewDirect() => new(() =>
    {
        try
        {
            return _membership.DirectMembers()?.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Debug($"Membres du groupe Administrateurs illisibles : {ex.Message}");
            return null;
        }
    });

    /// <summary>
    /// Compte ou groupe d'un domaine de comptes (S-1-5-21-…) ou d'Entra ID (S-1-12-1-…), hors comptes et groupes exclus :
    /// seuls ceux-là peuvent être reconnus comme administrateurs.
    /// </summary>
    internal static bool IsEligible(SecurityIdentifier sid)
    {
        var value = sid.Value;
        if (value.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = value.Split('-');
        // S-1-5-21-a-b-c-RID : 8 parties ; un SID de domaine sans RID n'est pas un compte.
        if (parts.Length < 8 || !uint.TryParse(parts[^1], out var rid)) return false;
        return !ExcludedRids.Contains(rid);
    }

    /// <summary>Compte membre (direct, imbriqué, ou administrateur élevé courant) du groupe local Administrateurs.</summary>
    public static bool IsAdministrator(SecurityIdentifier sid)
    {
        if (!IsEligible(sid)) return false;
        return Cache.GetOrAdd(sid.Value, _ => Resolve(sid));
    }

    private static bool Resolve(SecurityIdentifier sid)
    {
        try
        {
            if (_membership.ElevatedProcessUser() is { } me && me == sid) return true;
            if (_direct.Value is { } direct && direct.Contains(sid.Value)) return true;
            return _membership.IsMemberIncludingNested(sid) == true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Appartenance de {sid.Value} au groupe Administrateurs non vérifiée : {ex.Message}");
            return false;
        }
    }

    /// <summary>« PC\Pierre (S-1-5-21-…-1001) », ou le SID seul s'il ne se traduit pas.</summary>
    public static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "propriétaire illisible";
        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch (SystemException)
        {
            return sid.Value;
        }
    }
}

/// <summary>Implémentation Windows (netapi32) de <see cref="IAdministratorsMembership"/>.</summary>
internal sealed class WindowsAdministratorsMembership : IAdministratorsMembership
{
    public static readonly WindowsAdministratorsMembership Instance = new();

    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxPreferredLength = -1;
    private const int LgIncludeIndirect = 0x1;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private readonly Lazy<string?> _groupName = new(() =>
    {
        try
        {
            // Nom localisé (« Administrateurs », « Administratoren »...) : les API Net* attendent le nom, pas le SID.
            var account = DataProtection.AdministratorsSid.Translate(typeof(NTAccount)).Value;
            var slash = account.IndexOf('\\');
            return slash >= 0 ? account[(slash + 1)..] : account;
        }
        catch (SystemException ex)
        {
            Log.Debug($"Nom du groupe Administrateurs introuvable : {ex.Message}");
            return null;
        }
    });

    public IReadOnlyCollection<SecurityIdentifier>? DirectMembers()
    {
        if (_groupName.Value is not { } group) return null;
        var members = new List<SecurityIdentifier>();
        var resume = IntPtr.Zero;
        int status;
        do
        {
            status = NetLocalGroupGetMembers(null, group, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not (NerrSuccess or ErrorMoreData)) return null;
                for (var i = 0; i < read; i++)
                {
                    var sidPointer = Marshal.ReadIntPtr(buffer, i * IntPtr.Size); // LOCALGROUP_MEMBERS_INFO_0.lgrmi0_sid
                    if (sidPointer != IntPtr.Zero) members.Add(new SecurityIdentifier(sidPointer));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (status == ErrorMoreData);
        return members;
    }

    public bool? IsMemberIncludingNested(SecurityIdentifier sid)
    {
        if (_groupName.Value is not { } group) return null;
        string account;
        try
        {
            account = sid.Translate(typeof(NTAccount)).Value; // « DOMAINE\compte » ; échoue hors ligne pour un compte inconnu du cache
        }
        catch (SystemException)
        {
            return null;
        }
        var status = NetUserGetLocalGroups(null, account, 0, LgIncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        try
        {
            if (status != NerrSuccess) return null; // groupe (pas un utilisateur), domaine injoignable...
            for (var i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)); // LOCALGROUP_USERS_INFO_0.lgrui0_name
                if (string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    public SecurityIdentifier? ElevatedProcessUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? identity.User : null;
    }
}
