using System.Runtime.InteropServices;
using System.Text;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Jeton d'enrôlement de la console centrale (<see cref="Settings.ServerToken"/>), hors de settings.json (lisible par tous
/// les utilisateurs du poste) : fichier <see cref="TokenFileName"/> du dossier de données, lisible des seuls SYSTEM et
/// Administrateurs (DACL protégée dès sa création). C'est cette DACL qui le protège des utilisateurs du poste : le
/// chiffrement DPAPI « machine » (CRYPTPROTECT_LOCAL_MACHINE) peut être levé par n'importe quel compte du même poste.
/// DPAPI rend inexploitable une copie du fichier SEUL (sauvegarde de fichiers, copie vers un autre PC), mais pas une
/// image disque complète ou une sauvegarde système : avec les ruches SYSTEM et SECURITY du poste, la clé DPAPI machine se
/// retrouve hors ligne. Protégez ces sauvegardes comme le poste lui-même, et révoquez le jeton (console) en cas de doute.
/// Un processus non élevé ne le lit pas : l'envoi à la console est le fait des exécutions administrateur et des tâches
/// SYSTEM (agent, maintenance).
/// Image de référence (sysprep) ou poste cloné : la conservation de la clé DPAPI machine par « sysprep /generalize »
/// n'est pas documentée. Un jeton indéchiffrable est signalé, supprimé, et le poste continue sans console jusqu'à ce
/// que le jeton soit redéployé (kit de déploiement, Intune) : ne l'intégrez jamais à une image de référence.
/// Mode portable et emplacement sans ACL (<see cref="DataProtection.UnprotectedLocation"/>) : le jeton reste dans
/// settings.json (pas d'ACL sur FAT32/exFAT, DPAPI propre à chaque poste).
/// </summary>
public static class SecretStore
{
    public const string TokenFileName = "server-token.dat";
    private static readonly byte[] Magic = "SPKT1"u8.ToArray();
    private static readonly byte[] Entropy = "SpokaPilote.ServerToken"u8.ToArray();

    public static string TokenFile => Path.Combine(AppPaths.Root, TokenFileName);

    /// <summary>Jeton hors de settings.json : tout sauf le mode portable et un emplacement sans ACL.</summary>
    public static bool UsesTokenFile => AppPaths.Layout.Mode != DataLocationMode.Portable && !DataProtection.UnprotectedLocation;

    /// <summary>Jeton enregistré, null s'il est absent ou illisible (processus non élevé, fichier non fiable).</summary>
    public static string? ReadToken() => ReadToken(TokenFile);

    internal static string? ReadToken(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!DataProtection.IsTrustedForElevatedUse(path))
            {
                Log.Warn($"{path} n'appartient pas aux administrateurs : jeton ignoré.");
                return null;
            }
            var data = File.ReadAllBytes(path);
            if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                DiscardUnreadable(path, "format inconnu");
                return null;
            }
            byte[] plain;
            try
            {
                plain = Unprotect(data[Magic.Length..]);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Clé DPAPI machine différente (poste cloné depuis une image de référence, fichier copié d'un autre poste) ou
                // fichier altéré : le blob ne sera jamais lisible ici.
                DiscardUnreadable(path, ex.Message);
                return null;
            }
            var token = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // processus non élevé : normal
        }
        catch (IOException ex)
        {
            Log.Warn($"Jeton de la console illisible ({path}) : {ex.Message}.");
            return null;
        }
    }

    /// <summary>
    /// Jeton contenu dans ce fichier (server-token.dat mis en quarantaine, déjà lu en mémoire). Null si le format est
    /// inconnu ou le jeton vide ; Win32Exception s'il est indéchiffrable sur ce poste.
    /// </summary>
    internal static string? Decode(byte[] data)
    {
        if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var token = Encoding.UTF8.GetString(Unprotect(data[Magic.Length..]));
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>Jeton indéchiffrable sur ce poste : erreur claire, fichier supprimé (le poste fonctionne sans console).</summary>
    private static void DiscardUnreadable(string path, string reason)
    {
        Log.Error($"Jeton de la console indéchiffrable sur ce poste ({path} : {reason}). Cas typique : poste cloné depuis une image de " +
                  "référence qui contenait le jeton, ou fichier copié d'un autre poste. Fichier supprimé ; le poste n'envoie plus rien " +
                  "à la console tant que le jeton n'est pas redéployé (kit de déploiement, Intune, ou « spokapilote config set ServerToken - »).");
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"{path} non supprimé : {ex.Message}"); }
    }

    /// <summary>Enregistre (ou supprime si vide) le jeton. Lève une exception en cas d'échec (droits insuffisants).</summary>
    public static void WriteToken(string? token) => WriteToken(TokenFile, token);

    internal static void WriteToken(string path, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var blob = Protect(Encoding.UTF8.GetBytes(token.Trim()));
        DataProtection.WriteFileAtomically(path, [.. Magic, .. blob], privateFile: true);
    }

    // ------------------------------------------------------------------ DPAPI (portée machine)

    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    internal static byte[] Unprotect(byte[] blob) => Transform(blob, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden | CryptProtectLocalMachine, out var dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dataOut);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                return output;
            }
            finally
            {
                LocalFree(dataOut.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
