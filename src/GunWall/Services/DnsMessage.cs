using System;
using System.Text;

namespace GunWall.Services;

/// <summary>What GunWall's resolver did with a single DNS query.</summary>
public enum DnsAction { Forwarded, Cached, Blocked, Cloaked, Error }

/// <summary>One line in the resolver's query log. Plain data — no WPF types — so
/// the engine that produces it can be unit-tested in isolation.</summary>
public sealed record DnsLogEntry(DateTime Time, string Domain, string Type, DnsAction Action)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string ActionText => Action.ToString();
}

/// <summary>
/// Minimal DNS wire-format helpers: read the question from a query, build a small
/// NXDOMAIN reply, rewrite the message id, and read the smallest answer TTL.
///
/// This is pure byte manipulation — no sockets, no WPF — so it can be exercised by
/// an offline test harness. We deliberately only parse what we need (the first
/// question) and forward upstream bytes verbatim otherwise, which keeps us correct
/// without re-implementing a full resolver.
///
/// Header layout (12 bytes):
///   ID(2) Flags(2) QDCOUNT(2) ANCOUNT(2) NSCOUNT(2) ARCOUNT(2)
/// Flags byte 0: QR(1) Opcode(4) AA(1) TC(1) RD(1)
/// Flags byte 1: RA(1) Z(3) RCODE(4)
/// </summary>
public static class DnsMessage
{
    /// <summary>Read the id and the first question's name + type from a query.</summary>
    public static bool TryReadQuestion(byte[] msg, out string name, out ushort qtype, out ushort id)
    {
        name = ""; qtype = 0; id = 0;
        if (msg == null || msg.Length < 12) return false;
        id = (ushort)((msg[0] << 8) | msg[1]);
        int qd = (msg[4] << 8) | msg[5];
        if (qd < 1) return false;
        int pos = 12;
        if (!TryReadName(msg, ref pos, out name)) return false;
        if (pos + 4 > msg.Length) return false;
        qtype = (ushort)((msg[pos] << 8) | msg[pos + 1]);
        return true;
    }

    // Reads a DNS name at pos, following compression pointers. On return, pos is
    // advanced past the name at THIS level (past the terminating zero, or past the
    // 2-byte pointer if the name began with one).
    private static bool TryReadName(byte[] msg, ref int pos, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        int p = pos;
        int hops = 0;
        bool jumped = false;
        int afterPointer = -1;

        while (true)
        {
            if (p < 0 || p >= msg.Length) return false;
            int len = msg[p];

            if ((len & 0xC0) == 0xC0)              // compression pointer
            {
                if (p + 1 >= msg.Length) return false;
                int ptr = ((len & 0x3F) << 8) | msg[p + 1];
                if (!jumped) afterPointer = p + 2;
                jumped = true;
                if (++hops > 20) return false;     // guard against pointer loops
                p = ptr;
                continue;
            }

            if (len == 0) { p++; break; }          // end of name

            p++;                                   // a normal label
            if (p + len > msg.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            for (int i = 0; i < len; i++) sb.Append((char)msg[p + i]);
            p += len;
            if (sb.Length > 255) return false;
        }

        name = sb.ToString();
        pos = jumped ? afterPointer : p;
        return true;
    }

    /// <summary>Friendly name for a query type code (A, AAAA, HTTPS, ...).</summary>
    public static string TypeName(ushort qtype) => qtype switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        43 => "DS",
        48 => "DNSKEY",
        64 => "SVCB",
        65 => "HTTPS",
        255 => "ANY",
        _ => "TYPE" + qtype
    };

    /// <summary>Build an NXDOMAIN reply that echoes the query's id and question.</summary>
    public static byte[] BuildNxDomain(byte[] query)
    {
        int qend = QuestionEnd(query);
        bool haveQuestion = qend > 0;
        if (!haveQuestion) qend = Math.Min(query?.Length ?? 0, 12);
        if (qend < 12) qend = 12;

        var resp = new byte[qend];
        if (query != null) Array.Copy(query, resp, Math.Min(qend, query.Length));

        // QR=1, keep Opcode + RD from the query; RA=1, RCODE=3 (name does not exist).
        resp[2] = (byte)((query != null && query.Length > 2 ? query[2] : 0) | 0x80);
        resp[3] = 0x83;

        if (!haveQuestion) { resp[4] = 0; resp[5] = 0; } // QDCOUNT (else keep echoed = 1)
        resp[6] = 0; resp[7] = 0;   // ANCOUNT
        resp[8] = 0; resp[9] = 0;   // NSCOUNT
        resp[10] = 0; resp[11] = 0; // ARCOUNT (drop any EDNS OPT)
        return resp;
    }

    // Index just past the first question (name + qtype + qclass), or -1 if malformed.
    private static int QuestionEnd(byte[] msg)
    {
        if (msg == null || msg.Length < 12) return -1;
        int pos = 12;
        if (!SkipName(msg, ref pos)) return -1;
        pos += 4; // qtype + qclass
        return pos <= msg.Length ? pos : -1;
    }

    private static bool SkipName(byte[] msg, ref int pos)
    {
        int p = pos;
        while (true)
        {
            if (p < 0 || p >= msg.Length) return false;
            int len = msg[p];
            if ((len & 0xC0) == 0xC0) { p += 2; break; } // pointer ends the name
            if (len == 0) { p++; break; }
            p += 1 + len;
        }
        pos = p;
        return true;
    }

    /// <summary>Overwrite the 2-byte id at the front of a message in place.</summary>
    public static void WriteId(byte[] msg, ushort id)
    {
        if (msg == null || msg.Length < 2) return;
        msg[0] = (byte)(id >> 8);
        msg[1] = (byte)(id & 0xFF);
    }

    /// <summary>Return a copy of msg carrying a different id (original untouched).</summary>
    public static byte[] WithId(byte[] msg, ushort id)
    {
        var copy = (byte[])msg.Clone();
        WriteId(copy, id);
        return copy;
    }

    /// <summary>Smallest TTL across the answer records, clamped to [min,max];
    /// returns <paramref name="fallback"/> when there are no answers.</summary>
    public static int GetMinTtl(byte[] resp, int min = 10, int max = 3600, int fallback = 60)
    {
        try
        {
            if (resp == null || resp.Length < 12) return fallback;
            int qd = (resp[4] << 8) | resp[5];
            int an = (resp[6] << 8) | resp[7];
            if (an < 1) return fallback;

            int pos = 12;
            for (int i = 0; i < qd; i++)       // skip question(s)
            {
                if (!SkipName(resp, ref pos)) return fallback;
                pos += 4;
            }

            int best = int.MaxValue;
            for (int i = 0; i < an; i++)
            {
                if (!SkipName(resp, ref pos)) break;
                if (pos + 10 > resp.Length) break;
                long ttl = ((long)resp[pos + 4] << 24) | ((long)resp[pos + 5] << 16)
                         | ((long)resp[pos + 6] << 8) | resp[pos + 7];
                int rdlen = (resp[pos + 8] << 8) | resp[pos + 9];
                if (ttl >= 0 && ttl < best) best = (int)ttl;
                pos += 10 + rdlen;
            }

            if (best == int.MaxValue) return fallback;
            if (best < min) best = min;
            if (best > max) best = max;
            return best;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Extracts every IPv4 A-record address from a DNS response, walking the
    /// question section and answer records with full bounds checking and
    /// pointer-compression support. Never throws; malformed input yields an
    /// empty list. Feeds the resolver's resolved-IP memory (P2P detection).
    /// </summary>
    public static List<uint> ExtractARecords(byte[] msg)
    {
        var result = new List<uint>();
        try
        {
            if (msg == null || msg.Length < 12) return result;
            int qd = (msg[4] << 8) | msg[5];
            int an = (msg[6] << 8) | msg[7];
            if (an == 0) return result;
            int pos = 12;

            static bool SkipName(byte[] m, ref int p)
            {
                int hops = 0;
                while (true)
                {
                    if (p >= m.Length || ++hops > 64) return false;
                    byte len = m[p];
                    if (len == 0) { p += 1; return true; }
                    if ((len & 0xC0) == 0xC0) { p += 2; return true; } // compression pointer
                    p += 1 + len;
                }
            }

            for (int i = 0; i < qd; i++)
            {
                if (!SkipName(msg, ref pos)) return result;
                pos += 4; // qtype + qclass
                if (pos > msg.Length) return result;
            }
            for (int i = 0; i < an && result.Count < 64; i++)
            {
                if (!SkipName(msg, ref pos)) return result;
                if (pos + 10 > msg.Length) return result;
                int type = (msg[pos] << 8) | msg[pos + 1];
                int cls = (msg[pos + 2] << 8) | msg[pos + 3];
                int rdlen = (msg[pos + 8] << 8) | msg[pos + 9];
                pos += 10;
                if (pos + rdlen > msg.Length) return result;
                if (type == 1 && cls == 1 && rdlen == 4)
                    result.Add((uint)((msg[pos] << 24) | (msg[pos + 1] << 16) | (msg[pos + 2] << 8) | msg[pos + 3]));
                pos += rdlen;
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Reads a DNS name at <paramref name="pos"/>, following compression
    /// pointers. <paramref name="nextPos"/> is where the name ends in the
    /// original stream (immediately after the first pointer, per the wire
    /// format). Guards against pointer loops, out-of-range offsets and
    /// over-long names; returns false rather than throwing on any of them.
    /// </summary>
    public static bool TryReadName(byte[] m, int pos, out string name, out int nextPos)
    {
        name = "";
        nextPos = pos;
        if (m == null || pos < 0 || pos >= m.Length) return false;

        var sb = new System.Text.StringBuilder();
        int p = pos, jumps = 0;
        bool jumped = false;

        while (true)
        {
            if (p < 0 || p >= m.Length) return false;
            byte len = m[p];

            if (len == 0)                       // root label: the name ends
            {
                if (!jumped) nextPos = p + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)           // compression pointer
            {
                if (p + 1 >= m.Length) return false;
                int target = ((len & 0x3F) << 8) | m[p + 1];
                if (!jumped) { nextPos = p + 2; jumped = true; }
                if (++jumps > 32) return false; // loop guard
                if (target >= p && jumps > 1) { /* forward/self pointers still bounded by jumps */ }
                p = target;
                continue;
            }
            if ((len & 0xC0) != 0) return false; // reserved label type
            if (p + 1 + len > m.Length) return false;

            if (sb.Length > 0) sb.Append('.');
            for (int i = 0; i < len; i++) sb.Append((char)m[p + 1 + i]);
            if (sb.Length > 255) return false;  // max DNS name length
            p += 1 + len;
        }

        name = sb.ToString();
        return true;
    }

    /// <summary>
    /// Every CNAME target in a response's answer section, in order. This is the
    /// chain a "cloaked" tracker hides behind: the queried first-party name is
    /// clean, but it aliases to a third-party tracker further down the chain.
    /// Never throws; malformed input yields an empty list.
    /// </summary>
    public static List<string> ExtractCnames(byte[] msg)
    {
        var result = new List<string>();
        try
        {
            if (msg == null || msg.Length < 12) return result;
            int qd = (msg[4] << 8) | msg[5];
            int an = (msg[6] << 8) | msg[7];
            if (an == 0) return result;

            int pos = 12;
            for (int i = 0; i < qd; i++)
            {
                if (!TryReadName(msg, pos, out _, out pos)) return result;
                pos += 4;                                   // qtype + qclass
                if (pos > msg.Length) return result;
            }
            for (int i = 0; i < an && result.Count < 32; i++)
            {
                if (!TryReadName(msg, pos, out _, out pos)) return result;
                if (pos + 10 > msg.Length) return result;
                int type = (msg[pos] << 8) | msg[pos + 1];
                int rdlen = (msg[pos + 8] << 8) | msg[pos + 9];
                pos += 10;
                if (rdlen < 0 || pos + rdlen > msg.Length) return result;

                if (type == 5 &&                            // CNAME
                    TryReadName(msg, pos, out string target, out _) && target.Length > 0)
                    result.Add(target);

                pos += rdlen;
            }
        }
        catch { }
        return result;
    }
}
