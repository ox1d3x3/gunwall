using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Discovers devices on the local network(s). It pings every host across each
/// active private IPv4 /24 the machine is attached to (handling multiple
/// adapters — Wi-Fi, Ethernet, VPN, virtual switches), then reads the system
/// ARP table to map IP to MAC and attempts a reverse-DNS hostname. Pure managed
/// / IP Helper — no driver, no external services. Best-effort throughout.
/// </summary>
public sealed class NetworkScanner
{
    public sealed record Device(string Ip, string Mac, string Host);

    public static async Task<List<Device>> ScanAsync(Action<int>? progress = null)
    {
        var devices = new List<Device>();
        try
        {
            // Gather every private /24 the machine sits on (across all adapters).
            var prefixes = GetLocalSubnetPrefixes();
            if (prefixes.Count == 0) prefixes.Add("192.168.1."); // sensible fallback

            int totalHosts = prefixes.Count * 254;
            int done = 0;
            var pingTasks = new List<Task>();

            foreach (var prefix in prefixes)
            {
                for (int i = 1; i <= 254; i++)
                {
                    string ip = prefix + i;
                    pingTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using var p = new Ping();
                            // Pinging forces an ARP exchange even if ICMP is
                            // dropped, so the device still lands in the ARP table.
                            await p.SendPingAsync(ip, 600);
                        }
                        catch { }
                        finally
                        {
                            int d = System.Threading.Interlocked.Increment(ref done);
                            progress?.Invoke(Math.Min(99, d * 100 / Math.Max(1, totalHosts)));
                        }
                    }));
                }
            }
            await Task.WhenAll(pingTasks);

            // Read the ARP table. Include every valid private unicast neighbour
            // across ALL adapters — do NOT restrict to a single guessed subnet
            // (that was the bug that hid LAN devices behind a VPN adapter).
            var seen = new HashSet<string>();
            var arp = ReadArpTable();
            var resolveTasks = new List<Task>();

            foreach (var (ip, mac) in arp)
            {
                if (!IsRealLanIp(ip)) continue;
                if (!seen.Add(ip)) continue;
                string ipLocal = ip;
                string macLocal = mac;
                resolveTasks.Add(Task.Run(async () =>
                {
                    string host = await ResolveHostAsync(ipLocal);
                    lock (devices) devices.Add(new Device(ipLocal, macLocal, host));
                }));
            }
            await Task.WhenAll(resolveTasks);
            devices.Sort((a, b) => CompareIp(a.Ip, b.Ip));
            progress?.Invoke(100);
        }
        catch { /* best effort */ }
        return devices;
    }

    /// <summary>All private /24 prefixes the machine is attached to.</summary>
    private static List<string> GetLocalSubnetPrefixes()
    {
        var prefixes = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (!IsPrivate(ip)) continue;
                    string prefix = ip[..(ip.LastIndexOf('.') + 1)];
                    if (!prefixes.Contains(prefix)) prefixes.Add(prefix);
                }
            }
        }
        catch { }
        return prefixes;
    }

    private static bool IsPrivate(string ip)
    {
        var p = ip.Split('.');
        if (p.Length != 4 || !int.TryParse(p[0], out int a) || !int.TryParse(p[1], out int b))
            return false;
        if (a == 10) return true;                          // 10.0.0.0/8
        if (a == 192 && b == 168) return true;             // 192.168.0.0/16
        if (a == 172 && b >= 16 && b <= 31) return true;   // 172.16.0.0/12
        if (a == 169 && b == 254) return true;             // link-local
        return false;
    }

    /// <summary>A real LAN neighbour IP (private, not network/broadcast/multicast).</summary>
    private static bool IsRealLanIp(string ip)
    {
        if (!IsPrivate(ip)) return false;
        var p = ip.Split('.');
        if (!int.TryParse(p[3], out int last)) return false;
        if (last == 0 || last == 255) return false;        // network / broadcast
        if (int.TryParse(p[0], out int a) && a >= 224) return false; // multicast
        return true;
    }

    private static async Task<string> ResolveHostAsync(string ip)
    {
        try
        {
            // Short timeout so a device without a PTR record doesn't stall.
            var task = Dns.GetHostEntryAsync(ip);
            _ = task.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (await Task.WhenAny(task, Task.Delay(1200)) == task &&
                task.Status == TaskStatus.RanToCompletion)
                return task.Result.HostName;
        }
        catch { }
        return "";
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
        GetIpNetTable(IntPtr.Zero, ref size, false);  // first call sizes the buffer
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
                if (row.dwPhysAddrLen != 6) continue;        // Ethernet MACs only
                if (row.dwType == 2) continue;               // skip INVALID entries
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
