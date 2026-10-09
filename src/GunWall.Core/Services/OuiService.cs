using System.IO;
using System.Net.Http;

namespace GunWall.Services;

/// <summary>Maps a MAC address to the organisation that registered its prefix,
/// using IEEE's own public registry listing.
///
/// Three registries, and they do NOT share a prefix length:
///
///   MA-L  24 bits (6 hex)  ~32,800 assignments - most consumer hardware
///   MA-M  28 bits (7 hex)   ~4,500
///   MA-S  36 bits (9 hex)   ~5,100
///
/// That difference is the correctness trap. For an MA-M or MA-S range the first
/// 24 bits belong to **IEEE**, not to the vendor - so a naive three-byte lookup
/// returns a confidently wrong manufacturer for roughly nine thousand blocks.
/// Matching is therefore longest-prefix first: 36, then 28, then 24.
///
/// Downloaded on request rather than bundled, exactly as the GeoIP table is: it
/// keeps the binary small, keeps the data current, and means no IEEE listing is
/// redistributed inside an MIT repository.
/// </summary>
public sealed class OuiService
{
    // Keyed by uppercase hex prefix of the length each registry uses.
    private Dictionary<string, string> _p36 = new(StringComparer.Ordinal);
    private Dictionary<string, string> _p28 = new(StringComparer.Ordinal);
    private Dictionary<string, string> _p24 = new(StringComparer.Ordinal);

    public bool Loaded => _p24.Count + _p28.Count + _p36.Count > 0;
    public int Count => _p24.Count + _p28.Count + _p36.Count;

    /// <summary>IEEE's public listing. Pinned, and re-checked after redirects by
    /// the caller, for the same reason the updater pins its download host: this
    /// content is written to disk and read back as authority on what hardware is
    /// on someone's network.</summary>
    public const string RegistryHost = "standards-oui.ieee.org";

    public static readonly (string Name, string Url)[] Registries =
    {
        ("MA-L", "https://standards-oui.ieee.org/oui/oui.csv"),
        ("MA-M", "https://standards-oui.ieee.org/oui28/mam.csv"),
        ("MA-S", "https://standards-oui.ieee.org/oui36/oui36.csv"),
    };

    /// <summary>The organisation for this MAC, or "" when nothing matches.
    ///
    /// Returns "" for a locally administered address rather than searching: the
    /// U/L bit means the device chose its own address and no vendor registered
    /// it, so a lookup could only ever produce a coincidence. Every modern phone
    /// randomises by default, and the scan already labels those, so a blank here
    /// is explained rather than mysterious.</summary>
    public string Lookup(string? mac)
    {
        if (!Loaded || string.IsNullOrWhiteSpace(mac)) return "";

        string hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 6) return "";

        // Locally administered: bit 1 of the first octet.
        if (int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber,
                         null, out int first) && (first & 0x02) != 0)
            return "";

        // Longest prefix wins.
        if (hex.Length >= 9 && _p36.TryGetValue(hex[..9], out var v36)) return v36;
        if (hex.Length >= 7 && _p28.TryGetValue(hex[..7], out var v28)) return v28;
        return _p24.TryGetValue(hex[..6], out var v24) ? v24 : "";
    }

    public void LoadFromFile(string path)
    {
        if (File.Exists(path)) LoadFromText(File.ReadAllText(path));
    }

    /// <summary>Parses IEEE's CSV: Registry,Assignment,Organization Name,Address.
    ///
    /// Parsed as real CSV rather than split on commas. Organisation names contain
    /// them, quoted - "Shenzhen ViewAt Technology Co.,Ltd." - and splitting would
    /// silently truncate a vendor to "Shenzhen ViewAt Technology Co." while
    /// looking entirely plausible.
    ///
    /// Rows are bucketed by the LENGTH of their assignment rather than by the
    /// registry name, so a file that mixes registries, or a registry that changes
    /// its label, still lands in the right table.</summary>
    public void LoadFromText(string csv)
    {
        var p24 = new Dictionary<string, string>(StringComparer.Ordinal);
        var p28 = new Dictionary<string, string>(StringComparer.Ordinal);
        var p36 = new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = new StringReader(csv);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = ParseCsvLine(line);
            if (f.Count < 3) continue;

            string assign = new string(f[1].Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            string org = f[2].Trim();
            if (org.Length == 0) continue;

            switch (assign.Length)
            {
                case 6: p24[assign] = org; break;
                case 7: p28[assign] = org; break;
                case 9: p36[assign] = org; break;
                    // Anything else is a header row or a format this build does
                    // not know; skipped rather than guessed at.
            }
        }

        _p24 = p24; _p28 = p28; _p36 = p36;
    }

    /// <summary>Minimal RFC-4180 field splitter: quoted fields may contain commas,
    /// and a doubled quote inside one is a literal quote.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>(4);
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c != '"') { sb.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; continue; }
                inQuotes = false;
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Downloads every registry into one file. Returns what happened.
    ///
    /// Each registry is fetched independently and a failure on one does not lose
    /// the others: MA-L alone covers most consumer hardware, so partial data is
    /// worth far more than none. The result says which succeeded rather than
    /// reporting a single success or failure for all three.</summary>
    public static Task<(int Written, string Message)> DownloadAsync(string destPath)
        => DownloadAsync(destPath, CancellationToken.None);

    /// <summary>
    /// Fetches the three IEEE registries into one file.
    ///
    /// Two rules govern the write, and both exist because this runs unattended
    /// once a schedule is added:
    ///
    /// A PARTIAL result never replaces a COMPLETE one. All three registries share
    /// a single file, so writing whatever was reachable would silently discard the
    /// two that were not - a refresh that fetched only MA-L would turn a full
    /// vendor table into a third of one, reporting success. If a file is already
    /// present and any registry failed, the existing file is kept.
    ///
    /// On a FIRST download there is nothing to protect, so a partial result is
    /// better than none and is written with what failed named plainly.
    /// </summary>
    public static async Task<(int Written, string Message)> DownloadAsync(
        string destPath, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("User-Agent", "GunWall");

        var sb = new System.Text.StringBuilder();
        var ok = new List<string>();
        var failed = new List<string>();

        foreach (var (name, url) in Registries)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                    u.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(u.Host, RegistryHost, StringComparison.OrdinalIgnoreCase))
                { failed.Add(name); continue; }

                using var resp = await client.GetAsync(url, ct);
                // Re-checked on whatever answered, because HttpClient follows
                // redirects and this file is read back as authority.
                string finalHost = resp.RequestMessage?.RequestUri?.Host ?? "";
                if (!resp.IsSuccessStatusCode ||
                    (finalHost.Length > 0 &&
                     !string.Equals(finalHost, RegistryHost, StringComparison.OrdinalIgnoreCase)))
                { failed.Add(name); continue; }

                sb.Append(await resp.Content.ReadAsStringAsync(ct)).Append('\n');
                ok.Add(name);
            }
            catch { failed.Add(name); }
        }

        if (ok.Count == 0)
            return (0, "Could not reach the IEEE registry. Nothing was saved.");

        bool havePrevious = File.Exists(destPath);
        if (failed.Count > 0 && havePrevious)
            return (0, $"{string.Join(", ", failed)} could not be reached. The existing "
                     + "vendor database was kept rather than replaced with a partial one.");

        string text = sb.ToString();
        await AtomicFile.ReplaceAsync(destPath,
            async (outFile, token) =>
            {
                await using var writer = new StreamWriter(outFile, leaveOpen: true);
                await writer.WriteAsync(text.AsMemory(), token);
                await writer.FlushAsync();
            },
            // Each registry line begins with a hex OUI prefix; the CSV header
            // begins with "Registry". Either proves this is the IEEE file and not
            // a portal login page returned with 200.
            (path, length) => AtomicFile.PlausibleTextTable(path, length, 200_000,
                first => first.StartsWith("Registry", StringComparison.OrdinalIgnoreCase)
                      || first.Split(',').Length >= 3),
            ct);

        string msg = failed.Count == 0
            ? $"Downloaded {string.Join(", ", ok)}."
            : $"Downloaded {string.Join(", ", ok)}; {string.Join(", ", failed)} "
              + "could not be reached, so some devices may show no vendor.";
        return (ok.Count, msg);
    }
}
