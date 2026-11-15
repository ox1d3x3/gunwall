using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Recognises one program file in several folders (0.99.235): same file name and same
/// SHA-256. Used only to carry a BLOCK to a new copy - see
/// FirewallManager.AdoptIdenticalBlocked. Pure, so the bench runs it.
/// </summary>
public static class IdenticalFile
{
    // Its own splitter, not Path.GetFileName: the bench runs where '\\' is not a separator.
    private static string NameOf(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    private static bool SameName(string a, string b) =>
        string.Equals(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A blocked rule with a recorded hash names a file called the same, in
    /// another place. Cheap: decides whether the new file is worth hashing.</summary>
    public static bool HasCandidate(IEnumerable<FirewallRule> rules, string exePath) =>
        !rules.Any(r => SamePath(r.ExecutablePath, exePath))
        && rules.Any(r => r.Status == AppStatus.Blocked && r.Hash.Length > 0 && SameName(r.ExecutablePath, exePath));

    /// <summary>The blocked rules for the same file elsewhere, newest first. Empty when
    /// the path already has a rule, or <paramref name="hash"/> is empty.</summary>
    public static List<FirewallRule> BlockedTwins(IEnumerable<FirewallRule> rules, string exePath, string hash)
    {
        var all = rules.ToList();
        if (string.IsNullOrEmpty(hash) || all.Any(r => SamePath(r.ExecutablePath, exePath))) return new();
        return all.Where(r => r.Status == AppStatus.Blocked && r.Hash.Length > 0
                              && SameName(r.ExecutablePath, exePath)
                              && string.Equals(r.Hash, hash, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(r => r.CreatedUtc)
                  .ToList();
    }
}
