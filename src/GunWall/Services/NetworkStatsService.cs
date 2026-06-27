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
