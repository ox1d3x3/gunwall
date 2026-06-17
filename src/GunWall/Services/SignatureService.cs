using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GunWall.Services;

public enum SignatureStatus { Unknown, Valid, Unsigned, Invalid }

/// <summary>Result of validating an executable's Authenticode signature.</summary>
public sealed record SignatureInfo(SignatureStatus Status, string Signer, string Detail)
{
    public static readonly SignatureInfo Unknown = new(SignatureStatus.Unknown, "", "");
}

/// <summary>
/// Validates an executable's Authenticode signature with WinVerifyTrust - the
/// same check Windows itself performs. Unlike merely reading the certificate's
/// name, this confirms the signature is cryptographically intact AND chains to a
/// trusted root. So a file that was modified after signing, or that carries a
/// self-signed / forged certificate, is correctly reported as Invalid instead of
/// being trusted on the strength of a name alone. Results are cached per path.
/// </summary>
public static class SignatureService
{
    private static readonly ConcurrentDictionary<string, SignatureInfo> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates the signature of <paramref name="exePath"/> (cached).</summary>
    public static SignatureInfo Verify(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return SignatureInfo.Unknown;
        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                if (!File.Exists(path)) return SignatureInfo.Unknown;

                uint result = VerifyTrust(path);

                if (result == TRUST_E_NOSIGNATURE ||
                    result == TRUST_E_SUBJECT_FORM_UNKNOWN ||
                    result == TRUST_E_PROVIDER_UNKNOWN)
                    return new SignatureInfo(SignatureStatus.Unsigned, "", "No digital signature.");

                string signer = ReadSigner(path);

                if (result == 0)
                    return new SignatureInfo(SignatureStatus.Valid,
                        string.IsNullOrWhiteSpace(signer) ? "Signed" : signer,
                        "Valid, trusted Authenticode signature.");

                // A certificate is present but the file/chain isn't trustworthy.
                return new SignatureInfo(SignatureStatus.Invalid, signer, Describe(result));
            }
            catch
            {
                return SignatureInfo.Unknown;
            }
        });
    }

    /// <summary>A short publisher label for lists ("Microsoft...", "Unsigned",
    /// "Invalid signature").</summary>
    public static string PublisherLabel(string exePath)
    {
        var s = Verify(exePath);
        return s.Status switch
        {
            SignatureStatus.Valid    => s.Signer,
            SignatureStatus.Unsigned => "Unsigned",
            SignatureStatus.Invalid  => string.IsNullOrEmpty(s.Signer) ? "Invalid signature" : $"{s.Signer} \u2014 invalid",
            _                        => "Unknown"
        };
    }

    private static string ReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the simplest signer probe
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var c2 = new X509Certificate2(cert);
            return c2.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        }
        catch { return ""; }
    }

    private static string Describe(uint code) => code switch
    {
        TRUST_E_BAD_DIGEST        => "File has been modified since it was signed.",
        CERT_E_UNTRUSTEDROOT      => "Signed, but the certificate is not from a trusted authority.",
        CERT_E_CHAINING           => "Signed, but the certificate chain is incomplete.",
        CERT_E_EXPIRED            => "The signing certificate has expired.",
        CERT_E_REVOKED            => "The signing certificate has been revoked.",
        TRUST_E_EXPLICIT_DISTRUST => "The signing certificate is explicitly distrusted.",
        CERT_E_WRONG_USAGE        => "The certificate is not valid for code signing.",
        _                         => $"The signature could not be trusted (0x{code:X8})."
    };

    // ---------------------------------------------------------- WinVerifyTrust
    private static uint VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPClientData = IntPtr.Zero,
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,   // no network round-trips
            dwUnionChoice = WTD_CHOICE_FILE,
            pUnion = pFile,
            dwStateAction = WTD_STATEACTION_VERIFY,
            hWVTStateData = IntPtr.Zero,
            pwszURLReference = null,
            dwProvFlags = WTD_SAFER_FLAG,
            dwUIContext = 0,
            pSignatureSettings = IntPtr.Zero
        };
        IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        Marshal.StructureToPtr(data, pData, false);

        try
        {
            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            uint result = unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, pData));

            // Free the state WinVerifyTrust allocated.
            var after = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            after.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(after, pData, false);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_BAD_DIGEST = 0x80096010;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_CHAINING = 0x800B010A;
    private const uint CERT_E_EXPIRED = 0x800B0101;
    private const uint CERT_E_REVOKED = 0x800B010C;
    private const uint CERT_E_WRONG_USAGE = 0x800B0110;
    private const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
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
        public IntPtr pUnion;            // union: pFile for WTD_CHOICE_FILE
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
