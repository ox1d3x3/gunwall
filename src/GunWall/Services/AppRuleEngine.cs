using System.Net;
using System.Net.Sockets;
using GunWall.Models;

namespace GunWall.Services;

// =============================================================================
//  AppRuleEngine.cs  (§1 flagship — the entity-based rule engine)
//
//  A pure, side-effect-free evaluator for per-app ordered access policies.
//  Given a policy (an ordered list of allow/block rules + a default action) and
//  the facts about one observed connection, it returns a single verdict using
//  first-match-wins semantics — exactly the model simplewall / Portmaster power
//  users expect.
//
//  Everything here is deterministic and dependency-free, so it is exhaustively
//  unit-tested offline. Enforcement (turning a Block verdict into a WFP filter
//  + RST) lives in the UI sampling loop, reusing the proven reactive path; a
//  wrong rule can therefore only mis-block/allow a connection, never crash the
//  engine — matching the roadmap's risk note.
// =============================================================================

public enum RuleVerdict { Allow, Block }

/// <summary>The facts about one connection that entity rules match against.
/// All geo/scope fields are pre-computed by the caller so the engine stays
/// pure and testable.</summary>
public readonly struct ConnFacts
{
    public ConnFacts(string remoteIp, string scope, string country, string continent, int asn,
                     string domain = "")
    {
        RemoteIp = remoteIp ?? "";
        Scope = scope ?? "";
        Country = country ?? "";
        Continent = continent ?? "";
        Asn = asn;
        Domain = domain ?? "";
    }

    public string RemoteIp { get; }
    public string Scope { get; }       // "local" | "lan" | "internet"
    public string Country { get; }     // ISO-2, e.g. "RU"
    public string Continent { get; }   // "AF" | "AN" | "AS" | "EU" | "NA" | "OC" | "SA"
    public int Asn { get; }            // e.g. 13335

    /// <summary>The name this address was resolved from, when GunWall's own
    /// resolver answered the lookup; "" otherwise.</summary>
    public string Domain { get; }
}

public static class AppRuleEngine
{
    /// <summary>
    /// Evaluates one connection against an app's ordered policy. Walks the rules
    /// top-to-bottom; the first enabled rule whose entity matches decides the
    /// verdict. If nothing matches, the policy's default action applies.
    /// </summary>
    public static RuleVerdict Evaluate(AppAccessPolicy policy, ConnFacts facts)
    {
        if (policy != null)
        {
            foreach (var rule in policy.Rules)
            {
                if (!rule.Enabled) continue;
                if (Matches(rule, facts))
                    return rule.Action == "block" ? RuleVerdict.Block : RuleVerdict.Allow;
            }
            if (policy.DefaultBlock) return RuleVerdict.Block;
        }
        return RuleVerdict.Allow;
    }

    /// <summary>True if the connection's facts satisfy this rule's entity.</summary>
    public static bool Matches(AppAccessRule rule, ConnFacts facts)
    {
        switch (rule.EntityType)
        {
            case "any":
                return true;
            case "ip":
                return !string.IsNullOrEmpty(facts.RemoteIp) &&
                       string.Equals(facts.RemoteIp, rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "cidr":
                return IpInCidr(facts.RemoteIp, rule.Value);
            case "scope":
                return string.Equals(facts.Scope, rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "country":
                return facts.Country.Length > 0 &&
                       string.Equals(facts.Country, rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "continent":
                return facts.Continent.Length > 0 &&
                       string.Equals(facts.Continent, rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "asn":
                return facts.Asn != 0 && facts.Asn == NormalizeAsn(rule.Value);
            case "domain":
                return DomainMatches(facts.Domain, rule.Value);
            default:
                return false;
        }
    }

    /// <summary>
    /// Domain rule matching. A bare name matches itself and any subdomain, so
    /// "example.com" covers "cdn.example.com" - that is what people mean, and
    /// requiring a wildcard for the common case invites mistakes. A leading
    /// "*." is accepted and treated identically. Matching is case-insensitive
    /// and label-aware, so "notexample.com" never matches "example.com".
    /// </summary>
    public static bool DomainMatches(string domain, string pattern)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(pattern)) return false;

        string d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        string p = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        if (p.StartsWith("*.", StringComparison.Ordinal)) p = p[2..];
        if (p.Length == 0 || d.Length == 0) return false;

        if (d.Equals(p, StringComparison.Ordinal)) return true;
        // Subdomain: must align on a label boundary, so "evilexample.com" is
        // not a match for "example.com".
        return d.Length > p.Length && d.EndsWith(p, StringComparison.Ordinal)
            && d[d.Length - p.Length - 1] == '.';
    }

    /// <summary>Parses "AS13335", "as13335", or "13335" to 13335; 0 on garbage.</summary>
    public static int NormalizeAsn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        string v = value.Trim();
        if (v.StartsWith("AS", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        return int.TryParse(v, out int n) ? n : 0;
    }

    /// <summary>IPv4 CIDR membership test ("10.0.0.0/8"). IPv4-only, matching
    /// the GeoIP surface; anything unparseable is a non-match, never a throw.</summary>
    public static bool IpInCidr(string ip, string cidr)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(cidr)) return false;
            int slash = cidr.IndexOf('/');
            if (slash < 0) return false;
            string baseAddr = cidr[..slash].Trim();
            if (!int.TryParse(cidr[(slash + 1)..].Trim(), out int prefix) || prefix < 0 || prefix > 32)
                return false;
            if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork)
                return false;
            if (!IPAddress.TryParse(baseAddr, out var b) || b.AddressFamily != AddressFamily.InterNetwork)
                return false;

            uint ua = ToUint(a), ub = ToUint(b);
            uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            return (ua & mask) == (ub & mask);
        }
        catch { return false; }
    }

    private static uint ToUint(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }
}

/// <summary>
/// Pure address-range classifier: which scope a remote endpoint belongs to.
/// Shared by the §1 engine ("scope:" rules) and reused conceptually by §2.
/// IPv6 is classified coarsely (loopback / link-local-and-ULA / global).
/// </summary>
public static class IpScopeClassifier
{
    /// <summary>
    /// Whether an address may safely be given a machine-wide block filter.
    ///
    /// Stricter than "is it on the Internet": multicast and broadcast classify
    /// as internet scope for rule purposes, but a global block on, say, the
    /// mDNS group would break local network discovery for everything on the
    /// machine. A blocklist can name any of these - every hosts file maps its
    /// entries to a loopback address - so the check belongs here rather than in
    /// the caller's good intentions.
    /// </summary>
    public static bool IsPublicUnicast(string ip)
    {
        if (Classify(ip) != "internet") return false;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = addr.GetAddressBytes();
        if (b[0] >= 224) return false;                     // multicast (224/4) and reserved (240/4)
        if (b[0] == 0) return false;                       // "this network"
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;   // CGNAT 100.64/10
        return true;
    }

    public static string Classify(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        if (!IPAddress.TryParse(ip, out var addr)) return "";

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            if (b[0] == 127) return "local";
            if (b[0] == 10) return "lan";
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return "lan";
            if (b[0] == 192 && b[1] == 168) return "lan";
            if (b[0] == 169 && b[1] == 254) return "lan"; // link-local
            if (b[0] == 0) return "local";
            if (b[0] >= 224) return "internet"; // multicast/reserved -> treat as public
            return "internet";
        }
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(addr)) return "local";
            var b = addr.GetAddressBytes();
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return "lan"; // fe80::/10 link-local
            if ((b[0] & 0xFE) == 0xFC) return "lan";                 // fc00::/7 ULA
            return "internet";
        }
        return "";
    }
}
