using System.Diagnostics;

namespace GunWall.Services;

/// <summary>
/// Reads and controls the built-in Windows Defender Firewall, and enumerates
/// its application rules so they can be imported into GunWall.
///
/// State is read through the COM firewall policy object (locale-independent);
/// enabling/disabling uses <c>netsh advfirewall</c> (simple and reversible).
/// Everything is best-effort: failures return null/empty rather than throwing.
/// Running two WFP firewalls at once can conflict, which is why offering to
/// turn the Windows firewall off is a legitimate option.
/// </summary>
public static class WindowsFirewallService
{
    // NET_FW_PROFILE2_* bit flags.
    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;

    public sealed record FirewallState(bool? Domain, bool? Private, bool? Public)
    {
        public bool AnyOn => Domain == true || Private == true || Public == true;
        public bool AllOff => Domain == false && Private == false && Public == false;
    }

    public sealed record WfRule(string Name, string AppPath, bool Block, bool Outbound, bool Enabled);

    /// <summary>Per-profile enabled state via the COM firewall policy.</summary>
    public static FirewallState GetState()
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) return new FirewallState(null, null, null);
            dynamic policy = Activator.CreateInstance(t)!;
            bool d = policy.FirewallEnabled[ProfileDomain];
            bool p = policy.FirewallEnabled[ProfilePrivate];
            bool u = policy.FirewallEnabled[ProfilePublic];
            return new FirewallState(d, p, u);
        }
        catch
        {
            return new FirewallState(null, null, null);
        }
    }

    /// <summary>Turns Windows Firewall on/off for all profiles (needs admin).</summary>
    public static bool SetEnabled(bool on)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"advfirewall set allprofiles state {(on ? "on" : "off")}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Enumerates Windows Firewall rules that target a specific program.</summary>
    public static List<WfRule> GetAppRules()
    {
        var list = new List<WfRule>();
        try
        {
            Type? t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) return list;
            dynamic policy = Activator.CreateInstance(t)!;
            dynamic rules = policy.Rules;
            foreach (dynamic r in rules)
            {
                string? app = null;
                try { app = r.ApplicationName; } catch { }
                if (string.IsNullOrEmpty(app)) continue;

                int action = 1;  // 0 = block, 1 = allow
                int dir = 2;     // 1 = in, 2 = out
                bool enabled = true;
                string name = "";
                try { action = (int)r.Action; } catch { }
                try { dir = (int)r.Direction; } catch { }
                try { enabled = (bool)r.Enabled; } catch { }
                try { name = r.Name ?? ""; } catch { }

                list.Add(new WfRule(name, app!, action == 0, dir == 2, enabled));
            }
        }
        catch
        {
            // COM unavailable or access denied — return what we have.
        }
        return list;
    }
}
