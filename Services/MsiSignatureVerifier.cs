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

/// <summary>Verifies the Authenticode signature of a downloaded file with WinVerifyTrust.</summary>
internal static class MsiSignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;

    /// <param name="path">The file to check.</param>
    /// <param name="fileHandle">
    /// Optional already-open handle for the file. Passing it makes Windows verify exactly the
    /// bytes we hold open instead of re-opening the path.
    /// </param>
    public static MsiSignatureResult Check(string path, IntPtr fileHandle = default)
    {
        var pathPointer = IntPtr.Zero;
        var filePointer = IntPtr.Zero;

        try
        {
            pathPointer = Marshal.StringToHGlobalUni(path);

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPointer,
                hFile = fileHandle,
            };

            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, filePointer, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePointer,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };

            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

            if (result == 0)
                return new MsiSignatureResult(MsiSignatureStatus.Valid, TryGetSigner(path));

            var code = unchecked((uint)result);
            return code is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN
                ? new MsiSignatureResult(MsiSignatureStatus.Unsigned, null)
                : new MsiSignatureResult(MsiSignatureStatus.Invalid, null);
        }
        catch
        {
            return new MsiSignatureResult(MsiSignatureStatus.Invalid, null);
        }
        finally
        {
            if (filePointer != IntPtr.Zero)
                Marshal.FreeHGlobal(filePointer);
            if (pathPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pathPointer);
        }
    }

    private static string? TryGetSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No non-obsolete API reads the signer certificate from a signed file.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate2 = new X509Certificate2(certificate);
            return certificate2.Subject;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

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
}
