using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace GunWall.Services;

/// <summary>
/// Read-only GeoIP enrichment: maps a remote IPv4 address to its country code,
/// ASN, and AS owner. Backed by a sorted range table loaded from a local cache
/// file in the iptoasn.com TSV format ("start end asn country owner"), which is
/// published free and clear of any licence (CC0-style).
///
/// IMPORTANT: there is no enforcement here. This only enriches what the user
/// sees in the connection list. Country/ASN *rules* (actual blocking) arrive with
/// the entity rule engine - keeping this module low-risk and side-effect-free.
///
/// The lookup path is pure logic (no I/O, no WPF), so it is unit-testable
/// off-device; only the download/load helpers touch the disk and network.
/// </summary>
public sealed class GeoIpService
{
    public readonly struct GeoInfo
    {
        public GeoInfo(string country, int asn, string owner)
        {
            Country = country ?? "";
            Asn = asn;
            Owner = owner ?? "";
        }

        public string Country { get; }
        public int Asn { get; }
        public string Owner { get; }
        public bool HasData => Country.Length > 0 || Asn != 0;
    }

    // Parallel, sorted-by-start arrays for cache-friendly binary search.
    // IPv6, added in 0.99.106. Every v6 destination previously resolved to nothing
    // - the table was v4-only - so the map, the country tables and the Connections
    // LOCATION column all under-reported by however much of a machine's traffic
    // happens to be v6. On a network with working IPv6 that can be most of it.
    //
    // UInt128 rather than a pair of ulongs: .NET 8 has it, it compares correctly
    // without hand-written carry logic, and hand-written comparison of a 128-bit
    // value split across two words is exactly the kind of arithmetic that is wrong
    // once and then wrong forever in a table nobody re-reads.
    /// <summary>
    /// Each table is ONE object, installed by one reference write. Until 0.99.158
    /// a table was five fields written one after another; a lookup landing between
    /// two writes saw new starts with old ends - a wrong answer, or an index past the
    /// end of an older, shorter array. Harmless while loads happened only on a rare
    /// background refresh; not once the startup load moved off the UI thread and ran
    /// alongside every snapshot's lookups. A lookup reads the reference once.
    /// </summary>
    private sealed record Table6(UInt128[] Start, UInt128[] End, int[] Asn, string[] Country, string[] Owner)
    {
        public static readonly Table6 Empty = new(Array.Empty<UInt128>(), Array.Empty<UInt128>(),
            Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>());
    }
    private volatile Table6 _t6 = Table6.Empty;

    /// <summary>True when the IPv6 table has been loaded as well as the v4 one.</summary>
    public bool LoadedV6 => _t6.Start.Length > 0;
    public int RangeCountV6 => _t6.Start.Length;

    /// <summary>Packs an IPv6 address into a comparable 128-bit integer, most
    /// significant byte first, matching the ordering the table is sorted in.</summary>
    private static bool TryToUInt128(string ip, out UInt128 value) => TryToUInt128(ip.AsSpan(), out value);

    /// <summary>Same parse as before, without the per-call byte array: the address
    /// bytes are written into a stack buffer.</summary>
    private static bool TryToUInt128(ReadOnlySpan<char> ip, out UInt128 value)
    {
        value = default;
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return false;
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        Span<byte> b = stackalloc byte[16];
        if (!a.TryWriteBytes(b, out int written) || written != 16) return false;
        UInt128 v = 0;
        foreach (byte x in b) v = (v << 8) | x;
        value = v;
        return true;
    }

    /// <summary>The IPv4 table: one object, one reference write - see Table6.</summary>
    private sealed record Table4(uint[] Start, uint[] End, int[] Asn, string[] Country, string[] Owner)
    {
        public static readonly Table4 Empty = new(Array.Empty<uint>(), Array.Empty<uint>(),
            Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>());
    }
    private volatile Table4 _t4 = Table4.Empty;

    public bool Loaded => _t4.Start.Length > 0;
    public int RangeCount => _t4.Start.Length;

    // ---- optional self-hosted API mode (iptoasn-webservice) ----
    // When _apiBase is set, Lookup() resolves via the user's HTTP API instead of a
    // local table. Lookups are async + cached so the hot path NEVER blocks: a cache
    // miss returns "no data" immediately and kicks off a background fetch that fills
    // the cache for the next refresh cycle. Each unique IP is fetched at most once
    // (until a failure's short retry window elapses). Resolves IPv6 too, because the
    // server's combined database includes v6 ranges.
    private string _apiBase = "";
    private long _apiOkCount, _apiFailCount;

    /// <summary>Successful / failed API fetches this session (diagnostics).</summary>
    public long ApiOkCount => System.Threading.Interlocked.Read(ref _apiOkCount);
    public long ApiFailCount => System.Threading.Interlocked.Read(ref _apiFailCount);
    private static readonly HttpClient _apiClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeoInfo> _apiCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _apiInflight = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _apiRetryAt = new();
    private readonly System.Threading.SemaphoreSlim _apiGate = new(6); // cap concurrent requests

    public bool ApiEnabled => _apiBase.Length > 0;
    public int ApiCacheCount => _apiCache.Count;

    /// <summary>Switch to API-source mode against a base URL (e.g. http://host:53662).</summary>
    public void EnableApi(string baseUrl)
    {
        _apiBase = NormalizeBase(baseUrl);
        _apiCache.Clear(); _apiInflight.Clear(); _apiRetryAt.Clear();
    }

    /// <summary>Leave API-source mode (back to the local table, if any).</summary>
    public void DisableApi() => _apiBase = "";

    private static string NormalizeBase(string url)
    {
        url = (url ?? "").Trim();
        while (url.EndsWith("/")) url = url[..^1];
        return url;
    }

    /// <summary>The v6 half of Lookup. Same binary search, 128-bit keys.</summary>
    private GeoInfo LookupV6(string ip)
    {
        if (!LoadedV6) return new GeoInfo("", 0, "");
        if (!TryToUInt128(ip, out UInt128 addr)) return new GeoInfo("", 0, "");

        var t = _t6;   // one read: every array below from the same table
        int lo = 0, hi = t.Start.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (t.Start[mid] <= addr) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0 || addr > t.End[found]) return new GeoInfo("", 0, "");
        return new GeoInfo(t.Country[found], t.Asn[found], t.Owner[found]);
    }

    /// <summary>Parse an iptoasn IPv6 TSV into the v6 lookup arrays.
    ///
    /// The v6 file gives the range bounds as ADDRESSES ("2001:200::" ...) rather
    /// than as the pre-packed integers the v4-u32 file uses, so each bound is
    /// parsed rather than read as a number. Rows that do not parse are skipped
    /// rather than guessed at - a wrong range would attribute traffic to the wrong
    /// country silently, which is worse than showing nothing.</summary>
    public void LoadV6FromText(string tsv)
    {
        using var reader = new StringReader(tsv);
        LoadV6FromReader(reader);
    }

    private void LoadV6FromReader(TextReader reader)
    {
        var pool = new SpanPool();
        var starts = new List<UInt128>();
        var ends = new List<UInt128>();
        var asns = new List<int>();
        var countries = new List<string>();
        var owners = new List<string>();

        ReadLines(reader, line =>
        {
            if (line.Length == 0) return;
            // Fields: start \t end \t asn \t country [\t owner] - read in place from
            // the line, with no array and no string per field (see SplitTsv).
            if (!SplitTsv(line, out var f0, out var f1, out var f2, out var f3, out var f4)) return;
            if (!TryToUInt128(f0, out UInt128 s6)) return;
            if (!TryToUInt128(f1, out UInt128 e6)) return;
            if (e6 < s6) return;
            int.TryParse(f2, out int asn);
            // The same placeholders the v4 loader has always dropped. Missing here
            // until 0.99.172, so an unrouted v6 range answered with the country
            // "None" - and was counted and shown as if that were a country.
            if (f3.SequenceEqual("None") || f3.SequenceEqual("Unknown") || f3.SequenceEqual("-")) f3 = default;
            starts.Add(s6); ends.Add(e6); asns.Add(asn);
            countries.Add(pool.Get(f3));
            owners.Add(pool.Get(f4));
        });

        // Sorted by start, because the lookup is a binary search and the file's
        // ordering is not something to take on trust.
        int n = starts.Count;
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => starts[x].CompareTo(starts[y]));

        var ns = new UInt128[n]; var ne = new UInt128[n]; var na = new int[n];
        var nc = new string[n]; var no = new string[n];
        for (int i = 0; i < n; i++)
        {
            int k = idx[i];
            ns[i] = starts[k]; ne[i] = ends[k]; na[i] = asns[k];
            nc[i] = countries[k]; no[i] = owners[k];
        }
        _t6 = new Table6(ns, ne, na, nc, no);   // one write: see Table6
    }

    /// <summary>Parse the iptoasn TSV text into the sorted lookup arrays.</summary>
    public void LoadFromText(string tsv)
    {
        using var reader = new StringReader(tsv);
        LoadFromReader(reader);
    }

    private void LoadFromReader(TextReader reader)
    {
        var pool = new SpanPool();
        var starts = new List<uint>();
        var ends = new List<uint>();
        var asns = new List<int>();
        var countries = new List<string>();
        var owners = new List<string>();

        ReadLines(reader, line =>
        {
            if (line.Length == 0) return;
            // Fields: start \t end \t asn \t country [\t owner] - see SplitTsv.
            if (!SplitTsv(line, out var f0, out var f1, out var f2, out var cc, out var ow)) return;
            if (!uint.TryParse(f0, out uint s)) return;
            if (!uint.TryParse(f1, out uint e)) return;
            if (e < s) return;
            int.TryParse(f2, out int a);
            if (cc.SequenceEqual("None") || cc.SequenceEqual("Unknown") || cc.SequenceEqual("-")) cc = default;

            starts.Add(s); ends.Add(e); asns.Add(a); countries.Add(pool.Get(cc)); owners.Add(pool.Get(ow));
        });

        int n = starts.Count;
        // The published dataset is sorted by start, but sort defensively anyway.
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => starts[x].CompareTo(starts[y]));

        var ns = new uint[n]; var ne = new uint[n]; var na = new int[n];
        var nc = new string[n]; var no = new string[n];
        for (int i = 0; i < n; i++)
        {
            int j = idx[i];
            ns[i] = starts[j]; ne[i] = ends[j]; na[i] = asns[j];
            nc[i] = countries[j]; no[i] = owners[j];
        }

        _t4 = new Table4(ns, ne, na, nc, no);   // one write: see Table6
    }

    /// <summary>Look up a remote address. Unknown / IPv6 / invalid returns empty.</summary>
    public GeoInfo Lookup(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return new GeoInfo("", 0, "");
        if (IsPrivateOrReserved(ip)) return new GeoInfo("", 0, ""); // LAN/loopback/reserved: no public answer
        if (_apiBase.Length > 0) return LookupApi(ip);  // self-hosted API mode (async + cached)
        // v6 FIRST, and independently of the v4 table's state. Written the other
        // way round at first, which gated every v6 answer behind `Loaded` - a v4
        // flag - so a machine holding only the v6 table would have resolved
        // nothing while reporting a loaded database.
        if (!TryToUInt32(ip, out uint addr)) return LookupV6(ip);
        if (!Loaded) return new GeoInfo("", 0, "");

        // Greatest start <= addr, then confirm addr falls within that range's end.
        var t = _t4;   // one read: every array below from the same table
        int lo = 0, hi = t.Start.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (t.Start[mid] <= addr) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found >= 0 && addr <= t.End[found])
            return new GeoInfo(t.Country[found], t.Asn[found], t.Owner[found]);
        return new GeoInfo("", 0, "");
    }

    private static bool TryToUInt32(string ip, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false; // IPv4 table only
        byte[] b = addr.GetAddressBytes(); // 4 bytes, network order
        value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    // -------- I/O helpers (runtime-only; confirmed by the user's build) --------

    // Both loads stream the file line by line. They used File.ReadAllText, which
    // holds the whole dataset as one UTF-16 string - twice the file's size -
    // before parsing begins; measured on data shaped like the real files, the
    // load peaked at 363 MB of working set and allocated 728 MB in passing.
    // The reader permits concurrent replacement (FileShare.Delete), so a
    // refreshed database can be moved into place while a load is under way,
    // exactly as when the file was read in one call.
    public void LoadFromFile(string path)
    {
        if (!File.Exists(path)) return;
        using var reader = OpenShared(path);
        LoadFromReader(reader);
    }

    public void LoadV6FromFile(string path)
    {
        if (!File.Exists(path)) return;
        using var reader = OpenShared(path);
        LoadV6FromReader(reader);
    }

    /// <summary>
    /// Hands memory freed by a table load back to Windows - once, straight after it.
    ///
    /// Loading both tables allocates around half a gigabyte of short-lived data:
    /// lines, split fields, list growth, all garbage moments later. The runtime
    /// frees it internally but keeps the pages committed. Measured on data shaped
    /// like the real files, the working set after the process had settled for
    /// several seconds was the same as immediately after the load - nothing was
    /// returned on its own. One aggressive collection returns roughly 90 MB of it
    /// for about 50 ms of pause.
    ///
    /// Forced collections are normally a mistake. This is the case they exist
    /// for: one known, bounded burst of temporary data in an otherwise steady,
    /// long-lived process. Called after a load, never on a schedule.
    ///
    /// Returns the working set before and after, and the pause, so the effect is
    /// in the diagnostics rather than taken on trust.
    /// </summary>
    public static (double BeforeMb, double AfterMb, long Ms) ReturnFreedMemory()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            double before = p.WorkingSet64 / 1048576.0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            sw.Stop();
            p.Refresh();
            return (before, p.WorkingSet64 / 1048576.0, sw.ElapsedMilliseconds);
        }
        catch (Exception) { return (0, 0, 0); }   /* housekeeping must never fail a load */
    }

    private static StreamReader OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1 << 16),
            detectEncodingFromByteOrderMarks: true);

    /// <summary>
    /// The fields of one iptoasn line, as views into the line - no array, no
    /// string per field. Matches what <c>line.Split('\t')</c> gave: false when there
    /// are fewer than four fields; the fifth field (owner) is empty when absent and
    /// stops at the next tab when there are more than five.
    ///
    /// Splitting was most of the load's garbage: about 518 MB allocated for a few
    /// tens of megabytes kept, nearly all of it a string array and five strings per
    /// line for more than 700,000 lines.
    /// </summary>
    private static bool SplitTsv(ReadOnlySpan<char> line,
        out ReadOnlySpan<char> f0, out ReadOnlySpan<char> f1, out ReadOnlySpan<char> f2,
        out ReadOnlySpan<char> f3, out ReadOnlySpan<char> f4)
    {
        f0 = f1 = f2 = f3 = f4 = default;
        int t0 = line.IndexOf('\t');
        if (t0 < 0) return false;
        int t1 = Next(line, t0);
        if (t1 < 0) return false;
        int t2 = Next(line, t1);
        if (t2 < 0) return false;                       // fewer than four fields
        int t3 = Next(line, t2);                        // -1: no owner field
        f0 = line[..t0];
        f1 = line[(t0 + 1)..t1];
        f2 = line[(t1 + 1)..t2];
        if (t3 < 0) { f3 = line[(t2 + 1)..]; return true; }
        f3 = line[(t2 + 1)..t3];
        int t4 = Next(line, t3);                        // a sixth field ends the owner
        f4 = t4 < 0 ? line[(t3 + 1)..] : line[(t3 + 1)..t4];
        return true;

        static int Next(ReadOnlySpan<char> l, int after)
        {
            int k = l[(after + 1)..].IndexOf('\t');
            return k < 0 ? -1 : after + 1 + k;
        }
    }

    private delegate void LineHandler(ReadOnlySpan<char> line);

    /// <summary>
    /// Calls <paramref name="handle"/> with each line, as a view into a reusable
    /// buffer. Same line breaks as TextReader.ReadLine - "\n", "\r" and "\r\n",
    /// a last line without a break included, nothing after a final break - but no
    /// string per line: on these files that was the largest allocation left once
    /// the splitting stopped allocating. The span is valid only during the call.
    /// </summary>
    private static void ReadLines(TextReader reader, LineHandler handle)
    {
        char[] buf = new char[1 << 16];
        int start = 0, end = 0;
        bool skipLf = false;   // the last line ended in '\r': a following '\n' belongs to it
        while (true)
        {
            if (skipLf && start < end)
            {
                if (buf[start] == '\n') start++;
                skipLf = false;
            }
            int nl = start < end ? buf.AsSpan(start, end - start).IndexOfAny('\r', '\n') : -1;
            if (nl >= 0)
            {
                int at = start + nl;
                handle(new ReadOnlySpan<char>(buf, start, at - start));
                skipLf = buf[at] == '\r';
                start = at + 1;
                continue;
            }
            // No break in what is buffered: keep the partial line, make room, read on.
            if (start > 0)
            {
                Array.Copy(buf, start, buf, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            int n = reader.Read(buf, end, buf.Length - end);
            if (n == 0)
            {
                if (end > start) handle(new ReadOnlySpan<char>(buf, start, end - start));
                return;
            }
            end += n;
        }
    }

    /// <summary>
    /// One shared string per distinct value, looked up by the characters
    /// themselves, so a value already seen allocates nothing.
    ///
    /// Every range used to keep its own copy of its country and owner - the same
    /// "US" and the same owner name held hundreds of thousands of times - against a
    /// few hundred countries and tens of thousands of owners. Values are compared
    /// ordinally and returned unchanged, so every lookup answers exactly as before.
    /// </summary>
    private sealed class SpanPool
    {
        // Hash -> a string, or a List<string> when distinct values share a hash.
        private readonly Dictionary<int, object> _byHash = new();

        public string Get(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty) return "";
            int h = string.GetHashCode(value, StringComparison.Ordinal);
            if (_byHash.TryGetValue(h, out object? found))
            {
                if (found is string one)
                {
                    if (value.SequenceEqual(one)) return one;
                    string added = new(value);
                    _byHash[h] = new List<string> { one, added };
                    return added;
                }
                var list = (List<string>)found;
                foreach (string candidate in list)
                    if (value.SequenceEqual(candidate)) return candidate;
                string extra = new(value);
                list.Add(extra);
                return extra;
            }
            string first = new(value);
            _byHash[h] = first;
            return first;
        }
    }

    /// <summary>
    /// Download + gunzip the free CC0 iptoasn IPv4 dataset to the given cache path.
    /// Returns the number of bytes written. Throws on network/IO failure (callers
    /// run this off the UI thread and surface a friendly message).
    /// </summary>
    /// <summary>iptoasn.com, pinned. Checked before the request and again on
    /// whatever answered after redirects, exactly as the updater and the IEEE
    /// registry download already do. This file is read back as authority for
    /// which country an address belongs to, so where it came from matters.</summary>
    public const string DatabaseHost = "iptoasn.com";

    public static long DownloadDatabase(string destTsvPath)
        => DownloadDatabaseAsync(destTsvPath).GetAwaiter().GetResult();

    public static Task<long> DownloadDatabaseAsync(string destTsvPath,
                                                   CancellationToken ct = default)
        => FetchAsync("https://iptoasn.com/data/ip2asn-v4-u32.tsv.gz", destTsvPath, ct);

    /// <summary>Downloads the IPv6 half of the same CC0 dataset.
    ///
    /// A separate call rather than a flag, so a v6 failure cannot take the v4
    /// table down with it: the caller keeps whatever it managed to get. Losing v6
    /// coverage means some destinations show no country; losing v4 as well would
    /// mean almost none do.</summary>
    public static long DownloadDatabaseV6(string destTsvPath)
        => DownloadDatabaseV6Async(destTsvPath).GetAwaiter().GetResult();

    public static Task<long> DownloadDatabaseV6Async(string destTsvPath,
                                                     CancellationToken ct = default)
        => FetchAsync("https://iptoasn.com/data/ip2asn-v6.tsv.gz", destTsvPath, ct);

    /// <summary>
    /// Fetches and decompresses one half of the dataset over a pinned host, into
    /// a validated temporary file that replaces the destination only on success.
    ///
    /// The previous version did none of that. It took the URL unchecked, followed
    /// redirects without re-examining who answered, and opened the DESTINATION
    /// with File.Create - which truncates before the first byte arrives. A
    /// connection dropped at 80% left a partial country table where a working one
    /// had been, and it loaded without complaint.
    /// </summary>
    private static async Task<long> FetchAsync(string url, string destTsvPath,
                                               CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            u.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(u.Host, DatabaseHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to download the GeoIP database from '{url}': it is not "
                + $"https on {DatabaseHost}.");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.Add("User-Agent", "GunWall");

        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        string finalHost = resp.RequestMessage?.RequestUri?.Host ?? "";
        if (finalHost.Length > 0 &&
            !string.Equals(finalHost, DatabaseHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The GeoIP download was redirected to '{finalHost}', which is not "
                + $"{DatabaseHost}. Nothing was saved.");

        return await AtomicFile.ReplaceAsync(destTsvPath,
            async (outFile, token) =>
            {
                await using var netStream = await resp.Content.ReadAsStreamAsync(token);
                await using var gz = new GZipStream(netStream, CompressionMode.Decompress);
                await gz.CopyToAsync(outFile, token);
            },
            // A row is: start<TAB>end<TAB>ASN<TAB>country<TAB>description. The
            // first field is numeric in both the v4 (u32) and v6 tables, which is
            // enough to tell a real table from an HTML error page served with 200.
            (path, length) => AtomicFile.PlausibleTextTable(path, length, 1_000_000,
                first =>
                {
                    string[] parts = first.Split('\t');
                    return parts.Length >= 4 && parts[0].Length > 0 &&
                           parts[0].All(c => char.IsAsciiDigit(c) || c == ':' ||
                                             char.IsAsciiHexDigit(c));
                }),
            ct);
    }

    // -------- API-source lookup (async + cached; runtime-only network) --------

    /// <summary>True for addresses that are never announced (private, loopback, link-local,
    /// CGNAT, multicast, reserved) - querying the API for these is a guaranteed-empty round
    /// trip, so we skip it. Unparseable input is treated as non-routable too.</summary>
    /// <summary>
    /// What to show in a Location cell for an address no database can place: the
    /// kind of address it is, in plain words. Empty for an ordinary public address,
    /// whose blank cell has a different explanation (no GeoIP data, or an address
    /// the data does not cover) that only the caller knows.
    ///
    /// The cell used to stay blank for all of these, so a loopback connection, a
    /// printer on the LAN and a public server missing from the table looked the
    /// same - and the last one is the only one that says anything about coverage.
    /// </summary>
    public static string DescribeUnplaced(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !System.Net.IPAddress.TryParse(ip, out var a)) return "";
        if (System.Net.IPAddress.IsLoopback(a)) return "This PC (loopback)";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        byte[] b = a.GetAddressBytes();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (b[0] == 127) return "This PC (loopback)";
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168))
                return "Local network";
            if (b[0] == 169 && b[1] == 254) return "Local link";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "Carrier network (CGNAT)";
            if (b[0] >= 224 && b[0] <= 239) return "Multicast";
            if (b[0] == 0 || b[0] >= 240) return "Reserved address";
            return "";
        }
        bool allZero = true; foreach (var x in b) if (x != 0) { allZero = false; break; }
        if (allZero) return "Reserved address";
        if ((b[0] & 0xfe) == 0xfc) return "Local network";
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return "Local link";
        if (b[0] == 0xff) return "Multicast";
        return "";
    }

    internal static bool IsPrivateOrReserved(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return true;
        if (System.Net.IPAddress.IsLoopback(a)) return true;
        byte[] b = a.GetAddressBytes();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (b[0] == 0) return true;                                   // 0.0.0.0/8
            if (b[0] == 10) return true;                                  // 10/8 private
            if (b[0] == 127) return true;                                 // 127/8 loopback
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64/10 CGNAT
            if (b[0] == 169 && b[1] == 254) return true;                  // 169.254/16 link-local
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16/12 private
            if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16 private
            if (b[0] >= 224) return true;                                 // 224/4 multicast, 240/4 reserved, broadcast
            return false;
        }
        // IPv6
        bool allZero = true; foreach (var x in b) if (x != 0) { allZero = false; break; }
        if (allZero) return true;                                         // ::
        if ((b[0] & 0xfe) == 0xfc) return true;                           // fc00::/7 unique-local
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;           // fe80::/10 link-local
        if (b[0] == 0xff) return true;                                    // ff00::/8 multicast
        return false;
    }

    private GeoInfo LookupApi(string ip)
    {
        if (_apiCache.TryGetValue(ip, out var hit)) return hit;   // cached (positive or known-empty)
        long now = Environment.TickCount64;
        if (_apiRetryAt.TryGetValue(ip, out var until) && now < until) return new GeoInfo("", 0, "");
        if (!_apiInflight.ContainsKey(ip)) _ = FetchApiAsync(ip); // fire-and-forget; fills the cache
        return new GeoInfo("", 0, "");                            // never block the caller
    }

    private async System.Threading.Tasks.Task FetchApiAsync(string ip)
    {
        if (!_apiInflight.TryAdd(ip, 0)) return;                  // already fetching this IP
        string baseUrl = _apiBase;
        if (baseUrl.Length == 0) { _apiInflight.TryRemove(ip, out _); return; }
        await _apiGate.WaitAsync().ConfigureAwait(false);         // cap concurrency
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/as/ip/" + Uri.EscapeDataString(ip));
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _apiClient.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                System.Threading.Interlocked.Increment(ref _apiFailCount);
                _apiRetryAt[ip] = Environment.TickCount64 + 30_000; return;
            }
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            _apiCache[ip] = ParseApiJson(body);                  // cache the answer (incl. "no data")
            System.Threading.Interlocked.Increment(ref _apiOkCount);
            _apiRetryAt.TryRemove(ip, out _);
        }
        catch
        {
            System.Threading.Interlocked.Increment(ref _apiFailCount);
            _apiRetryAt[ip] = Environment.TickCount64 + 30_000;   // back off this IP for 30s
        }
        finally { _apiGate.Release(); _apiInflight.TryRemove(ip, out _); }
    }

    /// <summary>Parse an iptoasn-webservice JSON reply into a GeoInfo. Pure logic - unit-tested.
    /// Unannounced replies ({"announced":false}) and malformed input return empty.</summary>
    internal static GeoInfo ParseApiJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!(r.TryGetProperty("announced", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True))
                return new GeoInfo("", 0, "");
            string cc = r.TryGetProperty("as_country_code", out var c) ? (c.GetString() ?? "") : "";
            if (cc is "None" or "Unknown" or "-") cc = "";
            int asn = r.TryGetProperty("as_number", out var n) && n.TryGetInt32(out int ni) ? ni : 0;
            string ow = r.TryGetProperty("as_description", out var d) ? (d.GetString() ?? "") : "";
            return new GeoInfo(cc, asn, ow);
        }
        catch { return new GeoInfo("", 0, ""); }
    }

    /// <summary>One-shot connectivity test: resolve 8.8.8.8 and report a friendly status.</summary>
    public static async System.Threading.Tasks.Task<string> TestApiAsync(string baseUrl)
    {
        baseUrl = NormalizeBase(baseUrl);
        if (baseUrl.Length == 0) return "Enter the API server URL first.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/as/ip/8.8.8.8");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _apiClient.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return $"Server responded HTTP {(int)resp.StatusCode}.";
            var gi = ParseApiJson(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return gi.HasData
                ? $"Connected - 8.8.8.8 resolved to {gi.Country} AS{gi.Asn} {gi.Owner}."
                : "Connected, but the server returned no data for 8.8.8.8.";
        }
        catch (Exception ex) { return "Could not reach the server: " + ex.Message; }
    }
}
