using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace GunWall.Services;

/// <summary>
/// Names from multicast DNS (mDNS / Bonjour / DNS-SD) for the network scan
/// (0.99.181).
///
/// Reverse DNS names almost nothing on a home network and NetBIOS names only
/// Windows machines, NAS boxes and printers. Phones, TVs, speakers, Chromecasts,
/// Apple devices and smart-home gear answer neither - but they announce
/// themselves over mDNS, often with the name their owner gave them ("Living Room
/// TV", "Mahabub's iPhone").
///
/// Two ways of asking, because each misses things the other catches:
///
///  1. <see cref="DiscoverAsync"/> - one multicast query on each LAN adapter for
///     the service types that carry a friendly name, plus the DNS-SD "which
///     services exist?" meta-query, then a second round for anything learned
///     from it. Queries are sent from an ordinary port (not 5353), which makes
///     them "legacy unicast" queries (RFC 6762 section 6.7): responders reply
///     straight to that port, so nothing needs to share Windows' own mDNS socket.
///
///  2. <see cref="ReverseNameAsync"/> - for a device still without a name, the
///     reverse-lookup question (PTR d.c.b.a.in-addr.arpa) sent to that device
///     alone on port 5353 (RFC 6762 section 5.5). Its reply comes back from the
///     address it was sent to, so a stateful firewall passes it even where
///     replies to multicast are not allowed.
///
/// Every reply is unauthenticated data from an unknown device, so the parser
/// treats it as hostile input: every length is checked against the datagram,
/// name compression may only point backwards (no loops), and names are capped
/// and stripped of control characters before they reach the screen.
///
/// Read-only: it only asks. It never answers queries, registers a name, or
/// changes anything on the network.
/// </summary>
public static class MdnsResolver
{
    /// <summary>What a device announced about itself.</summary>
    /// <param name="Name">The friendliest name it gave (e.g. "Living Room TV").</param>
    /// <param name="Model">Model, when announced (e.g. "Google Nest Mini").</param>
    /// <param name="Host">Its mDNS host name (e.g. "Mahabubs-iPhone.local").</param>
    /// <param name="Services">Readable names of what it announced, comma-separated.</param>
    public sealed record Info(string Name, string Model, string Host, string Services, string NameFrom = "");

    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Service types that carry a usable name, best first. The number is
    /// the priority when a device announces several (lower wins); the label is
    /// what the scan shows in the tooltip.</summary>
    private static readonly (string Type, int Rank, string Label)[] Known =
    {
        ("_googlecast._tcp", 1, "Google Cast"),          // TXT fn = the name set in Google Home
        ("_amzn-wplay._tcp", 1, "Fire TV"),              // TXT n = the device name
        ("_airplay._tcp", 2, "AirPlay"),
        ("_companion-link._tcp", 2, "Apple device"),
        ("_androidtvremote2._tcp", 2, "Android TV"),
        ("_raop._tcp", 3, "AirPlay audio"),              // instance "MAC@Name"
        ("_hap._tcp", 3, "HomeKit"),
        ("_ipp._tcp", 3, "Printer"),
        ("_printer._tcp", 3, "Printer"),
        ("_pdl-datastream._tcp", 3, "Printer"),
        ("_spotify-connect._tcp", 4, "Spotify Connect"),
        ("_smb._tcp", 4, "File sharing"),
        ("_afpovertcp._tcp", 4, "File sharing"),
        ("_device-info._tcp", 4, "Device info"),
        ("_esphomelib._tcp", 4, "ESPHome"),
        ("_workstation._tcp", 5, "Workstation"),         // instance "name [mac]"
        ("_sftp-ssh._tcp", 6, "SSH"),
        ("_ssh._tcp", 6, "SSH"),
        ("_http._tcp", 7, "Web"),
    };

    private const string ServicesMeta = "_services._dns-sd._udp.local";

    // ------------------------------------------------------------------ discovery

    /// <summary>Multicast discovery on every private IPv4 adapter. Returns what
    /// was announced, keyed by IPv4 address. Never throws; an empty result means
    /// nothing answered (or multicast is blocked on this network).</summary>
    public static async Task<Dictionary<string, Info>> DiscoverAsync(TimeSpan perRound)
    {
        var col = new Collector();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in LocalLanAddresses())
            {
                try
                {
                    // Not bound explicitly: Windows binds it on the first send, as
                    // it does for the NetBIOS query. An explicit bind to a LAN
                    // address is what can raise Windows Firewall's "allow access"
                    // prompt; the outgoing adapter is chosen by the option below.
                    var udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                                               local.GetAddressBytes());
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    sockets.Add(udp);
                }
                catch { /* this adapter will not take multicast; the others still can */ }
            }
            if (sockets.Count == 0) return new();

            // Round 1: the meta-query plus every known type, in one packet.
            var first = new List<(string, ushort)> { (ServicesMeta, 12) };
            first.AddRange(Known.Select(k => (k.Type + ".local", (ushort)12)));
            await RoundAsync(sockets, first, col, perRound);

            // Round 2: service types learned from the meta-query that were not
            // asked for, and A records for host names seen without an address.
            var second = new List<(string, ushort)>();
            foreach (var t in col.AnnouncedTypes())
                if (!Known.Any(k => string.Equals(k.Type + ".local", t, StringComparison.OrdinalIgnoreCase)))
                    second.Add((t, 12));
            foreach (var h in col.HostsWithoutAddress()) second.Add((h, 1));
            if (second.Count > 0)
                await RoundAsync(sockets, second.Take(40).ToList(), col, perRound);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Log($"mDNS discovery stopped early: {ex.GetType().Name}.");
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        return col.Result();
    }

    private static async Task RoundAsync(List<UdpClient> sockets, List<(string Name, ushort Type)> questions,
                                         Collector col, TimeSpan window)
    {
        // A packet holds up to ~9 KB in mDNS, but some stacks drop anything over
        // one Ethernet frame, so long lists go in several packets.
        foreach (var chunk in questions.Chunk(12))
        {
            byte[] q;
            try { q = BuildQuery(chunk); }
            catch (ArgumentException) { continue; }   // a name learned from the network that is not a valid query
            foreach (var s in sockets)
                try { await s.SendAsync(q, q.Length, Group); } catch { }
        }

        using var cts = new CancellationTokenSource(window);
        await Task.WhenAll(sockets.Select(s => ReceiveUntilAsync(s, col, cts.Token)));
    }

    private static async Task ReceiveUntilAsync(UdpClient s, Collector col, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await s.ReceiveAsync(ct); }
            // Windows reports an ICMP "port unreachable" as a reset on the NEXT
            // receive. It says nothing about the other devices: keep listening.
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch { return; }        // window over, or the socket was closed
            try { col.Add(r.RemoteEndPoint.Address, r.Buffer); }
            catch { /* one bad packet must not end listening for the rest */ }
        }
    }

    // ------------------------------------------------------------------ per device

    /// <summary>Asks one device for its own host name over unicast mDNS. Returns
    /// "" if it does not answer in time. The ".local" suffix is kept: it says
    /// where the name came from and that it only resolves on this network.</summary>
    public static async Task<string> ReverseNameAsync(string ip, TimeSpan timeout)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
                return "";
            var b = addr.GetAddressBytes();
            string arpa = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            byte[] q = BuildQuery(new[] { (arpa, (ushort)12) });
            await udp.SendAsync(q, q.Length, new IPEndPoint(addr, 5353));

            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token); }
                catch { return ""; }
                if (!r.RemoteEndPoint.Address.Equals(addr)) continue;   // only the device asked
                var rrs = new List<Rr>();
                if (!Parse(r.Buffer, rrs)) continue;
                foreach (var rr in rrs)
                    if (rr.Type == 12 && string.Equals(rr.Name, arpa, StringComparison.OrdinalIgnoreCase)
                        && rr.Target.Length > 0)
                        return Clean(rr.Target);
            }
        }
        catch { }
        return "";
    }

    // ------------------------------------------------------------------ wire format

    /// <summary>A standard query: id 0 and no flags, as RFC 6762 asks. Each
    /// question sets the unicast-response bit ("QU"), which is what most stacks
    /// already do for a query from a port other than 5353.</summary>
    internal static byte[] BuildQuery(IReadOnlyCollection<(string Name, ushort Type)> questions)
    {
        var buf = new List<byte>(512) { 0, 0, 0, 0 };              // id, flags
        buf.Add((byte)(questions.Count >> 8)); buf.Add((byte)questions.Count);
        buf.AddRange(new byte[6]);                                  // an, ns, ar = 0
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63) throw new ArgumentException("bad label");
                buf.Add((byte)bytes.Length);
                buf.AddRange(bytes);
            }
            buf.Add(0);
            buf.Add((byte)(type >> 8)); buf.Add((byte)type);
            buf.Add(0x80); buf.Add(0x01);                           // QU + class IN
        }
        return buf.ToArray();
    }

    /// <summary>One resource record, reduced to what the scan uses.</summary>
    internal sealed class Rr
    {
        public string Name = "";
        public ushort Type;
        public uint Ttl;
        public string Target = "";                                  // PTR / SRV target
        public IPAddress? Address;                                  // A
        public Dictionary<string, string>? Txt;                     // TXT key=value
    }

    /// <summary>Parses a DNS response into records. False if it is not a
    /// response or is malformed before the first record; records already read
    /// from a damaged packet are kept.</summary>
    internal static bool Parse(byte[] p, List<Rr> into)
    {
        if (p.Length < 12) return false;
        if ((p[2] & 0x80) == 0) return false;                       // a query, not a response
        int qd = U16(p, 4), records = U16(p, 6) + U16(p, 8) + U16(p, 10);
        int off = 12;
        for (int i = 0; i < qd; i++)
        {
            if (!ReadName(p, ref off, out _)) return false;
            off += 4;
            if (off > p.Length) return false;
        }
        for (int i = 0; i < Math.Min(records, 256); i++)
        {
            if (!ReadName(p, ref off, out string name)) break;
            if (off + 10 > p.Length) break;
            var rr = new Rr
            {
                Name = name,
                Type = (ushort)U16(p, off),
                Ttl = (uint)((p[off + 4] << 24) | (p[off + 5] << 16) | (p[off + 6] << 8) | p[off + 7]),
            };
            int len = U16(p, off + 8);
            int start = off + 10;
            if (start + len > p.Length) break;
            off = start + len;

            switch (rr.Type)
            {
                case 1 when len == 4:
                    rr.Address = new IPAddress(new[] { p[start], p[start + 1], p[start + 2], p[start + 3] });
                    break;
                case 12:
                {
                    int t = start;
                    if (ReadName(p, ref t, out string target) && t <= start + len) rr.Target = target;
                    break;
                }
                case 33 when len >= 7:
                {
                    int t = start + 6;                              // priority, weight, port
                    if (ReadName(p, ref t, out string target) && t <= start + len) rr.Target = target;
                    break;
                }
                case 16:
                    rr.Txt = ReadTxt(p, start, len);
                    break;
                default:
                    continue;                                       // not used: don't keep it
            }
            into.Add(rr);
        }
        return true;
    }

    /// <summary>Reads a possibly-compressed name. Compression pointers must point
    /// backwards, which makes a loop impossible; total length is capped at 255 as
    /// in DNS itself.</summary>
    internal static bool ReadName(byte[] p, ref int off, out string name)
    {
        name = "";
        var labels = new List<string>();
        int pos = off, total = 0;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            if (pos >= p.Length) return false;
            int len = p[pos];
            if (len == 0)
            {
                if (!jumped) off = pos + 1;
                name = string.Join(".", labels);
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= p.Length) return false;
                int target = ((len & 0x3F) << 8) | p[pos + 1];
                if (target >= pos) return false;                    // forward or self: refuse
                if (!jumped) off = pos + 2;
                jumped = true;
                pos = target;
                continue;
            }
            if ((len & 0xC0) != 0) return false;                    // reserved label types
            if (pos + 1 + len > p.Length) return false;
            total += len + 1;
            if (total > 255) return false;
            labels.Add(Encoding.UTF8.GetString(p, pos + 1, len));
            pos += 1 + len;
        }
        return false;
    }

    private static Dictionary<string, string> ReadTxt(byte[] p, int start, int len)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = start, end = start + len;
        while (i < end)
        {
            int n = p[i++];
            if (n == 0) continue;
            if (i + n > end) break;
            string s = Encoding.UTF8.GetString(p, i, n);
            i += n;
            int eq = s.IndexOf('=');
            string k = eq < 0 ? s : s[..eq];
            if (k.Length > 0 && !d.ContainsKey(k)) d[k] = eq < 0 ? "" : s[(eq + 1)..];
        }
        return d;
    }

    private static int U16(byte[] p, int i) => (p[i] << 8) | p[i + 1];

    /// <summary>For display: no control characters, no trailing dot, at most 63
    /// characters.</summary>
    internal static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.TrimEnd('.'))
            if (!char.IsControl(c)) sb.Append(c);
        string t = sb.ToString().Trim();
        return t.Length <= 63 ? t : t[..63];
    }

    // ------------------------------------------------------------------ collecting

    /// <summary>Gathers records from every reply and decides, per address, the
    /// name to show. Thread-safe: replies arrive on several sockets at once.</summary>
    private sealed class Collector
    {
        private readonly object _gate = new();
        // instance full name -> address of the device that sent it (fallback when
        // no A record ties its SRV target to an address)
        private readonly Dictionary<string, IPAddress> _instanceFrom = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _srvTarget = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _txt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> _hostAddr = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IPAddress, string> _addrHost = new();
        private readonly HashSet<string> _types = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);

        public void Add(IPAddress from, byte[] packet)
        {
            var rrs = new List<Rr>();
            if (!Parse(packet, rrs)) return;
            lock (_gate)
            {
                foreach (var rr in rrs)
                {
                    if (rr.Ttl == 0) continue;                      // a "goodbye": the record is going away
                    switch (rr.Type)
                    {
                        case 12 when string.Equals(rr.Name, ServicesMeta, StringComparison.OrdinalIgnoreCase):
                            if (rr.Target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && _types.Count < 64)
                                _types.Add(rr.Target);
                            break;
                        case 12 when rr.Target.Length > 0 && rr.Name.StartsWith("_", StringComparison.Ordinal):
                            if (_instances.Count < 512) _instances.Add(rr.Target);
                            _instanceFrom.TryAdd(rr.Target, from);
                            break;
                        case 33 when rr.Target.Length > 0:
                            _srvTarget[rr.Name] = rr.Target;
                            _instanceFrom.TryAdd(rr.Name, from);
                            if (_instances.Count < 512) _instances.Add(rr.Name);
                            break;
                        case 16 when rr.Txt != null:
                            _txt[rr.Name] = rr.Txt;
                            break;
                        case 1 when rr.Address != null:
                            _hostAddr[rr.Name] = rr.Address;
                            _addrHost.TryAdd(rr.Address, rr.Name);
                            break;
                    }
                }
            }
        }

        public List<string> AnnouncedTypes() { lock (_gate) return _types.ToList(); }

        public List<string> HostsWithoutAddress()
        {
            lock (_gate)
                return _srvTarget.Values.Where(h => !_hostAddr.ContainsKey(h))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }

        public Dictionary<string, Info> Result()
        {
            lock (_gate)
            {
                // address -> (best rank, name, model, services)
                var best = new Dictionary<string, (int Rank, string Name, string Model, SortedSet<string> Services, string From)>();
                foreach (var inst in _instances)
                {
                    var (type, label, rank) = Classify(inst);
                    if (type.Length == 0) continue;

                    IPAddress? addr = null;
                    if (_srvTarget.TryGetValue(inst, out var target)) _hostAddr.TryGetValue(target, out addr);
                    addr ??= _instanceFrom.GetValueOrDefault(inst);
                    if (addr == null || addr.AddressFamily != AddressFamily.InterNetwork) continue;
                    string key = addr.ToString();

                    _txt.TryGetValue(inst, out var txt);
                    string name = FriendlyName(inst, type, txt);
                    // A service names the device only when the name looks like one a
                    // person gave it. Android announces tags such as
                    // "nearby-presence-nsd-<digits>" (0.99.185) and "I1g20E38n14AAA"
                    // (0.99.186); since 0.99.187 this applies to every service type.
                    if (LooksMachineGenerated(name)) name = "";
                    string model = Model(txt);

                    if (!best.TryGetValue(key, out var cur))
                        cur = (int.MaxValue, "", "", new SortedSet<string>(StringComparer.OrdinalIgnoreCase), "");
                    cur.Services.Add(label);
                    if (name.Length > 0 && rank < cur.Rank) { cur.Rank = rank; cur.Name = name; cur.From = type; }
                    if (cur.Model.Length == 0 && model.Length > 0) cur.Model = model;
                    best[key] = cur;
                }

                var result = new Dictionary<string, Info>();
                foreach (var (ip, v) in best)
                {
                    string host = IPAddress.TryParse(ip, out var a) && _addrHost.TryGetValue(a, out var h) ? Clean(h) : "";
                    result[ip] = new Info(v.Name, v.Model, host, string.Join(", ", v.Services), v.From);
                }
                // Devices that gave only an address record for their host name.
                foreach (var (addr, h) in _addrHost)
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !result.ContainsKey(addr.ToString()))
                        result[addr.ToString()] = new Info("", "", Clean(h), "");
                return result;
            }
        }

        /// <summary>(service type, readable label, rank) for an instance name such
        /// as "Living Room TV._googlecast._tcp.local". Unknown types rank last but
        /// still name the device.</summary>
        private static (string Type, string Label, int Rank) Classify(string instance)
        {
            int i = instance.IndexOf("._", StringComparison.Ordinal);
            if (i <= 0) return ("", "", 0);
            string type = instance[(i + 1)..];
            if (type.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) type = type[..^6];
            foreach (var k in Known)
                if (string.Equals(k.Type, type, StringComparison.OrdinalIgnoreCase)) return (k.Type, k.Label, k.Rank);
            // "_name._tcp" -> "name"; a random hex type name reads as "Other".
            string label = type.Split('.')[0].TrimStart('_');
            if (label.Length == 0 || LooksMachineGenerated(label)) label = "Other";
            return (type, label, 8);
        }

        private static string FriendlyName(string instance, string type, Dictionary<string, string>? txt)
        {
            // The instance label is everything before "._type" - it may itself
            // contain dots ("Mahabub's iPad.local" style names exist).
            int i = instance.IndexOf("._", StringComparison.Ordinal);
            string label = i > 0 ? instance[..i] : instance;

            if (type == "_googlecast._tcp" && txt != null && txt.TryGetValue("fn", out var fn) && fn.Length > 0)
                return Clean(fn);                                   // instance is "Chromecast-<hex>"
            if (type == "_amzn-wplay._tcp" && txt != null && txt.TryGetValue("n", out var n) && n.Length > 0)
                return Clean(n);
            if (type == "_raop._tcp")
            {
                int at = label.IndexOf('@');                        // "A1B2C3D4E5F6@Kitchen"
                if (at >= 0) label = label[(at + 1)..];
            }
            if (type == "_workstation._tcp")
            {
                int br = label.LastIndexOf(" [", StringComparison.Ordinal);   // "nas [00:11:22:..]"
                if (br > 0) label = label[..br];
            }
            return Clean(label);
        }

        private static string Model(Dictionary<string, string>? txt)
        {
            if (txt == null) return "";
            foreach (var k in new[] { "md", "ty", "model", "am", "usb_MDL" })
                if (txt.TryGetValue(k, out var v) && v.Trim().Length > 0) return Clean(v);
            return "";
        }
    }

    /// <summary>True for an identifier rather than a name a person gave:
    ///  - no spaces and a long run of hex digits or numbers, or Android's
    ///    "nearby-" tags ("nearby-presence-nsd-29517384", 0.99.185); or
    ///  - one unbroken token of ten or more characters mixing upper case, lower
    ///    case and at least three digits ("I1g20E38n14AAA", seen from an Android
    ///    phone in 0.99.186). Real names either have a separator or are short
    ///    ("Living Room TV", "Kitchen-Speaker", "MacBookPro16", "Pixel7a").</summary>
    internal static bool LooksMachineGenerated(string s)
    {
        if (s.Length == 0 || s.Contains(' ')) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "[0-9A-Fa-f]{8,}|[0-9]{5,}")
            || s.StartsWith("nearby-", StringComparison.OrdinalIgnoreCase))
            return true;
        bool unbroken = s.IndexOfAny(new[] { '-', '_', '.', '\'', '\u2019' }) < 0;
        return unbroken && s.Length >= 10
               && s.Count(char.IsDigit) >= 3 && s.Any(char.IsUpper) && s.Any(char.IsLower);
    }

    // ------------------------------------------------------------------ adapters

    /// <summary>This machine's private IPv4 addresses on adapters that are up -
    /// the same set the scan sweeps.</summary>
    private static List<IPAddress> LocalLanAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!ni.SupportsMulticast) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.GetAddressBytes();
                    bool priv = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                    if (priv && !list.Contains(a)) list.Add(a);
                }
            }
        }
        catch { }
        return list;
    }
}
