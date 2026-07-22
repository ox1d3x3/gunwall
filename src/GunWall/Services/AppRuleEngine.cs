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
    public ConnFacts(string remoteIp, string scope, string country, string continent, int asn)
    {
        RemoteIp = remoteIp ?? "";
        Scope = scope ?? "";
        Country = country ?? "";
        Continent = continent ?? "";
        Asn = asn;
    }

    public string RemoteIp { get; }
    public string Scope { get; }       // "local" | "lan" | "internet"
    public string Country { get; }     // ISO-2, e.g. "RU"
    public string Continent { get; }   // "AF" | "AN" | "AS" | "EU" | "NA" | "OC" | "SA"
    public int Asn { get; }            // e.g. 13335
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
            default:
                return false;
        }
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
