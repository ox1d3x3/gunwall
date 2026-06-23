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
    private uint[] _start = Array.Empty<uint>();
    private uint[] _end = Array.Empty<uint>();
    private int[] _asn = Array.Empty<int>();
    private string[] _country = Array.Empty<string>();
    private string[] _owner = Array.Empty<string>();

    public bool Loaded => _start.Length > 0;
    public int RangeCount => _start.Length;

    /// <summary>Parse the iptoasn TSV text into the sorted lookup arrays.</summary>
    public void LoadFromText(string tsv)
    {
        var starts = new List<uint>();
        var ends = new List<uint>();
        var asns = new List<int>();
        var countries = new List<string>();
        var owners = new List<string>();

        using var reader = new StringReader(tsv);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            // Fields: start \t end \t asn \t country \t owner
            string[] f = line.Split('\t');
            if (f.Length < 4) continue;
            if (!uint.TryParse(f[0], out uint s)) continue;
            if (!uint.TryParse(f[1], out uint e)) continue;
            if (e < s) continue;
            int.TryParse(f[2], out int a);
            string cc = f[3];
            if (cc is "None" or "Unknown" or "-") cc = "";
            string ow = f.Length > 4 ? f[4] : "";

            starts.Add(s); ends.Add(e); asns.Add(a); countries.Add(cc); owners.Add(ow);
        }

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

        _start = ns; _end = ne; _asn = na; _country = nc; _owner = no;
    }

    /// <summary>Look up a remote address. Unknown / IPv6 / invalid returns empty.</summary>
    public GeoInfo Lookup(string ip)
    {
        if (!Loaded || string.IsNullOrEmpty(ip)) return new GeoInfo("", 0, "");
        if (!TryToUInt32(ip, out uint addr)) return new GeoInfo("", 0, ""); // IPv6 / invalid

        // Greatest start <= addr, then confirm addr falls within that range's end.
        int lo = 0, hi = _start.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_start[mid] <= addr) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found >= 0 && addr <= _end[found])
            return new GeoInfo(_country[found], _asn[found], _owner[found]);
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

    public void LoadFromFile(string path)
    {
        if (File.Exists(path)) LoadFromText(File.ReadAllText(path));
    }

    /// <summary>
    /// Download + gunzip the free CC0 iptoasn IPv4 dataset to the given cache path.
    /// Returns the number of bytes written. Throws on network/IO failure (callers
    /// run this off the UI thread and surface a friendly message).
    /// </summary>
    public static long DownloadDatabase(string destTsvPath)
    {
        const string url = "https://iptoasn.com/data/ip2asn-v4-u32.tsv.gz";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var netStream = client.GetStreamAsync(url).GetAwaiter().GetResult();
        using var gz = new GZipStream(netStream, CompressionMode.Decompress);
        using var outFile = File.Create(destTsvPath);
        gz.CopyTo(outFile);
        outFile.Flush();
        return outFile.Length;
    }
}
