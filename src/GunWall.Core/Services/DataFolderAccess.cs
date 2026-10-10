using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GunWall.Services;

/// <summary>
/// Who may use GunWall's data folder (0.99.200).
///
/// The folder belongs to Windows and administrators; other users may read it.
/// 0.99.199's installer meant to set exactly that with icacls, but applied the
/// folder-style grant to every FILE as well: on a file those flags are refused,
/// and the files were left with their inherited permissions removed and nothing
/// in their place - "Access to the path ...rules.json is denied" even for an
/// administrator (reported 2026-10-10). The rules file was untouched but
/// unreadable, so GunWall started with no rules.
///
/// <see cref="EnsureUsable"/> runs at every start, before anything reads the
/// folder: if the profile cannot be opened it rewrites the folder's permissions
/// and lets every file and subfolder inherit them again. Administrators own the
/// files, and an owner may always rewrite permissions, so this repairs itself.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataFolderAccess
{
    /// <summary>True if the folder and its profile can be used (after a repair if
    /// one was needed).</summary>
    public static bool EnsureUsable(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return true;
        if (Usable(dir)) return true;
        log($"Data folder: {dir} cannot be read or written - repairing its permissions.");
        try
        {
            Repair(dir);
        }
        catch (Exception ex)
        {
            log($"Data folder: permission repair failed ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
        bool ok = Usable(dir);
        log(ok ? "Data folder: permissions repaired - SYSTEM and Administrators full control, Users read; every file inherits."
               : "Data folder: still not usable after the repair.");
        return ok;
    }

    /// <summary>Can a file be created there, and can the profile be opened for
    /// reading and writing?</summary>
    private static bool Usable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".access-check");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            string rules = Path.Combine(dir, "rules.json");
            if (File.Exists(rules))
                using (new FileStream(rules, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            string log = Path.Combine(dir, "diagnostics.log");
            if (File.Exists(log))
                using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; }   // in use is not a permission problem
    }

    /// <summary>The folder: protected, SYSTEM and Administrators full, Users read,
    /// all inherited by contents. Everything inside: explicit entries removed and
    /// inheritance switched back on, so it takes exactly the folder's rules.</summary>
    public static void Repair(string dir)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(ds);

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
        {
            var s = new DirectorySecurity();
            s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            try { new DirectoryInfo(sub).SetAccessControl(s); } catch { }
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var f = new FileSecurity();
            f.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            try { new FileInfo(file).SetAccessControl(f); } catch { }
        }
    }
}
