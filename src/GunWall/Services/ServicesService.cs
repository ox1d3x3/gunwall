using System.Diagnostics;

namespace GunWall.Services;

/// <summary>
/// Enumerates Windows services and resolves the executable hosting each one, so
/// the firewall can block a service by its host image. Uses the built-in sc.exe
/// tool (no managed dependency, honoring the zero-NuGet rule): `sc query` for
/// the list/state and `sc qc` for the binary path. All best-effort.
/// </summary>
public sealed class ServicesService
{
    public sealed record ServiceItem(string Name, string Display, string Status, string ExePath);

    /// <summary>Lists all services with their state (path resolved on demand).</summary>
    public static List<ServiceItem> GetServices()
    {
        var list = new List<ServiceItem>();
        try
        {
            string output = RunSc("query type= service state= all");
            string name = "", display = "", state = "";
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
                {
                    // Flush the previous record.
                    if (!string.IsNullOrEmpty(name))
                        list.Add(new ServiceItem(name, string.IsNullOrEmpty(display) ? name : display, state, ""));
                    name = line["SERVICE_NAME:".Length..].Trim();
                    display = ""; state = "";
                }
                else if (line.StartsWith("DISPLAY_NAME:", StringComparison.OrdinalIgnoreCase))
                {
                    display = line["DISPLAY_NAME:".Length..].Trim();
                }
                else if (line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                {
                    // e.g. "STATE              : 4  RUNNING"
                    string after = line[(line.IndexOf(':') + 1)..].Trim();
                    var parts = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    state = parts.Length >= 2 ? Capitalize(parts[1]) : after;
                }
            }
            if (!string.IsNullOrEmpty(name))
                list.Add(new ServiceItem(name, string.IsNullOrEmpty(display) ? name : display, state, ""));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetServices failed: {ex.Message}");
        }
        return list.OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resolves the host .exe path for a single service via `sc qc`.</summary>
    public static string GetBinaryPath(string serviceName)
    {
        try
        {
            string output = RunSc($"qc \"{serviceName}\"");
            foreach (var line in output.Split('\n'))
            {
                int idx = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                int colon = line.IndexOf(':', idx);
                if (colon < 0) continue;
                return ExtractExe(line[(colon + 1)..].Trim());
            }
        }
        catch (Exception ex) { Debug.WriteLine($"GetBinaryPath failed: {ex.Message}"); }
        return "";
    }

    private static string RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(8000);
        return output;
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLower();

    private static string ExtractExe(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1) return cmd[1..end];
        }
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0) return cmd[..(exeIdx + 4)];
        int space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }
}
