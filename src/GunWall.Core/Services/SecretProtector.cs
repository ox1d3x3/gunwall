using System;
using System.Security.Cryptography;
using System.Text;

namespace GunWall.Services;

/// <summary>
/// Encrypts credentials held in the profile with DPAPI at machine scope.
///
/// The profile is routinely attached to bug reports and exported to share a rule
/// set; a credential stored in it as plain text leaves with it. A machine-scope
/// blob decrypts only on the computer that produced it, so a copied profile carries
/// nothing usable. Machine scope rather than user scope because the profile is
/// machine-wide (%ProgramData%) and GunWall may be started elevated from more than
/// one account; a user-scope blob would read as lost for every account but one.
///
/// Stored form: <c>dpapi1:</c> followed by base64. The prefix distinguishes an
/// encrypted value from a plain-text one written by an earlier version, which is
/// how migration recognises what to convert. It also versions the format.
/// </summary>
internal static class SecretProtector
{
    public const string Prefix = "dpapi1:";

    // Binds blobs to this purpose: another application calling DPAPI at machine
    // scope without this entropy cannot decrypt them.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GunWall/VirusTotalApiKey/v1");

    public static bool IsProtected(string? stored) =>
        stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the stored form of <paramref name="plain"/>. Throws when DPAPI fails
    /// or the result does not decrypt back to the input: the caller must then store
    /// nothing, never the plain text as a fallback.
    /// </summary>
    public static string Protect(string plain)
    {
        byte[] data = Encoding.UTF8.GetBytes(plain);
        try
        {
            byte[] blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
            string stored = Prefix + Convert.ToBase64String(blob);
            if (!TryUnprotect(stored, out string back) || !string.Equals(back, plain, StringComparison.Ordinal))
                throw new CryptographicException("The encrypted value did not decrypt back to the original.");
            return stored;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    /// <summary>
    /// Decrypts a stored value. False when it is not in the encrypted form, is
    /// malformed, or was produced on another machine (a copied or restored
    /// profile). Never throws; the reason is not needed by any caller and the
    /// exception text is not logged.
    /// </summary>
    public static bool TryUnprotect(string? stored, out string plain)
    {
        plain = "";
        if (!IsProtected(stored)) return false;
        try
        {
            byte[] blob = Convert.FromBase64String(stored!.Substring(Prefix.Length));
            byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
            try { plain = Encoding.UTF8.GetString(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }
}
