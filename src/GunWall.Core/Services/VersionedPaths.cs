using System.IO;
using System.Text.RegularExpressions;

namespace GunWall.Services;

/// <summary>
/// Programs that update into a folder named after their version (0.99.227):
///     ...\EdgeWebView\Application\141.0.3537.71\msedgewebview2.exe
///     ...\OneDrive\25.170.0831.0002\FileCoAuth.exe
///     ...\GitHubDesktop\app-3.5.2\GitHubDesktop.exe
/// Every update is a new path, so the rule for the old one stopped matching: the new
/// version was asked about again, and the old rule was later pruned as "program
/// gone" (msedgewebview2 on the domain laptop, 14/10). Store apps had this solved
/// (StorePackagePaths); this is the same for ordinary folders.
///
/// The version folder is the LAST directory in the path that is a version number,
/// optionally after a short letters-and-dash prefix ("app-"). Two paths are versions
/// of one program when everything before that folder and everything after it are
/// the same, and the prefix is the same.
/// </summary>
public static class VersionedPaths
{
    private static readonly Regex VersionDir = new(@"^(?<pre>[A-Za-z]{1,12}-)?(?<ver>\d+(\.\d+){1,3})$", RegexOptions.CultureInvariant);

    /// <summary>The parts of a path around its version folder, or null.</summary>
    public sealed record Parts(string Parent, string Prefix, Version Version, string Rest);

    public static Parts? Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Windows paths; '/' only for a path written that way throughout (the bench).
        char sep = path.Contains('\\') ? '\\' : '/';
        var segs = path.Split(sep);
        // The file is the last segment; look at directories from the nearest outwards.
        for (int i = segs.Length - 2; i >= 1; i--)
        {
            var m = VersionDir.Match(segs[i]);
            if (!m.Success || !Version.TryParse(m.Groups["ver"].Value, out var v)) continue;
            string parent = string.Join(sep, segs, 0, i);
            string rest = string.Join(sep, segs, i + 1, segs.Length - i - 1);
            if (parent.Length == 0 || rest.Length == 0) return null;
            return new Parts(parent, m.Groups["pre"].Value, v, rest);
        }
        return null;
    }

    /// <summary>True when <paramref name="newer"/> is a later version of the same
    /// program as <paramref name="older"/>.</summary>
    public static bool IsNewerVersionOf(string newer, string older)
    {
        var n = Split(newer); var o = Split(older);
        return n != null && o != null
            && string.Equals(n.Parent, o.Parent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(n.Prefix, o.Prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(n.Rest, o.Rest, StringComparison.OrdinalIgnoreCase)
            && n.Version > o.Version;
    }

    /// <summary>The same file in the newest later version folder beside the old one,
    /// or null. Lists only the old folder's parent.</summary>
    public static string? FindUpdated(string oldPath)
    {
        var o = Split(oldPath);
        if (o == null) return null;
        try
        {
            string? best = null; Version? bestV = null;
            foreach (var dir in Directory.EnumerateDirectories(o.Parent))
            {
                var m = VersionDir.Match(Path.GetFileName(dir));
                if (!m.Success || !string.Equals(m.Groups["pre"].Value, o.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(m.Groups["ver"].Value, out var v) || v <= o.Version) continue;
                if (bestV != null && v <= bestV) continue;
                string candidate = Path.Combine(dir, o.Rest);
                if (!File.Exists(candidate)) continue;
                best = candidate; bestV = v;
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>The publisher a rule can be tied to: a valid, trusted signature with a
    /// readable signer name. "" otherwise - an unsigned file, a broken signature, or
    /// one whose name could not be read ("Signed") identifies nobody.</summary>
    public static string UsableSigner(SignatureInfo s) =>
        s.Status == SignatureStatus.Valid && !string.IsNullOrWhiteSpace(s.Signer) && s.Signer != "Signed"
            ? s.Signer : "";

    /// <summary>
    /// May the new version inherit an ALLOW rule? Only when it carries a valid signature
    /// from the same publisher the rule was made for: the one recorded on the rule, or,
    /// for a rule made before signers were recorded, the old file's own. A version
    /// folder is just a folder name - without this, anything dropped into a newer
    /// "141.0.0.1" folder beside an allowed program would be allowed with it. Block
    /// rules need no such proof: following a block can only tighten.
    /// </summary>
    public static bool MayInheritAllow(string? ruleSigner, SignatureInfo newFile, SignatureInfo oldFile)
    {
        string now = UsableSigner(newFile);
        if (now.Length == 0) return false;
        string expected = !string.IsNullOrWhiteSpace(ruleSigner) ? ruleSigner! : UsableSigner(oldFile);
        return expected.Length > 0 && string.Equals(now, expected, StringComparison.OrdinalIgnoreCase);
    }
}
