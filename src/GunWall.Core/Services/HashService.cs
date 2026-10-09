using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace GunWall.Services;

/// <summary>
/// Computes and caches SHA-256 hashes of executables. The hash lets GunWall
/// detect when a previously approved (or blocked) program has been replaced on
/// disk — a swapped binary keeps the same path but produces a different hash,
/// which is a strong tamper signal. Hashing is done off the UI thread and
/// cached by path+size+writetime so repeated lookups are free.
/// </summary>
public static class HashService
{
    private sealed record CacheKey(string Path, long Size, long Ticks);
    private static readonly ConcurrentDictionary<CacheKey, string> Cache = new();

    /// <summary>
    /// Returns the uppercase hex SHA-256 of the file, or empty string if it
    /// cannot be read. Never throws.
    /// </summary>
    public static string Compute(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "";
        try
        {
            var fi = new FileInfo(exePath);
            if (!fi.Exists) return "";

            var key = new CacheKey(exePath, fi.Length, fi.LastWriteTimeUtc.Ticks);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            using var stream = File.OpenRead(exePath);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            string hex = Convert.ToHexString(hash); // uppercase, no dashes
            Cache[key] = hex;
            return hex;
        }
        catch
        {
            return ""; // locked, missing, or access-denied — treat as unknown
        }
    }

    /// <summary>Short, display-friendly form: first 12 hex chars.</summary>
    public static string Short(string fullHash) =>
        string.IsNullOrEmpty(fullHash) ? "\u2014" : fullHash[..Math.Min(12, fullHash.Length)];
}
