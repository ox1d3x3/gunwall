using System.Security.Cryptography;
using System.Text;

namespace GunWall.Services;

// =============================================================================
//  ServiceSidService.cs
//
//  Works out the security identifier Windows assigns to a named service.
//
//  This is what makes a per-service firewall rule possible. Several dozen
//  Windows services share a handful of svchost.exe processes, so a rule written
//  against the executable applies to all of them at once: blocking svchost to
//  stop telemetry also stops Windows Update, DHCP and time synchronisation.
//  Filtering on the service's own identity instead separates them.
//
//  Every service runs with an additional SID of the form
//      S-1-5-80-{five 32-bit values}
//  derived deterministically from its name: uppercase it, encode as UTF-16LE,
//  take the SHA-1, and read the 20 result bytes as five little-endian 32-bit
//  values. Because it is a pure function of the name, it can be computed
//  without asking the system and verified against documented values in a test -
//  which matters here, since a wrong SID produces a filter that silently
//  matches nothing rather than an error.
// =============================================================================

public static class ServiceSidService
{
    /// <summary>
    /// The service SID for a service name, or "" if the name is unusable.
    /// Deterministic: no system call, no privileges, same answer everywhere.
    /// </summary>
    public static string SidForServiceName(string? serviceName)
    {
        string name = (serviceName ?? "").Trim();
        if (name.Length == 0) return "";

        // Windows uppercases with the invariant culture before hashing; using a
        // local culture here would produce a different SID for names containing
        // letters such as 'i' under a Turkish locale.
        byte[] hash = SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        if (hash.Length != 20) return "";

        var sb = new StringBuilder("S-1-5-80");
        for (int i = 0; i < 20; i += 4)
        {
            uint part = (uint)(hash[i] | (hash[i + 1] << 8) | (hash[i + 2] << 16) | (hash[i + 3] << 24));
            sb.Append('-').Append(part);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A security descriptor, in SDDL form, that identifies exactly this
    /// service. WFP compares the token of the process making a connection
    /// against this descriptor; a service's token carries its own SID, so only
    /// that service satisfies it even when the process hosts many.
    ///
    /// "A;;CC;;;{sid}" grants the one right WFP inspects to that SID alone.
    /// The owner and group are set to Local System because a descriptor with no
    /// owner is rejected.
    /// </summary>
    public static string SddlForServiceName(string? serviceName)
    {
        string sid = SidForServiceName(serviceName);
        return sid.Length == 0 ? "" : $"O:SYG:SYD:(A;;CC;;;{sid})";
    }

    /// <summary>
    /// Whether a string looks like a service SID this class would produce.
    /// Used to sanity-check anything read back from stored rules.
    /// </summary>
    public static bool LooksLikeServiceSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        if (!sid.StartsWith("S-1-5-80-", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = sid.Split('-');
        if (parts.Length != 9) return false;                 // S 1 5 80 + five values
        for (int i = 4; i < 9; i++)
            if (!uint.TryParse(parts[i], out _)) return false;
        return true;
    }

    /// <summary>
    /// The account name Windows shows for a service identity, e.g.
    /// "NT SERVICE\\Dnscache". Display only.
    /// </summary>
    public static string AccountNameFor(string? serviceName)
    {
        string n = (serviceName ?? "").Trim();
        return n.Length == 0 ? "" : @"NT SERVICE\" + n;
    }
}
