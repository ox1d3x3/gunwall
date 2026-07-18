using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace GunWall.Services;

/// <summary>One row in the "top countries" view (flag is resolved in XAML from Code).</summary>
public sealed record CountryStat(string Code, string Name, int Count);

/// <summary>One row in the "most active apps" view.</summary>
public sealed record AppStat(string Name, int Count, int Countries);

/// <summary>
/// Session-scoped accounting of where external network traffic goes. It records the
/// set of distinct remote IPs ("destinations") contacted, grouped by country and by
/// application, so the Traffic panel can show the busiest of each. Loopback and
/// private/LAN addresses are ignored - only routable destinations are counted.
///
/// Deliberately depends on no GunWall model or WPF type (it takes plain strings), so
/// the aggregation logic can be compiled and unit-tested on its own. Thread-safe: the
/// snapshot loop writes while the UI reads.
/// </summary>
public sealed class NetworkStatsService
{
    private readonly object _lock = new();
    private readonly HashSet<string> _allIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _appIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _appCountries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _countryIps = new(StringComparer.OrdinalIgnoreCase);

    // §Phase 5: byte-attributed breakdown (estimated or ETW-split, caller's choice)
    private readonly Dictionary<string, long> _appBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _ipBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _typeBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _countryBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _ipApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record one observed connection. Safe to call every refresh: each distinct remote
    /// IP is only ever counted once per app/country (sets dedupe). <paramref name="country"/>
    /// may be empty on first sighting (GeoIP resolves asynchronously) and gets attributed
    /// on a later call once it is known.
    /// </summary>
    public void RecordOne(string app, string remoteIp, string country)
    {
        if (string.IsNullOrEmpty(remoteIp) || remoteIp is "0.0.0.0" or "::") return;
        if (IsLocalOrPrivate(remoteIp)) return;
        if (string.IsNullOrWhiteSpace(app)) app = "(unknown)";

        lock (_lock)
        {
            _allIps.Add(remoteIp);
            AddTo(_appIps, app, remoteIp);
            if (!string.IsNullOrEmpty(country))
            {
                AddTo(_countryIps, country, remoteIp);
                AddTo(_appCountries, app, country);
            }
        }
    }

    public int TotalDestinations { get { lock (_lock) { return _allIps.Count; } } }
    public int CountryCount { get { lock (_lock) { return _countryIps.Count; } } }
    public int AppCount { get { lock (_lock) { return _appIps.Count; } } }

    /// <summary>Countries with the most distinct destinations contacted, highest first.</summary>
    public List<(string Code, int Count)> TopCountries(int n)
    {
        lock (_lock)
        {
            return _countryIps
                .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(n).Select(kv => (kv.Key, kv.Value.Count)).ToList();
        }
    }

    /// <summary>Apps that contacted the most distinct destinations, with how many countries.</summary>
    public List<(string App, int Count, int Countries)> TopApps(int n)
    {
        lock (_lock)
        {
            return _appIps
                .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(n)
                .Select(kv => (kv.Key, kv.Value.Count,
                               _appCountries.TryGetValue(kv.Key, out var cs) ? cs.Count : 0))
                .ToList();
        }
    }

    /// <summary>
    /// Attribute <paramref name="bytes"/> of this tick's traffic to one observed
    /// connection: its app, remote host, traffic type (from port/protocol) and
    /// country all accumulate the same bytes. Local/private destinations and
    /// non-positive byte counts are ignored.
    /// </summary>
    public void RecordTraffic(string app, string remoteIp, string country, int port, bool udp, long bytes)
    {
        if (bytes <= 0) return;
        if (string.IsNullOrEmpty(remoteIp) || remoteIp is "0.0.0.0" or "::") return;
        if (IsLocalOrPrivate(remoteIp)) return;
        if (string.IsNullOrWhiteSpace(app)) app = "(unknown)";
        string type = ClassifyPort(port, udp);

        lock (_lock)
        {
            Bump(_appBytes, app, bytes);
            Bump(_ipBytes, remoteIp, bytes);
            Bump(_typeBytes, type, bytes);
            if (!string.IsNullOrEmpty(country)) Bump(_countryBytes, country, bytes);
            AddTo(_ipApps, remoteIp, app);
        }
    }

    public long TotalAttributedBytes
    {
        get { lock (_lock) { long t = 0; foreach (var v in _appBytes.Values) t += v; return t; } }
    }

    /// <summary>Apps by attributed bytes, largest first.</summary>
    public List<(string App, long Bytes)> TopAppBytes(int n) => TopOf(_appBytes, n);

    /// <summary>Traffic types by attributed bytes, largest first.</summary>
    public List<(string Type, long Bytes)> TopTypes(int n) => TopOf(_typeBytes, n);

    /// <summary>Country codes by attributed bytes, largest first.</summary>
    public List<(string Code, long Bytes)> TopCountryBytes(int n) => TopOf(_countryBytes, n);

    /// <summary>Remote hosts by attributed bytes, largest first, with how many
    /// distinct apps talked to each.</summary>
    public List<(string Ip, long Bytes, int Apps)> TopHosts(int n)
    {
        lock (_lock)
        {
            return _ipBytes
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(n)
                .Select(kv => (kv.Key, kv.Value,
                               _ipApps.TryGetValue(kv.Key, out var apps) ? apps.Count : 0))
                .ToList();
        }
    }

    /// <summary>
    /// Human name for a remote port + protocol, GlassWire-style. Unknown ports
    /// collapse into "Other" so the type list stays readable.
    /// </summary>
    public static string ClassifyPort(int port, bool udp)
    {
        return port switch
        {
            443 => udp ? "QUIC (HTTP/3)" : "HTTPS",
            80 => "HTTP",
            8080 or 8443 => "HTTP alt",
            53 => "DNS",
            853 => "DNS over TLS",
            123 => "NTP time sync",
            22 => "SSH",
            21 or 20 => "FTP",
            25 or 465 or 587 => "Mail (SMTP)",
            993 or 143 => "Mail (IMAP)",
            995 or 110 => "Mail (POP3)",
            3478 or 3479 or 3480 or 3481 or 19302 => "Voice/Video (STUN)",
            1194 => "VPN (OpenVPN)",
            51820 => "VPN (WireGuard)",
            500 or 4500 => "VPN (IPsec)",
            1723 => "VPN (PPTP)",
            3389 => "Remote Desktop",
            445 or 139 => "File sharing (SMB)",
            1900 => "Device discovery (SSDP)",
            5353 => "Device discovery (mDNS)",
            >= 6881 and <= 6889 => "BitTorrent",
            9993 => "ZeroTier",
            _ => "Other"
        };
    }

    private static void Bump(Dictionary<string, long> map, string key, long by)
    {
        map.TryGetValue(key, out long cur);
        map[key] = cur + by;
    }

    private List<(string, long)> TopOf(Dictionary<string, long> map, int n)
    {
        lock (_lock)
        {
            return map
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    private static void AddTo(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[key] = set;
        }
        set.Add(value);
    }

    /// <summary>True for loopback, RFC1918, link-local, CGNAT and IPv6 ULA/link-local.</summary>
    private static bool IsLocalOrPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return true;
        byte[] b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 0 || b[0] == 127) return true;                      // this-host / loopback
            if (b[0] == 10) return true;                                    // 10.0.0.0/8
            if (b[0] == 192 && b[1] == 168) return true;                    // 192.168.0.0/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;       // 172.16.0.0/12
            if (b[0] == 169 && b[1] == 254) return true;                    // 169.254.0.0/16 link-local
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;      // 100.64.0.0/10 CGNAT
            return false;
        }
        if (IPAddress.IsLoopback(a)) return true;                           // ::1
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;             // fe80::/10 link-local
        if ((b[0] & 0xfe) == 0xfc) return true;                             // fc00::/7 unique-local
        return false;
    }
}
