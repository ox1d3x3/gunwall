using System.IO;
using System.Text.Json;

namespace GunWall.Services;

/// <summary>
/// Applications the background service saw while the window was closed, waiting
/// to be asked about when it opens (0.99.199). In Zero Trust they stay blocked
/// meanwhile, which is the point.
///
/// One entry per application: a program that tried fifty times while you were
/// away asks once. Written only by the engine owner, read by the window after it
/// has taken ownership, so the two never touch the file at the same time.
/// </summary>
public static class PendingPrompts
{
    public const string FileName = "pending-prompts.json";
    public const int MaxEntries = 200;

    public static int Add(string path, IEnumerable<PromptRequest> prompts)
    {
        var list = Read(path);
        var seen = new HashSet<string>(list.Select(p => p.ExePath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var p in prompts)
        {
            if (list.Count >= MaxEntries) break;
            if (string.IsNullOrEmpty(p.ExePath) || !seen.Add(p.ExePath)) continue;
            list.Add(p);
            added++;
        }
        if (added > 0) Write(path, list);
        return added;
    }

    /// <summary>Everything waiting, oldest first; the file is removed. On Windows a
    /// file not owned by SYSTEM or Administrators is discarded unread: anyone who
    /// could plant one could put their own program in front of an elevated
    /// "Allow" button (reviewed 0.99.199).</summary>
    public static List<PromptRequest> TakeAll(string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path) && !OwnedByAdmins(path))
        {
            DiagnosticLog.Log($"Pending prompts: {path} is not owned by SYSTEM or Administrators - discarded unread.");
            try { File.Delete(path); } catch { }
            return new();
        }
        var list = Read(path);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return list;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool OwnedByAdmins(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl()
                .GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            return owner != null && (owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid)
                                  || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch { return false; }
    }

    private static List<PromptRequest> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<PromptRequest>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Log($"Pending prompts: {path} unreadable ({ex.GetType().Name}) - ignored.");
            return new();
        }
    }

    private static void Write(string path, List<PromptRequest> list)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list));
        File.Move(tmp, path, overwrite: true);
    }
}
