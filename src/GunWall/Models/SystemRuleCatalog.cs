namespace GunWall.Models;

/// <summary>
/// One entry in the curated system-rule library. A preset is either "special"
/// (handled by dedicated engine logic, e.g. block-all-inbound / block-IPv6) or
/// a port/protocol rule applied generically. Direction is "out", "in", or
/// "both".
/// </summary>
public sealed record SystemRulePreset(
    string Key,
    string Name,
    string Description,
    string Category,      // "allow" or "block"
    bool Special,
    bool Block,
    string Direction,     // out | in | both
    string Protocol,      // Any | TCP | UDP
    int[] Ports);

/// <summary>
/// The built-in library of named system rules, mirroring (and extending) the
/// common preset rules a mature firewall ships. "Allow" rules permit a service
/// outbound; "Block" rules harden against risky traffic. Easy to extend.
/// </summary>
public static class SystemRuleCatalog
{
    public static readonly IReadOnlyList<SystemRulePreset> All = new List<SystemRulePreset>
    {
        // ----------------------------- Allow common services (outbound permits)
        new("svc_dns",   "DNS",   "Domain name resolution (port 53).", "allow", false, false, "out", "Any", new[]{53}),
        new("svc_dhcp",  "DHCP",  "Automatic IP address assignment (ports 67-68).", "allow", false, false, "out", "UDP", new[]{67,68}),
        new("svc_mdns",  "mDNS",  "Multicast DNS / local discovery (port 5353).", "allow", false, false, "out", "UDP", new[]{5353}),
        new("svc_llmnr", "LLMNR", "Link-local name resolution (port 5355).", "allow", false, false, "out", "UDP", new[]{5355}),
        new("svc_ssdp",  "SSDP / UPnP", "Device discovery on the LAN (port 1900).", "allow", false, false, "out", "UDP", new[]{1900}),
        new("svc_ntp",   "NTP",   "Network time synchronization (port 123).", "allow", false, false, "out", "UDP", new[]{123}),
        new("svc_http",  "HTTP",  "Unencrypted web traffic (port 80).", "allow", false, false, "out", "TCP", new[]{80}),
        new("svc_https", "HTTPS", "Encrypted web traffic (port 443).", "allow", false, false, "out", "TCP", new[]{443}),
        new("svc_quic",  "QUIC",  "HTTP/3 over UDP (port 443).", "allow", false, false, "out", "UDP", new[]{443}),
        new("svc_ssh",   "SSH",   "Secure shell (port 22).", "allow", false, false, "out", "TCP", new[]{22}),
        new("svc_ftp",   "FTP",   "File transfer (port 21).", "allow", false, false, "out", "TCP", new[]{21}),
        new("svc_smtp",  "SMTP",  "Outgoing mail (ports 25, 465, 587).", "allow", false, false, "out", "TCP", new[]{25,465,587}),
        new("svc_imap",  "IMAP",  "Incoming mail, IMAP (ports 143, 993).", "allow", false, false, "out", "TCP", new[]{143,993}),
        new("svc_pop3",  "POP3",  "Incoming mail, POP3 (ports 110, 995).", "allow", false, false, "out", "TCP", new[]{110,995}),

        // ----------------------------- Quick toggles (roadmap: "quick rule toggles")
        //
        // Both are ALLOW rules, so both sit at weight 0x0B - above per-app rules
        // and above the zero-trust baseline. That is the whole point of the allow
        // category and it is also its cost: an allow here opens the port for every
        // process, not just the one that needed it. The descriptions say so,
        // because a permit the user does not understand is worse than no permit.
        new("svc_winupdate", "Windows Update",
            "Delivery Optimization peer transfer (TCP/UDP 7680) and WSUS servers (TCP 8530, 8531). "
            + "Windows Update's own downloads go over ordinary HTTPS, which is governed per application - "
            + "if updates are stuck, allow the svchost service that runs them rather than turning this on. "
            + "These are the extra ports that HTTPS does not cover.",
            "allow", false, false, "out", "TCP", new[]{7680,8530,8531}),
        new("svc_winupdate_do", "Windows Update peer discovery",
            "The UDP half of Delivery Optimization (port 7680), used to find other PCs on the LAN that "
            + "already hold an update. Off means updates download from Microsoft directly, which is slower "
            + "on a network with several machines but talks to fewer peers.",
            "allow", false, false, "out", "UDP", new[]{7680}),
        new("svc_teredo", "Teredo (IPv6 over IPv4)",
            "IPv6 tunnelled inside UDP (port 3544), used when a network offers no native IPv6. "
            + "Some games and peer-to-peer applications rely on it.",
            "allow", false, false, "out", "UDP", new[]{3544}),
        new("svc_6to4", "6to4 / ISATAP (IPv6 transition)",
            "IPv6 carried directly inside IPv4 as protocol 41, with no port of its own. "
            + "Needed only if this PC reaches IPv6 through a 6to4 or ISATAP tunnel; native IPv6 and "
            + "Teredo do not use it.",
            "allow", false, false, "out", "41", System.Array.Empty<int>()),

        // ----------------------------- Harden against risky traffic (blocks)
        new("stealth", "Stealth mode",
            "Makes this PC quieter to port scans: blocks unsolicited inbound connections and suppresses the ICMP 'unreachable' replies that reveal closed ports.",
            "block", true, true, "both", "Any", System.Array.Empty<int>()),
        new("block_inbound", "Block all inbound connections",
            "Stops any device from initiating a connection to this PC. Outbound still works.",
            "block", true, true, "in", "Any", System.Array.Empty<int>()),
        new("block_listen", "Block listening sockets (advanced)",
            "Stops applications from opening a listening socket at all \u2014 one step earlier than blocking inbound accept. Good hardening for a desktop that doesn't host servers; remove it if a local server, game host, or peer-to-peer app needs to accept connections.",
            "block", true, true, "in", "Any", System.Array.Empty<int>()),
        new("block_forwarding", "Block routed (transit) traffic (advanced)",
            "Stops this PC from forwarding traffic between networks \u2014 the router role. Blocks a virtual machine, mesh VPN peer, or bridged device from using this PC as a transit hop to reach somewhere else. Leave off if you deliberately share this PC's connection (Internet Connection Sharing, a VM in bridged/NAT mode that needs routing, or an exit node).",
            "block", true, true, "both", "Any", System.Array.Empty<int>()),
        new("block_ipv6", "Block IPv6",
            "Blocks all IPv6 traffic. Useful if you only use IPv4 and want to reduce exposure.",
            "block", true, true, "both", "Any", System.Array.Empty<int>()),
        new("block_smb", "Block file sharing (SMB, port 445)",
            "Blocks SMB traffic, a common ransomware and lateral-movement vector.",
            "block", false, true, "both", "TCP", new[]{445}),
        new("block_netbios", "Block NetBIOS (ports 137-139)",
            "Legacy Windows networking ports, rarely needed and often probed.",
            "block", false, true, "both", "Any", new[]{137,138,139}),
        new("block_rdp_in", "Block inbound Remote Desktop (RDP, port 3389)",
            "Prevents incoming RDP connections - a frequent brute-force target.",
            "block", false, true, "in", "TCP", new[]{3389}),
        new("block_telnet", "Block Telnet (port 23)",
            "Blocks unencrypted Telnet, which should never be used over a network.",
            "block", false, true, "both", "TCP", new[]{23}),
    };
}
