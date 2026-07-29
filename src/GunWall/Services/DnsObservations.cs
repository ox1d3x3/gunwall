namespace GunWall.Services;

// =============================================================================
//  DnsObservations.cs
//
//  One shared memory of "which name produced which address".
//
//  Two features depend on this and neither can work without it:
//    - domain rules, which must match a NAME against a connection that only
//      carries an ADDRESS;
//    - "block direct connections", which asks whether an address was ever
//      looked up by name at all.
//
//  Until now the only source was GunWall's own resolver, which meant both
//  features were blind unless the machine's DNS was pointed at it. That
//  redirection was removed in 0.95.0, so the memory is now fed from two
//  independent places instead:
//
//    1. the built-in resolver, for anything deliberately pointed at it;
//    2. a passive ETW observer of the Windows DNS client, which sees every
//       lookup the machine makes without intercepting or altering any of it.
//
//  The second is strictly better behaved than redirection: nothing is claimed,
//  nothing is rewritten, and it cannot conflict with security software or a VPN
//  over ownership of DNS.
// =============================================================================

public static class DnsObservations
{
    /// <summary>Where an observation came from, for diagnostics.</summary>
    public enum Source { Resolver, SystemObserver }

    private static readonly object _gate = new();

    // IPv4 address (host order) -> most recent name that resolved to it.
    private static readonly Dictionary<uint, string> _v4 = new();

    // IPv6 is kept as text: it is far less common on these paths and the extra
    // machinery of a 128-bit key isn't worth it.
    private static readonly Dictionary<string, string> _v6 = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxEntries = 8192;

    private static long _fromResolver, _fromObserver, _names;

    public static long RecordedByResolver => Interlocked.Read(ref _fromResolver);
    public static long RecordedByObserver => Interlocked.Read(ref _fromObserver);
    public static long NamesSeen => Interlocked.Read(ref _names);

    public static int Count { get { lock (_gate) return _v4.Count + _v6.Count; } }

    /// <summary>True when anything at all has been observed, i.e. when domain
    /// rules and direct-connection detection have data to work with.</summary>
    public static bool HasData => Count > 0;

    public static void Clear()
    {
        lock (_gate) { _v4.Clear(); _v6.Clear(); }
    }

    /// <summary>
    /// Records that <paramref name="name"/> resolved to these addresses.
    /// Last writer wins: a shared CDN address should carry the name most
    /// recently asked for, which is the one an app is about to connect to.
    /// </summary>
    public static void Record(string? name, IEnumerable<uint>? v4, IEnumerable<string>? v6, Source source)
    {
        string n = Normalize(name);
        if (n.Length == 0) return;

        bool recorded = false;
        lock (_gate)
        {
            // A single flush is simpler and safer than an LRU here: the memory
            // exists to answer "recently", and rebuilding it costs nothing but
            // a little time.
            if (_v4.Count + _v6.Count > MaxEntries) { _v4.Clear(); _v6.Clear(); }

            if (v4 != null)
                foreach (uint ip in v4)
                    if (ip != 0) { _v4[ip] = n; recorded = true; }

            if (v6 != null)
                foreach (string ip in v6)
                {
                    string k = (ip ?? "").Trim();
                    if (k.Length > 0) { _v6[k] = n; recorded = true; }
                }
        }

        if (!recorded) return;
        Interlocked.Increment(ref _names);
        if (source == Source.Resolver) Interlocked.Increment(ref _fromResolver);
        else Interlocked.Increment(ref _fromObserver);
    }

    /// <summary>The name this address was resolved from, or "" if unknown.</summary>
    public static string DomainForIp(string? ip)
    {
        string s = (ip ?? "").Trim();
        if (s.Length == 0) return "";
        if (!System.Net.IPAddress.TryParse(s, out var addr)) return "";

        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            uint v = ToUInt32(addr);
            lock (_gate) return _v4.TryGetValue(v, out var n) ? n : "";
        }

        // An IPv4-mapped IPv6 address is the same host; check both forms.
        if (addr.IsIPv4MappedToIPv6)
        {
            uint v = ToUInt32(addr.MapToIPv4());
            lock (_gate) if (_v4.TryGetValue(v, out var n4)) return n4;
        }
        lock (_gate) return _v6.TryGetValue(addr.ToString(), out var n6) ? n6 : "";
    }

    /// <summary>Whether this address was ever handed out in a DNS answer.</summary>
    public static bool WasResolved(string? ip) => DomainForIp(ip).Length > 0;

    /// <summary>Lower-cased, trailing dot removed; "" when unusable.</summary>
    private static string Normalize(string? name)
    {
        string n = (name ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        // A root or wildcard answer tells us nothing useful about a host.
        return n is "" or "*" ? "" : n;
    }

    private static uint ToUInt32(System.Net.IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }
}
