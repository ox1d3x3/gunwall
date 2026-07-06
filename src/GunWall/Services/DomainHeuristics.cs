using System;
using System.Collections.Generic;
using System.Linq;

namespace GunWall.Services;

/// <summary>
/// §6 domain heuristics: scores how machine-generated a domain looks (DGA-style
/// malware phone-home names like "xkqwzjtrbvyp.com"). Pure logic, no I/O, so it
/// is fully unit-testable offline.
///
/// Only the registrable label is scored (the label left of the public suffix):
/// random-looking *subdomains* are completely normal (CDNs, cloud storage), so
/// "r3---sn-abc123.googlevideo.com" is judged by "googlevideo", not the junk.
/// </summary>
public static class DomainHeuristics
{
    // Multi-part public suffixes we should step over when finding the registrable
    // label. Not exhaustive — common ones only; unknown TLDs fall back to one label.
    private static readonly HashSet<string> _twoPartTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk","org.uk","ac.uk","gov.uk","com.au","net.au","org.au","co.nz",
        "co.jp","or.jp","ne.jp","com.br","com.cn","com.tr","co.in","co.za",
        "com.mx","com.ar","com.sg","com.hk","co.kr","com.tw"
    };

    /// <summary>The label that would be registered ("googlevideo" in
    /// "r3.googlevideo.com"). Empty when the name has no dot or is an IP.</summary>
    public static string RegistrableLabel(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "";
        string d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(d, out _)) return "";
        var parts = d.Split('.');
        if (parts.Length < 2) return "";
        int tldParts = parts.Length >= 3 &&
                       _twoPartTlds.Contains($"{parts[^2]}.{parts[^1]}") ? 2 : 1;
        int idx = parts.Length - tldParts - 1;
        return idx >= 0 ? parts[idx] : "";
    }

    /// <summary>Score 0-100 (higher = more DGA-like). Suspicious at >= 70.
    /// Reason describes the strongest signals for the notification.</summary>
    public static (bool Suspicious, int Score, string Reason) Score(string domain)
    {
        string label = RegistrableLabel(domain);
        // Short labels can't be judged fairly, and brands are short: skip.
        if (label.Length < 8) return (false, 0, "");

        var reasons = new List<string>();
        int score = 0;

        // 1) Shannon entropy of the label (random strings sit near log2(alphabet)).
        double entropy = Entropy(label);
        if (entropy >= 3.6) { score += 30; reasons.Add("high randomness"); }
        else if (entropy >= 3.2) score += 15;

        // 2) Vowel scarcity — pronounceable words have vowels; DGA output often not.
        int letters = label.Count(char.IsLetter);
        if (letters >= 6)
        {
            double vowelRatio = label.Count(c => "aeiou".Contains(c)) / (double)letters;
            if (vowelRatio < 0.15) { score += 30; reasons.Add("almost no vowels"); }
            else if (vowelRatio < 0.25) score += 15;
        }

        // 3) Longest consonant run (words rarely exceed 4).
        int run = LongestConsonantRun(label);
        if (run >= 6) { score += 25; reasons.Add($"{run} consonants in a row"); }
        else if (run >= 5) score += 12;

        // 4) Digits mixed through the name (hex-ish machine names).
        int digits = label.Count(char.IsDigit);
        double digitRatio = digits / (double)label.Length;
        if (digitRatio >= 0.35) { score += 20; reasons.Add("digit-heavy"); }
        else if (digitRatio >= 0.2) score += 10;

        // 5) Unusual length.
        if (label.Length >= 20) { score += 15; reasons.Add("unusually long"); }
        else if (label.Length >= 14) score += 6;

        if (score > 100) score = 100;
        return (score >= 70, score, string.Join(", ", reasons));
    }

    private static double Entropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        double e = 0, len = s.Length;
        foreach (var n in counts.Values)
        {
            double p = n / len;
            e -= p * Math.Log2(p);
        }
        return e;
    }

    private static int LongestConsonantRun(string s)
    {
        int best = 0, cur = 0;
        foreach (var c in s)
        {
            if (char.IsLetter(c) && !"aeiou".Contains(c)) { cur++; if (cur > best) best = cur; }
            else cur = 0;
        }
        return best;
    }
}
