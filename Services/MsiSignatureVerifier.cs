using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Clockwork.Services;

internal enum MsiSignatureStatus
{
    /// <summary>A signature is present and Windows trusts it.</summary>
    Valid,

    /// <summary>The file carries no signature at all.</summary>
    Unsigned,

    /// <summary>A signature is present but invalid, tampered with or untrusted.</summary>
    Invalid,
}

internal readonly record struct MsiSignatureResult(MsiSignatureStatus Status, string? Signer);

/// <summary>
/// Verifies the Authenticode signature of an MSI file using WinVerifyTrust for complete
/// certificate chain and root trust validation, and MsiGetFileSignatureInformation to extract
/// the leaf signer identity.
/// </summary>
internal static class MsiSignatureVerifier
{
    private const uint MSI_INVALID_HASH_IS_FATAL = 0x1;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_ACTION_UNKNOWN = unchecked((int)0x800B0002);

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private static readonly Guid ActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <param name="path">The file to check.</param>
    public static MsiSignatureResult Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);

        // WinVerifyTrust validates that the file has not been modified and that the
        // Authenticode signature chains up to a trusted root authority in Windows.
        var trustResult = VerifyWinTrust(path);
        if (trustResult is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN or TRUST_E_ACTION_UNKNOWN)
            return new MsiSignatureResult(MsiSignatureStatus.Unsigned, null);

        if (trustResult != 0)
            return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);

        // MsiGetFileSignatureInformation retrieves the signer certificate context for identity checks.
        var certContext = IntPtr.Zero;
        try
        {
            var hresult = MsiGetFileSignatureInformationW(
                path,
                MSI_INVALID_HASH_IS_FATAL,
                out certContext,
                IntPtr.Zero,
                IntPtr.Zero);

            if (hresult != 0 || certContext == IntPtr.Zero)
            {
                return hresult is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN
                    ? new MsiSignatureResult(MsiSignatureStatus.Unsigned, null)
                    : new MsiSignatureResult(MsiSignatureStatus.Invalid, null);
            }

            using var cert = new X509Certificate2(certContext);

            if (!HasCodeSigningEku(cert) || !VerifyCertificateChain(cert))
                return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);

            return new MsiSignatureResult(MsiSignatureStatus.Valid, cert.Subject);
        }
        catch
        {
            return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);
        }
        finally
        {
            if (certContext != IntPtr.Zero)
                CertFreeCertificateContext(certContext);
        }
    }

    private static int VerifyWinTrust(string path)
    {
        var pFilePath = Marshal.StringToHGlobalUni(path);
        var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());

        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pFilePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = WTD_STATEACTION_IGNORE,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            return WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref trustData);
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeHGlobal(pFilePath);
        }
    }

    private static bool VerifyCertificateChain(X509Certificate2 cert)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);

            var isValid = chain.Build(cert);
            if (isValid)
                return true;

            // Allow if validation failed only due to offline/unreachable revocation endpoints,
            // or if the certificate has expired but had a valid timestamp confirmed by WinVerifyTrust.
            foreach (var status in chain.ChainStatus)
            {
                if (status.Status is not (
                    X509ChainStatusFlags.NoError or
                    X509ChainStatusFlags.RevocationStatusUnknown or
                    X509ChainStatusFlags.OfflineRevocation or
                    X509ChainStatusFlags.NotTimeValid))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasCodeSigningEku(X509Certificate2 cert)
    {
        foreach (var extension in cert.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (string.Equals(oid.Value, CodeSigningOid, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }
        }

        // If no EKU extension is present, all usages are unrestricted per RFC 5280.
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hWnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiGetFileSignatureInformationW(
        string szSignedObjectPath,
        uint dwFlags,
        out IntPtr ppcCertContext,
        IntPtr pbHashData,
        IntPtr pcbHashData);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCertificateContext(IntPtr pCertContext);
}