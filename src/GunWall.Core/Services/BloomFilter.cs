using System;
using System.IO;

namespace GunWall.Services;

/// <summary>
/// A compact, dependency-free Bloom filter for fast "is this domain on a list?"
/// membership tests over large blocklists. False positives are possible (the rate
/// is tunable); false negatives never happen. It lets GunWall skip the expensive
/// exact lookup for the overwhelmingly common not-on-any-list case.
///
/// Pure logic - no WPF, no I/O on the hot path - so it is unit-testable off-device.
/// </summary>
public sealed class BloomFilter
{
    private readonly ulong[] _bits;
    private readonly int _bitCount;
    private readonly int _hashCount;

    public int Count { get; private set; }
    public int BitCount => _bitCount;
    public int HashCount => _hashCount;

    public BloomFilter(int expectedItems, double falsePositiveRate = 0.01)
    {
        if (expectedItems < 1) expectedItems = 1;
        if (falsePositiveRate <= 0 || falsePositiveRate >= 1) falsePositiveRate = 0.01;

        // Optimal sizing: m = -n ln(p) / (ln2)^2 ;  k = (m/n) ln2
        double m = -(expectedItems * Math.Log(falsePositiveRate)) / (Math.Log(2) * Math.Log(2));
        _bitCount = Math.Max(64, (int)Math.Ceiling(m));
        _hashCount = Math.Clamp((int)Math.Round((_bitCount / (double)expectedItems) * Math.Log(2)), 1, 16);
        _bits = new ulong[(_bitCount + 63) / 64];
    }

    /// <summary>Add a domain (case/trailing-dot normalised) to the set.</summary>
    public void Add(string item)
    {
        if (string.IsNullOrEmpty(item)) return;
        var (h1, h2) = Hash(item);
        for (int i = 0; i < _hashCount; i++)
        {
            uint bit = (uint)((h1 + (ulong)i * h2) % (ulong)_bitCount);
            _bits[bit >> 6] |= 1UL << (int)(bit & 63);
        }
        Count++;
    }

    /// <summary>
    /// Returns false if the item is *definitely* not in the set, true if it
    /// *might* be (then confirm with an exact check).
    /// </summary>
    public bool MightContain(string item)
    {
        if (string.IsNullOrEmpty(item)) return false;
        var (h1, h2) = Hash(item);
        for (int i = 0; i < _hashCount; i++)
        {
            uint bit = (uint)((h1 + (ulong)i * h2) % (ulong)_bitCount);
            if ((_bits[bit >> 6] & (1UL << (int)(bit & 63))) == 0) return false;
        }
        return true;
    }

    public void Clear()
    {
        Array.Clear(_bits, 0, _bits.Length);
        Count = 0;
    }

    // FNV-1a 64-bit over a normalised view of the string, split into two halves
    // for Kirsch-Mitzenmacher double hashing (g_i = h1 + i*h2).
    private static (ulong h1, ulong h2) Hash(string s)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        // Normalise: lowercase ASCII, ignore one trailing dot.
        int len = s.Length;
        if (len > 0 && s[len - 1] == '.') len--;

        ulong hash = fnvOffset;
        for (int i = 0; i < len; i++)
        {
            char c = s[i];
            if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
            hash ^= c;
            hash *= fnvPrime;
        }
        ulong h1 = hash & 0xFFFFFFFFUL;
        ulong h2 = ((hash >> 32) & 0xFFFFFFFFUL) | 1UL; // force odd & non-zero
        return (h1, h2);
    }
}
