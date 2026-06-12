using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace GunWall.Services;

/// <summary>
/// Enrichment lookups for alerts and views:
///  - Authenticode publisher of an executable (a publisher signature).
///  - Reverse-DNS host name for a remote address (a resolved host name).
/// Both are cached and failure-tolerant; both are local lookups except the
/// reverse-DNS query, which goes to the user's own configured DNS server —
/// the same query the OS performs constantly. Nothing else leaves the machine.
/// </summary>
public static class NetInfoService
{
    private static readonly ConcurrentDictionary<string, string> PublisherCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> HostCache = new();

    /// <summary>Returns the signing publisher of an EXE, or "Unsigned".</summary>
    public static string GetPublisher(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "Unknown";
        return PublisherCache.GetOrAdd(exePath, path =>
        {
            try
            {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the simplest signed-file probe
                var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var cert2 = new X509Certificate2(cert);
                string name = cert2.GetNameInfo(X509NameType.SimpleName, false);
                return string.IsNullOrWhiteSpace(name) ? "Signed" : name;
            }
            catch
            {
                return "Unsigned";
            }
        });
    }

    /// <summary>
    /// Resolves a reverse-DNS host for an IP with a short timeout.
    /// Returns empty string when unknown (caller shows the bare IP).
    /// </summary>
    public static async Task<string> ResolveHostAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return "";
        if (HostCache.TryGetValue(ip, out var cached)) return cached;

        string host = "";
        try
        {
            var resolve = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(resolve, Task.Delay(1500));
            if (done == resolve) host = resolve.Result.HostName;
        }
        catch
        {
            // NXDOMAIN / timeout — perfectly normal for many IPs.
        }
        HostCache[ip] = host;
        return host;
    }
}
