using System.Diagnostics;
using System.IO;

namespace GunWall.Services;

/// <summary>
/// Manages a clearly-delimited GunWall section inside the Windows hosts file
/// (C:\Windows\System32\drivers\etc\hosts). Domain-based blocking: each blocked
/// domain becomes a "0.0.0.0 domain" sink line, which is instant to apply and
/// robust to the server changing IP. Everything outside the GunWall markers is
/// preserved untouched, and the original hosts file is backed up once.
/// </summary>
public static class HostsFileService
{
    private const string BeginMarker = "# >>> GunWall managed block BEGIN (auto-generated - do not edit inside) >>>";
    private const string EndMarker = "# <<< GunWall managed block END <<<";

    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private static string BackupPath(string dataFolder) => Path.Combine(dataFolder, "hosts.backup");

    /// <summary>Domains currently in the GunWall block.</summary>
    public static List<string> GetBlockedDomains()
    {
        var domains = new List<string>();
        try
        {
            if (!File.Exists(HostsPath)) return domains;
            bool inBlock = false;
            foreach (var raw in File.ReadAllLines(HostsPath))
            {
                var line = raw.Trim();
                if (line.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase)) { inBlock = true; continue; }
                if (line.Equals(EndMarker, StringComparison.OrdinalIgnoreCase)) { inBlock = false; continue; }
                if (inBlock && line.StartsWith("0.0.0.0 ", StringComparison.Ordinal))
                    domains.Add(line.Substring(8).Trim());
            }
        }
        catch { }
        return domains;
    }

    /// <summary>
    /// Replaces the GunWall block with sink entries for the given domains.
    /// An empty set removes the block entirely. Returns true on success.
    /// </summary>
    public static bool SetBlockedDomains(IEnumerable<string> domains, string dataFolder)
    {
        try
        {
            // One-time backup of the user's original hosts file.
            try
            {
                string backup = BackupPath(dataFolder);
                if (!File.Exists(backup) && File.Exists(HostsPath))
                    File.Copy(HostsPath, backup);
            }
            catch { }

            string existing = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            string userContent = StripBlock(existing);

            var sorted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in domains)
            {
                var dd = (d ?? "").Trim();
                if (dd.Length > 0) sorted.Add(dd);
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(userContent.TrimEnd('\r', '\n'));
            if (sorted.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine(BeginMarker);
                sb.AppendLine($"# {sorted.Count} domains blocked by GunWall. Manage these from the app, not by hand.");
                foreach (var d in sorted) sb.Append("0.0.0.0 ").AppendLine(d);
                sb.AppendLine(EndMarker);
            }
            sb.AppendLine();

            File.WriteAllText(HostsPath, sb.ToString());
            FlushDns();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string StripBlock(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        bool inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase)) { inBlock = true; continue; }
            if (line.Equals(EndMarker, StringComparison.OrdinalIgnoreCase)) { inBlock = false; continue; }
            if (!inBlock) kept.Add(raw);
        }
        return string.Join(Environment.NewLine, kept);
    }

    /// <summary>Flushes the DNS resolver cache so hosts changes take effect at once.</summary>
    public static void FlushDns()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
