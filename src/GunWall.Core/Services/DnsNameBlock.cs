using System.Diagnostics;
using System.IO;

namespace GunWall.Services;

/// <summary>
/// Blocks names through Windows' own DNS policy - the Name Resolution Policy Table -
/// when the hosts file cannot be written (0.99.238).
///
/// Why: on the PC this was found on, the hosts file answers "access denied" even to
/// SYSTEM, so a custom blocklist blocked nothing. Blocking the sites' addresses
/// instead (0.99.236) could only act after a first connection, leaked for any large
/// site, and cut Bitwarden and Edge off from a self-hosted vault that shares
/// Cloudflare addresses with a blocked name. A name has to be blocked as a name.
///
/// How: one policy rule per 50 names sends lookups for exactly those names to
/// 127.0.0.1, where nothing answers, so they fail for every program that uses
/// Windows' resolver. Other names are untouched. Rules are created and removed with
/// Windows' own cmdlets (Add-/Remove-DnsClientNrptRule), which is the supported way
/// and takes effect at once; every rule carries <see cref="Tag"/> in its comment, and
/// only rules with that comment are ever removed.
///
/// Limits, stated where the user sees them: a browser with its own secure DNS
/// (DNS over HTTPS) does not ask Windows and is not affected; and this is for a
/// personal list, not a 100,000-line one - above <see cref="MaxNames"/> it declines.
/// </summary>
public static class DnsNameBlock
{
    public const string Tag = "GunWall custom blocklist";
    public const int MaxNames = 2000;
    public const int NamesPerRule = 50;
    private const string StateFile = "dns-policy-names.txt";

    private static string StatePath(string dataFolder) => Path.Combine(dataFolder, StateFile);

    /// <summary>True when GunWall has rules in place (its own record says so).</summary>
    public static bool HasRules(string dataFolder)
    {
        try { return File.Exists(StatePath(dataFolder)); } catch { return false; }
    }

    /// <summary>The names the rules in place were made for, or empty.</summary>
    public static List<string> RecordedNames(string dataFolder)
    {
        try { return File.Exists(StatePath(dataFolder)) ? Clean(File.ReadAllLines(StatePath(dataFolder))) : new(); }
        catch { return new(); }
    }

    /// <summary>Names fit for a policy rule: plain host names only, lower case, sorted,
    /// once each. Anything with a character outside letters, digits, '.', '-' and '_'
    /// is dropped - the list becomes part of a command, so nothing else may pass.</summary>
    public static List<string> Clean(IEnumerable<string> names)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            // A leading '.' is Windows' form of "this name and everything under it".
            string n = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (n.Length is 0 or > 253 || !n.TrimStart('.').Contains('.') || n.StartsWith("..", StringComparison.Ordinal)) continue;
            if (n.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'))) continue;
            set.Add(n);
        }
        return set.ToList();
    }

    /// <summary>Removes GunWall's rules (only those tagged as its own).</summary>
    public static string RemoveScript =>
        "$ErrorActionPreference='Stop'; "
        + "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + Tag + "' } | "
        + "ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }; "
        + "Clear-DnsClientCache";

    /// <summary>Replaces GunWall's rules with rules for the names in <paramref name="namesFile"/>.</summary>
    public static string ApplyScript(string namesFile) =>
        "$ErrorActionPreference='Stop'; "
        + "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + Tag + "' } | "
        + "ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }; "
        + "$n=@(Get-Content -LiteralPath '" + namesFile.Replace("'", "''") + "' | Where-Object { $_ }); "
        + "for($i=0;$i -lt $n.Count;$i+=" + NamesPerRule + "){ "
        + "Add-DnsClientNrptRule -Namespace $n[$i..([Math]::Min($i+" + (NamesPerRule - 1) + ",$n.Count-1))] "
        + "-NameServers '127.0.0.1' -Comment '" + Tag + "' }; "
        + "Clear-DnsClientCache";

    /// <summary>Puts rules in place for <paramref name="names"/>. Returns whether it
    /// worked and, when not, why in words a log line can carry.</summary>
    public static (bool Ok, string Detail) Apply(IEnumerable<string> names, string dataFolder)
    {
        var list = Clean(names);
        if (list.Count == 0) return Clear(dataFolder);
        if (list.Count > MaxNames)
            return (false, $"the list has {list.Count:N0} names; Windows' DNS policy is used for up to {MaxNames:N0}");
        string path = StatePath(dataFolder);
        try
        {
            Directory.CreateDirectory(dataFolder);
            string tmp = path + ".new";
            File.WriteAllLines(tmp, list);
            var (code, output) = RunPowerShell(ApplyScript(tmp), 60_000);
            if (code != 0)
            {
                try { File.Delete(tmp); } catch { }
                return (false, $"Windows refused the DNS policy rules (exit {code}): {Shorten(output)}");
            }
            File.Move(tmp, path, overwrite: true);
            return (true, $"{list.Count:N0} name(s) in {(list.Count + NamesPerRule - 1) / NamesPerRule} DNS policy rule(s)");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Removes GunWall's rules. Ok when there were none.</summary>
    public static (bool Ok, string Detail) Clear(string dataFolder)
    {
        if (!HasRules(dataFolder)) return (true, "no DNS policy rules to remove");
        try
        {
            var (code, output) = RunPowerShell(RemoveScript, 60_000);
            if (code != 0) return (false, $"Windows would not remove the DNS policy rules (exit {code}): {Shorten(output)}");
            File.Delete(StatePath(dataFolder));
            return (true, "DNS policy rules removed");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Asks Windows to resolve <paramref name="name"/>: true when it no longer
    /// gets a public address - the block is in effect, not merely written.</summary>
    public static bool InEffect(string name)
    {
        try
        {
            var t = Task.Run(() => System.Net.Dns.GetHostAddresses(name));
            if (!t.Wait(5000)) return true;                 // nothing answers: that is the block
            return t.Result.All(a => System.Net.IPAddress.IsLoopback(a) || a.Equals(System.Net.IPAddress.Any)
                                     || a.Equals(System.Net.IPAddress.IPv6Any));
        }
        catch { return true; }                               // "no such host": blocked
    }

    private static string Shorten(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 300 ? s : s[..300] + "...";
    }

    /// <summary>Windows PowerShell by its full path (never from PATH), one command,
    /// no profile, no window.</summary>
    private static (int Code, string Output) RunPowerShell(string command, int timeoutMs)
    {
        if (!OperatingSystem.IsWindows()) return (-1, "not Windows");
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                  "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        using var p = Process.Start(psi);
        if (p == null) return (-1, "PowerShell did not start");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "PowerShell did not finish in time");
        }
        string text = (err.Result + " " + outp.Result).Trim();
        return (p.ExitCode, text);
    }
}
