using System.Runtime.InteropServices;
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

/// <summary>Verifies the Authenticode signature of an MSI file using MsiGetFileSignatureInformation.</summary>
internal static class MsiSignatureVerifier
{
    private const uint MSI_INVALID_HASH_IS_FATAL = 0x1;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    /// <param name="path">The file to check.</param>
    public static MsiSignatureResult Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);

        var certContext = IntPtr.Zero;

        try
        {
            var hresult = MsiGetFileSignatureInformationW(
                path,
                MSI_INVALID_HASH_IS_FATAL,
                out certContext,
                IntPtr.Zero,
                IntPtr.Zero);

            if (hresult == 0)
            {
                string? signer = null;
                if (certContext != IntPtr.Zero)
                {
                    using var cert = new X509Certificate2(certContext);
                    signer = cert.Subject;
                }

                return new MsiSignatureResult(MsiSignatureStatus.Valid, signer);
            }

            return hresult is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN
                ? new MsiSignatureResult(MsiSignatureStatus.Unsigned, null)
                : new MsiSignatureResult(MsiSignatureStatus.Invalid, null);
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