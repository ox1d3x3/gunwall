using System;
using System.Collections.Generic;
using System.Linq;

namespace GunWall.Services;

/// <summary>One row for the usage table (strings pre-formatted for binding).</summary>
public sealed record UsageRow(string App, string DownText, string UpText, string TotalText, long TotalBytes);

/// <summary>
/// Approximate per-app data usage. Windows doesn't hand out per-process byte
/// counts without an ETW subscription, so this takes the honest lightweight
/// route: each interval's measured total bytes (from the NIC counters) are
/// attributed to apps in proportion to their share of active external
/// connections in that interval. Clearly an estimate — but it reliably answers
/// "what caused that spike?", which is the point.
///
/// Usage is accumulated into per-minute buckets kept for 24 hours, so the UI
/// can total any window (last 5 minutes, hour, day). Pure logic, no WPF —
/// fully unit-testable offline.
/// </summary>
public sealed class AppUsageService
{
    private sealed class Bucket
    {
        public DateTime Minute;                       // UTC, truncated to minute
        public readonly Dictionary<string, (long Down, long Up)> Apps = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Queue<Bucket> _buckets = new();
    private readonly object _lock = new();
    private const int MaxMinutes = 24 * 60;

    /// <summary>Attribute one interval's bytes across the apps active in it.</summary>
    public void Record(double bytesDown, double bytesUp, IReadOnlyList<(string App, int Conns)> apps)
    {
        if (apps == null || apps.Count == 0) return;
        if (bytesDown <= 0 && bytesUp <= 0) return;

        long totalConns = 0;
        foreach (var (_, n) in apps) totalConns += Math.Max(0, n);
        if (totalConns <= 0) return;

        var minute = Truncate(DateTime.UtcNow);
        lock (_lock)
        {
            Bucket? b = _buckets.Count > 0 ? _buckets.Last() : null;
            if (b == null || b.Minute != minute)
            {
                b = new Bucket { Minute = minute };
                _buckets.Enqueue(b);
                while (_buckets.Count > MaxMinutes) _buckets.Dequeue();
            }

            foreach (var (app, n) in apps)
            {
                if (n <= 0 || string.IsNullOrEmpty(app)) continue;
                double share = n / (double)totalConns;
                long d = (long)(bytesDown * share);
                long u = (long)(bytesUp * share);
                if (d == 0 && u == 0) continue;
                b.Apps.TryGetValue(app, out var cur);
                b.Apps[app] = (cur.Down + d, cur.Up + u);
            }
        }
    }

    /// <summary>Per-app totals over the trailing window, largest first.</summary>
    public List<UsageRow> Totals(TimeSpan window, int maxRows = 50)
    {
        var cutoff = Truncate(DateTime.UtcNow) - window;
        var sum = new Dictionary<string, (long Down, long Up)>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var b in _buckets)
            {
                if (b.Minute < cutoff) continue;
                foreach (var (app, v) in b.Apps)
                {
                    sum.TryGetValue(app, out var cur);
                    sum[app] = (cur.Down + v.Down, cur.Up + v.Up);
                }
            }
        }

        return sum
            .Select(kv => new UsageRow(
                kv.Key,
                FormatBytes(kv.Value.Down),
                FormatBytes(kv.Value.Up),
                FormatBytes(kv.Value.Down + kv.Value.Up),
                kv.Value.Down + kv.Value.Up))
            .OrderByDescending(r => r.TotalBytes)
            .Take(maxRows)
            .ToList();
    }

    public static string FormatBytes(long b)
    {
        if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:0.##} GB";
        if (b >= 1_048_576) return $"{b / 1_048_576.0:0.#} MB";
        if (b >= 1024) return $"{b / 1024.0:0.#} KB";
        return $"{b} B";
    }

    // ------------------------------------------------ persistence (24h survives restarts)
    private sealed class BucketDto
    {
        public DateTime Minute { get; set; }
        public Dictionary<string, long[]> Apps { get; set; } = new();
    }

    /// <summary>Write all buckets to a JSON file (best-effort).</summary>
    public void SaveTo(string path)
    {
        try
        {
            List<BucketDto> dto;
            lock (_lock)
            {
                dto = _buckets.Select(b => new BucketDto
                {
                    Minute = b.Minute,
                    Apps = b.Apps.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Down, kv.Value.Up })
                }).ToList();
            }
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dto));
        }
        catch { /* history is a convenience; never block shutdown */ }
    }

    /// <summary>Load buckets from disk, dropping anything older than 24h.</summary>
    public void LoadFrom(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            var dto = System.Text.Json.JsonSerializer
                .Deserialize<List<BucketDto>>(System.IO.File.ReadAllText(path));
            if (dto == null) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-MaxMinutes);
            lock (_lock)
            {
                _buckets.Clear();
                foreach (var d in dto.OrderBy(d => d.Minute))
                {
                    if (d.Minute < cutoff) continue;
                    var b = new Bucket { Minute = d.Minute };
                    foreach (var (app, v) in d.Apps)
                        if (v is { Length: 2 }) b.Apps[app] = (v[0], v[1]);
                    _buckets.Enqueue(b);
                }
            }
        }
        catch { /* corrupt history file: start fresh */ }
    }

    private static DateTime Truncate(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
}
