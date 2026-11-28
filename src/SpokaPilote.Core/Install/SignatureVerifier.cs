using System.Runtime.InteropServices;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SpokaPilote.Core.Install;

public sealed record SignatureResult(bool IsValid, string? Signer, string Detail)
{
    public override string ToString() => IsValid ? $"Signature valide ({Signer})" : $"Signature invalide : {Detail}";
}

/// <summary>
/// Vérification Authenticode via l'API Windows WinVerifyTrust (même contrôle que l'Explorateur / SmartScreen),
/// y compris pour les catalogues de pilotes (.cat).
/// </summary>
public static class SignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureResult Verify(string filePath) => Verify(filePath, checkRevocation: false);

    /// <summary>
    /// Vérification Authenticode ; avec <paramref name="checkRevocation"/>, la révocation des certificats de la chaîne est
    /// aussi contrôlée (en ligne), avec une politique tolérante au hors-ligne : un certificat RÉVOQUÉ est refusé, mais une
    /// révocation invérifiable (poste isolé, serveur de révocation injoignable) n'empêche pas d'accepter une signature
    /// par ailleurs valide. Utilisé quand la signature est la seule vérification possible (copie de la console sans
    /// empreinte publiée) : un certificat volé puis révoqué ne suffit pas.
    /// </summary>
    public static SignatureResult Verify(string filePath, bool checkRevocation)
    {
        var signer = GetSigner(filePath);
        if (checkRevocation)
        {
            var withRevocation = WinVerifyTrustFile(filePath, revocation: true);
            switch (ClassifyRevocation(withRevocation))
            {
                case RevocationOutcome.Valid:
                    return new SignatureResult(true, signer, "OK (révocation vérifiée)");
                case RevocationOutcome.Unavailable:
                    var offline = WinVerifyTrustFile(filePath, revocation: false);
                    return offline == 0
                        ? new SignatureResult(true, signer, "OK (révocation non vérifiable : serveur de révocation injoignable)")
                        : new SignatureResult(false, signer, DescribeStatus(offline));
                default:
                    return new SignatureResult(false, signer, DescribeStatus(withRevocation));
            }
        }
        var status = WinVerifyTrustFile(filePath, revocation: false);
        if (status == 0) return new SignatureResult(true, signer, "OK");
        return new SignatureResult(false, signer, DescribeStatus(status));
    }

    internal enum RevocationOutcome { Valid, Unavailable, Rejected }

    /// <summary>
    /// Résultat d'une vérification avec contrôle de révocation : valide, révocation invérifiable (hors ligne : la
    /// signature est revérifiée sans ce contrôle) ou refus (révoqué, non signé, altéré...).
    /// </summary>
    internal static RevocationOutcome ClassifyRevocation(int status) => unchecked((uint)status) switch
    {
        0 => RevocationOutcome.Valid,
        0x800B010E or // CERT_E_REVOCATION_FAILURE : serveur de révocation hors ligne
        0x80092013 or // CRYPT_E_REVOCATION_OFFLINE
        0x80092012 => RevocationOutcome.Unavailable, // CRYPT_E_NO_REVOCATION_CHECK
        _ => RevocationOutcome.Rejected, // dont CERT_E_REVOKED (0x800B010C) et CRYPT_E_REVOKED (0x80092010)
    };

    /// <summary>Sujet du certificat du signataire (Authenticode PE/MSI ou PKCS#7 pour les .cat).</summary>
    public static string? GetSigner(string filePath)
    {
        try
        {
            if (filePath.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            {
                var cms = new SignedCms();
                cms.Decode(File.ReadAllBytes(filePath));
                return cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate?.Subject : null;
            }
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reste l'API la plus simple pour l'Authenticode PE
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeStatus(int status) => unchecked((uint)status) switch
    {
        0x800B0100 => "fichier non signé",
        0x800B0101 => "certificat expiré",
        0x800B0109 => "chaîne de certificats non approuvée",
        0x800B010C or 0x80092010 => "certificat révoqué",
        0x80096010 => "signature altérée (fichier modifié)",
        0x800B0111 => "éditeur explicitement non approuvé",
        0x80092026 => "signature non vérifiable",
        _ => $"code 0x{status:X8}",
    };

    // ------------------------------------------------------------------ P/Invoke WinVerifyTrust

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x80;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    private static int WinVerifyTrustFile(string filePath, bool revocation)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = pathPtr,
        };
        var filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, filePtr, false);

        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = WtdUiNone,
            // Par défaut, même politique que Windows pour l'Authenticode : pas de contrôle de révocation en ligne
            // (sinon faux négatifs sur les postes isolés / derrière un proxy). Contrôle demandé : toute la chaîne sauf la
            // racine, l'appelant tolérant une révocation invérifiable (voir Verify).
            fdwRevocationChecks = revocation ? WtdRevokeWholeChain : WtdRevokeNone,
            dwUnionChoice = WtdChoiceFile,
            pFile = filePtr,
            dwStateAction = WtdStateActionVerify,
            dwProvFlags = revocation ? WtdRevocationCheckChainExcludeRoot : 0,
        };
        var dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
        Marshal.StructureToPtr(data, dataPtr, false);

        try
        {
            return WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, dataPtr);
        }
        finally
        {
            data = Marshal.PtrToStructure<WinTrustData>(dataPtr);
            data.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, dataPtr, true);
            WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, dataPtr);
            Marshal.FreeCoTaskMem(dataPtr);
            Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}
