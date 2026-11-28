using System.Runtime.InteropServices;
using System.Text;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>
/// Jeton d'enrôlement de la console centrale (<see cref="Settings.ServerToken"/>), hors de settings.json (lisible par tous
/// les utilisateurs du poste) : fichier <see cref="TokenFileName"/> du dossier de données, lisible des seuls SYSTEM et
/// Administrateurs (DACL protégée dès sa création), chiffré par DPAPI « machine » (une copie du fichier hors du poste,
/// sauvegarde ou image disque, est inexploitable). Un processus non élevé ne le lit pas : l'envoi à la console est le
/// fait des exécutions administrateur et des tâches SYSTEM (agent, maintenance).
/// Mode portable : le jeton reste dans settings.json sur la clé (pas d'ACL sur FAT32/exFAT, DPAPI propre à chaque poste).
/// </summary>
public static class SecretStore
{
    public const string TokenFileName = "server-token.dat";
    private static readonly byte[] Magic = "SPKT1"u8.ToArray();
    private static readonly byte[] Entropy = "SpokaPilote.ServerToken"u8.ToArray();

    public static string TokenFile => Path.Combine(AppPaths.Root, TokenFileName);

    /// <summary>Jeton hors de settings.json : tout sauf le mode portable.</summary>
    public static bool UsesTokenFile => AppPaths.Layout.Mode != DataLocationMode.Portable;

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
            if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            var token = Encoding.UTF8.GetString(Unprotect(data[Magic.Length..]));
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // processus non élevé : normal
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Jeton de la console illisible ({path}) : {ex.Message}. Reconfigurez-le : spokapilote config set ServerToken <jeton>.");
            return null;
        }
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
