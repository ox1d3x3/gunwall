using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GunWall.Services;

public sealed record DnsPreset(string Key, string Name, string Description, string Primary, string Secondary);

/// <summary>
/// Sets or restores the DNS servers on active adapters via netsh. Pointing the
/// system at a filtering resolver adds a second, maintenance-free blocking layer
/// on top of the hosts file. Only resolvers that answer on plain UDP/53 are
/// offered here (so AdGuard and Quad9 work; Mullvad's filtered DNS needs
/// encrypted DNS and is intentionally left out for now).
/// </summary>
public static class DnsService
{
    public static readonly DnsPreset Automatic =
        new("auto", "Automatic (from network)", "Use whatever DNS your network hands out.", "", "");
    public static readonly DnsPreset AdGuard =
        new("adguard", "AdGuard DNS - ads & trackers", "Blocks ads and trackers at the resolver.", "94.140.14.14", "94.140.15.15");
    public static readonly DnsPreset Quad9 =
        new("quad9", "Quad9 - security", "Blocks malware and phishing domains.", "9.9.9.9", "149.112.112.112");
    public static readonly DnsPreset Cloudflare =
        new("cloudflare", "Cloudflare 1.1.1.1 - no filtering", "Fast, private DNS with no content filtering.", "1.1.1.1", "1.0.0.1");

    public static readonly IReadOnlyList<DnsPreset> All = new[] { Automatic, AdGuard, Quad9, Cloudflare };

    public static DnsPreset ByKey(string key) =>
        All.FirstOrDefault(p => p.Key == key) ?? Automatic;

    private static IEnumerable<string> ActiveAdapterNames()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            yield return ni.Name;
        }
    }

    /// <summary>
    /// Runs netsh and returns its output. Used to capture the machine's ACTUAL
    /// DNS configuration for diagnostics, rather than the configuration GunWall
    /// believes it applied - the two can differ, and that gap is exactly where
    /// resolution problems hide.
    /// </summary>
    private static string NetshOutput(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "(netsh did not start)";
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return outp.Trim();
        }
        catch (Exception ex) { return $"(netsh failed: {ex.Message})"; }
    }

    /// <summary>
    /// The DNS configuration Windows is really using, for the diagnostics
    /// bundle: per-adapter servers for BOTH address families, plus the Name
    /// Resolution Policy Table. NRPT matters because a VPN client can install
    /// policy rules that override adapter DNS entirely - so GunWall can appear
    /// to have redirected DNS while Windows quietly sends queries elsewhere.
    /// </summary>
    public static string DescribeCurrentDnsState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("===== IPv4 DNS servers (netsh) =====");
        sb.AppendLine(NetshOutput("interface ipv4 show dnsservers"));
        sb.AppendLine();
        sb.AppendLine("===== IPv6 DNS servers (netsh) =====");
        sb.AppendLine(NetshOutput("interface ipv6 show dnsservers"));
        sb.AppendLine();
        sb.AppendLine("===== Name Resolution Policy Table (effective) =====");
        sb.AppendLine("NRPT rules override per-adapter DNS. A VPN or mesh client");
        sb.AppendLine("with rules here will win over GunWall's redirection.");
        sb.AppendLine(NetshOutput("namespace show effectivepolicy"));
        return sb.ToString();
    }

    private static bool RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
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

    /// <summary>Applies a DNS preset to all active adapters. Returns adapters changed.</summary>
    public static int Apply(DnsPreset preset)
    {
        int changed = 0;
        foreach (var name in ActiveAdapterNames())
        {
            bool ok;
            if (preset.Key == "auto")
            {
                ok = RunNetsh($"interface ipv4 set dnsservers name=\"{name}\" source=dhcp");
            }
            else
            {
                ok = RunNetsh($"interface ipv4 set dnsservers name=\"{name}\" static {preset.Primary} primary");
                if (ok && !string.IsNullOrEmpty(preset.Secondary))
                    RunNetsh($"interface ipv4 add dnsservers name=\"{name}\" address={preset.Secondary} index=2");
            }
            if (ok) changed++;
        }
        HostsFileService.FlushDns();
        return changed;
    }

    // ================================================= §3 Phase 2: system routing
    // These only ever touch PHYSICAL adapters (ActiveAdapterNames skips loopback and
    // tunnels), so a VPN's in-tunnel DNS is never modified - no leak risk.

    /// <summary>True when a VPN/overlay tunnel adapter is up (PIA, WireGuard, TAP,
    /// ZeroTier, Tailscale, ...) so the UI can explain DNS precedence honestly.</summary>
    public static bool TunnelAdapterUp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    d.Contains("vpn") || d.Contains("wintun") || d.Contains("wireguard") ||
                    d.Contains("openvpn") || d.Contains("tap-") ||
                    d.Contains("zerotier") || d.Contains("tailscale"))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Reads each active physical adapter's current IPv4 DNS setting from the
    /// registry (locale-independent, unlike parsing netsh output). An empty NameServer
    /// value means the adapter takes DNS from DHCP.</summary>
    public static List<SavedAdapterDns> CaptureAdapterDns()
    {
        var list = new List<SavedAdapterDns>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                            or NetworkInterfaceType.Tunnel) continue;
                string ns = "";
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + ni.Id);
                    ns = (k?.GetValue("NameServer") as string ?? "").Trim();
                }
                catch { }
                var servers = ns.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                list.Add(new SavedAdapterDns { Name = ni.Name, WasDhcp = servers.Count == 0, Servers = servers });
            }
        }
        catch { }
        return list;
    }

    /// <summary>True when a captured state just reflects GunWall's own redirect
    /// (every adapter static on 127.0.0.1) - e.g. after a crash before restore.
    /// Such a capture must never overwrite the genuine saved state.</summary>
    public static bool LooksLikeOurRedirect(List<SavedAdapterDns> captured) =>
        captured.Count > 0 && captured.All(s =>
            !s.WasDhcp && s.Servers.Count == 1 &&
            (s.Servers[0] == "127.0.0.1" || s.Servers[0] == "::1"));

    // NOTE: GunWall no longer changes this PC's DNS settings. The routing
    // feature was removed because taking over port 53 puts the firewall in
    // direct conflict with other software that also claims it - security
    // suites' DNS protection and VPN leak protection in particular - and the
    // failure mode is the machine appearing to lose its internet connection.
    // RestoreAdapters is retained so an upgrade from a version that HAD routed
    // DNS can put the machine back the way it found it.

    /// <summary>Puts adapters back exactly as captured: DHCP, or the original
    /// static server list. Returns adapters changed.</summary>
    /// <summary>
    /// Finds any adapter still pointing at GunWall's own loopback resolver and
    /// returns that family to automatic.
    ///
    /// This exists because restoring from the saved list alone is not enough:
    /// the old redirect applied to every adapter that was active at the moment
    /// it ran, so an adapter that appeared later - a VPN reconnecting, a VM
    /// adapter coming up - could be redirected without ever being recorded. It
    /// also saved nothing at all when it detected a crash-leftover state. Either
    /// way those adapters would keep pointing at 127.0.0.1 with no interface
    /// left to undo it. This sweep guarantees that cannot happen.
    ///
    /// Deliberately called ONLY during the one-time migration, never on every
    /// launch: pointing DNS at 127.0.0.1 by hand is now a legitimate way to use
    /// the resolver, and GunWall must not undo a choice the user made.
    /// </summary>
    public static int ClearLoopbackRedirects()
    {
        int cleared = 0;
        cleared += SweepFamily("ipv4", "127.0.0.1");
        cleared += SweepFamily("ipv6", "::1");
        if (cleared > 0) HostsFileService.FlushDns();
        return cleared;
    }

    private static int SweepFamily(string family, string loopback)
    {
        int cleared = 0;
        try
        {
            string dump = NetshOutput($"interface {family} show dnsservers");
            string? current = null;
            bool hit = false;

            foreach (string raw in dump.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int q1 = line.IndexOf('"');
                if (line.TrimStart().StartsWith("Configuration for interface", StringComparison.OrdinalIgnoreCase)
                    && q1 >= 0)
                {
                    if (current != null && hit && ResetFamily(family, current)) cleared++;
                    int q2 = line.IndexOf('"', q1 + 1);
                    current = q2 > q1 ? line.Substring(q1 + 1, q2 - q1 - 1) : null;
                    hit = false;
                    continue;
                }
                // Match the address as a whole token so "127.0.0.1" doesn't also
                // match something merely containing it.
                foreach (string tok in line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(tok, loopback, StringComparison.OrdinalIgnoreCase)) hit = true;
            }
            if (current != null && hit && ResetFamily(family, current)) cleared++;
        }
        catch { }
        return cleared;
    }

    private static bool ResetFamily(string family, string adapter)
    {
        bool ok = RunNetsh($"interface {family} set dnsservers name=\"{adapter}\" source=dhcp");
        if (ok)
            DiagnosticLog.Log($"Cleared leftover loopback DNS redirect on \"{adapter}\" ({family}).");
        return ok;
    }

    public static int RestoreAdapters(IEnumerable<SavedAdapterDns> saved)
    {
        int changed = 0;
        foreach (var s in saved)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            bool ok;
            if (s.WasDhcp || s.Servers.Count == 0)
            {
                ok = RunNetsh($"interface ipv4 set dnsservers name=\"{s.Name}\" source=dhcp");
            }
            else
            {
                ok = RunNetsh($"interface ipv4 set dnsservers name=\"{s.Name}\" static {s.Servers[0]} primary validate=no");
                for (int i = 1; i < s.Servers.Count; i++)
                    RunNetsh($"interface ipv4 add dnsservers name=\"{s.Name}\" address={s.Servers[i]} index={i + 1}");
            }
            // Return IPv6 DNS to automatic. We set ::1 on redirect, so we must
            // undo it; DHCP/RA restores whatever the network (or VPN) provides.
            RunNetsh($"interface ipv6 set dnsservers name=\"{s.Name}\" source=dhcp");
            if (ok) changed++;
        }
        HostsFileService.FlushDns();
        return changed;
    }
}
