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
}
