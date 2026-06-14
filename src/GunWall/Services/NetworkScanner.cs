using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Discovers devices on the local network. It pings every host in the local
/// IPv4 /24, then reads the system ARP table (GetIpNetTable) to map IP to MAC,
/// and attempts a reverse-DNS hostname. Pure managed / IP Helper — no driver,
/// no external services. Best-effort throughout.
/// </summary>
public sealed class NetworkScanner
{
    public sealed record Device(string Ip, string Mac, string Host);

    /// <summary>
    /// Scans the local /24 around the machine's primary IPv4 and returns the
    /// discovered devices. Reports progress (0-100) via the optional callback.
    /// </summary>
    public static async Task<List<Device>> ScanAsync(Action<int>? progress = null)
    {
        var devices = new List<Device>();
        try
        {
            string? local = GetLocalIPv4();
            if (local == null) return devices;

            // Build the /24 base (e.g. 192.168.1.).
            int lastDot = local.LastIndexOf('.');
            string basePrefix = local[..(lastDot + 1)];

            // Ping all 254 hosts in parallel batches.
            var pingTasks = new List<Task>();
            int done = 0;
            for (int i = 1; i <= 254; i++)
            {
                string ip = basePrefix + i;
                pingTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var p = new Ping();
                        await p.SendPingAsync(ip, 500);
                    }
                    catch { }
                    finally
                    {
                        int d = System.Threading.Interlocked.Increment(ref done);
                        progress?.Invoke(d * 100 / 254);
                    }
                }));
            }
            await Task.WhenAll(pingTasks);

            // Read the ARP table — hosts that responded now have MAC entries.
            foreach (var (ip, mac) in ReadArpTable())
            {
                if (!ip.StartsWith(basePrefix, StringComparison.Ordinal)) continue;
                string host = await ResolveHostAsync(ip);
                devices.Add(new Device(ip, mac, host));
            }
            devices.Sort((a, b) => CompareIp(a.Ip, b.Ip));
        }
        catch { /* best effort */ }
        return devices;
    }

    private static string? GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                    return ua.Address.ToString();
            }
        }
        return null;
    }

    private static async Task<string> ResolveHostAsync(string ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip);
            return entry.HostName;
        }
        catch { return ""; }
    }

    private static int CompareIp(string a, string b)
    {
        try
        {
            var pa = a.Split('.'); var pb = b.Split('.');
            for (int i = 0; i < 4; i++)
            {
                int c = int.Parse(pa[i]).CompareTo(int.Parse(pb[i]));
                if (c != 0) return c;
            }
        }
        catch { }
        return string.CompareOrdinal(a, b);
    }

    // ---- ARP table via IP Helper (GetIpNetTable) ----
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        public byte mac0; public byte mac1; public byte mac2;
        public byte mac3; public byte mac4; public byte mac5;
        public byte mac6; public byte mac7;
        public int dwAddr;
        public int dwType;
    }

    private static IEnumerable<(string Ip, string Mac)> ReadArpTable()
    {
        var results = new List<(string, string)>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return results;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return results;
            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
            IntPtr ptr = buffer + 4;
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(ptr);
                ptr += rowSize;
                if (row.dwPhysAddrLen != 6) continue;          // skip non-Ethernet
                if (row.dwType is 2 or 4 or 0) { /* keep dynamic/static */ }
                var ipBytes = BitConverter.GetBytes(row.dwAddr);
                string ip = $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{ipBytes[3]}";
                string mac = $"{row.mac0:X2}:{row.mac1:X2}:{row.mac2:X2}:" +
                             $"{row.mac3:X2}:{row.mac4:X2}:{row.mac5:X2}";
                if (mac == "00:00:00:00:00:00" || mac == "FF:FF:FF:FF:FF:FF") continue;
                results.Add((ip, mac));
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
        return results;
    }
}
