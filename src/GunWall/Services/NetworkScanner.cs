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
    public sealed record Device(string Ip, string Mac, string Host, string Os = "",
                               string Kind = "", string Vendor = "", string Note = "")
    {
        /// <summary>True when this device chose its own MAC, so a note attached to
        /// it will be lost the next time it randomises. Derived rather than stored
        /// separately, so it cannot disagree with the NOTE column beside it.</summary>
        public bool MacIsRandom => IsRandomisedMac(Mac);
    }

    /// <summary>Supplies the note for a device. Set by the host, like Oui.</summary>
    public static Func<string, string>? NoteLookup { get; set; }

    /// <summary>Supplies vendor names, when a registry has been downloaded. Set by
    /// the host so the scanner does not own the table or its lifetime.</summary>
    public static OuiService? Oui { get; set; }

    /// <summary>TTL seen in each host's ping reply, keyed by address.
    ///
    /// The initial TTL an operating system stamps on outgoing packets is one of
    /// the few things that leaks its identity without probing for it, and the
    /// reply was already being received and thrown away here.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _ttl = new();

    /// <summary>A guess at the operating system from the reply TTL.
    ///
    /// Every common stack starts at 64, 128 or 255, and each router on the way
    /// subtracts one - so rounding UP to the nearest of those recovers the
    /// original. On a LAN the hop count is normally zero or one, which is what
    /// makes this reliable enough to show at all.
    ///
    /// It is a GUESS and is labelled as one. TTL can be changed by policy, a
    /// hardened host may lie deliberately, and 64 covers Linux, macOS, Android
    /// and iOS alike - so the label names the family it can actually distinguish
    /// rather than inventing a precision it does not have. Anything unrecognised
    /// returns empty rather than a nearest match: a blank cell is honest, and a
    /// confident wrong answer about what is on someone's network is not.</summary>
    private static string GuessOs(int ttl)
    {
        if (ttl <= 0) return "";
        if (ttl <= 64  && ttl >= 52)  return "Linux / macOS / Android";
        if (ttl <= 128 && ttl >= 116) return "Windows";
        if (ttl <= 255 && ttl >= 243) return "Router / embedded";
        return "";
    }

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
            // Cleared per scan: a device that has since gone, or been replaced at
            // the same address, must not inherit the previous run's reading.
            _ttl.Clear();

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
                            var reply = await p.SendPingAsync(ip, 600);
                            // The reply carries the sender's remaining TTL, which
                            // is the only free identifying signal in this exchange.
                            // It was being discarded. Options is null on some
                            // failure paths, hence the null-conditional.
                            if (reply?.Status == IPStatus.Success && reply.Options != null)
                                _ttl[ip] = reply.Options.Ttl;
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
            var gateways = GetGateways();
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
                    bool isGateway = gateways.Contains(ipLocal);

                    // A known gateway overrides the TTL guess. "Linux / macOS /
                    // Android" for the router is correct and useless; "Router
                    // (gateway)" comes from the routing table and is a fact.
                    string os = isGateway
                        ? "Router (gateway)"
                        : (_ttl.TryGetValue(ipLocal, out int t) ? GuessOs(t) : "");

                    string kind = isGateway ? "Gateway"
                                : IsRandomisedMac(macLocal) ? "Randomised MAC"
                                : "";

                    string vendor = Oui?.Lookup(macLocal) ?? "";
                    string note = NoteLookup?.Invoke(macLocal) ?? "";
                    lock (devices) devices.Add(new Device(ipLocal, macLocal, host, os, kind, vendor, note));
                }));
            }
            await Task.WhenAll(resolveTasks);
            devices.Sort((a, b) => CompareIp(a.Ip, b.Ip));
            progress?.Invoke(100);
        }
        catch { /* best effort */ }
        return devices;
    }

    /// <summary>Every default-gateway address this machine has.
    ///
    /// Read from the routing table, so it is KNOWN rather than guessed. The TTL
    /// heuristic labels a router "Linux / macOS / Android" and is technically
    /// right - most consumer routers do run Linux - while telling the reader
    /// nothing. "Router (gateway)" is a fact the machine already holds, and a fact
    /// beats an inference wherever one is available.</summary>
    private static HashSet<string> GetGateways()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                {
                    var a = g?.Address;
                    if (a == null) continue;
                    string t = a.ToString();
                    // A gateway of 0.0.0.0 means "none configured" on some adapters.
                    if (t.Length > 0 && t != "0.0.0.0" && t != "::") set.Add(t);
                }
            }
        }
        catch { }
        return set;
    }

    /// <summary>True when a MAC is locally administered - i.e. randomised.
    ///
    /// Bit 1 of the first octet is the U/L flag: set means the address was chosen
    /// by the device rather than assigned by a manufacturer. Every modern phone
    /// randomises its MAC per network by default, which is why a vendor lookup
    /// returns nothing for them. Saying "randomised" is far more useful than
    /// leaving the cell blank, because blank reads as a failure to look up rather
    /// than as a deliberate privacy feature working correctly.
    ///
    /// Computed from the address itself, so no lookup table and nothing to go
    /// stale.</summary>
    internal static bool IsRandomisedMac(string mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length < 2) return false;
        return int.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber,
                            null, out int first) && (first & 0x02) != 0;
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

        // Reverse DNS answers for almost nothing on a home network - one device in
        // nine, in the reported scan - because a PTR record only exists if the
        // router bothered to publish one. NetBIOS is the fallback that actually
        // works there: Windows machines, NAS boxes and printers all answer a node
        // status query on their own, without any server having recorded them.
        return await NetBiosNameAsync(ip);
    }

    /// <summary>Asks a host its own NetBIOS name (UDP 137, node status query).
    ///
    /// The wire format is small and fixed, so it is built by hand rather than
    /// marshalled: a 12-byte header, then the wildcard name "*" padded to 16 bytes
    /// and first-level encoded, then QTYPE=NBSTAT(0x21) QCLASS=IN(0x01).
    ///
    /// The reply carries a name table; the first entry is the machine name, padded
    /// with spaces to 15 characters with a one-byte suffix. Group names and the
    /// __MSBROWSE__ entry are skipped, since the workgroup is not what anyone
    /// looking at this column wants.
    ///
    /// Every length is bounds-checked against the actual datagram before it is
    /// read. A reply is unauthenticated data from an unknown device on the local
    /// network, so it is treated as hostile input rather than as a well-formed
    /// packet that happens to have arrived.</summary>
    private static async Task<string> NetBiosNameAsync(string ip)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var addr)) return "";

            var query = new byte[50];
            query[0] = 0x82; query[1] = 0x28;   // transaction id, arbitrary
            query[2] = 0x00; query[3] = 0x00;   // flags: standard query
            query[5] = 0x01;                    // one question
            query[12] = 0x20;                   // encoded name length: 32 bytes
            // "*" then 15 NULs, first-level encoded as two nibbles per byte.
            query[13] = (byte)('C' + 0);        // 'C','K' encodes '*' (0x2A)
            query[14] = (byte)('K');
            for (int i = 15; i < 45; i++) query[i] = (byte)'A';   // 'A','A' encodes 0x00
            query[45] = 0x00;                   // end of name
            query[46] = 0x00; query[47] = 0x21; // QTYPE = NBSTAT
            query[48] = 0x00; query[49] = 0x01; // QCLASS = IN

            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = 900;
            await udp.SendAsync(query, query.Length, new IPEndPoint(addr, 137));

            var recv = udp.ReceiveAsync();
            if (await Task.WhenAny(recv, Task.Delay(900)) != recv) return "";
            byte[] r = recv.Result.Buffer;

            // header(12) + echoed question(34) + RR name/type/class/ttl(12) +
            // rdlength(2) = 60, then a one-byte name count.
            const int NamesCountOffset = 56;
            if (r.Length <= NamesCountOffset) return "";
            int count = r[NamesCountOffset];
            int p = NamesCountOffset + 1;

            for (int i = 0; i < count && p + 18 <= r.Length; i++, p += 18)
            {
                string name = System.Text.Encoding.ASCII.GetString(r, p, 15).Trim();
                byte suffix = r[p + 15];
                bool group = (r[p + 16] & 0x80) != 0;
                if (group || suffix != 0x00) continue;          // workgroup entry
                if (name.Length == 0 || name.StartsWith("\u0001", StringComparison.Ordinal)) continue;
                return name;
            }
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
