using System;
using System.Collections.Generic;
using System.IO;

namespace GunWall.Services;

/// <summary>
/// Store apps live in versioned folders:
///     ...\WindowsApps\Name_Version_Arch_ResourceId_PublisherId\inner\app.exe
/// An update installs a new folder and removes the old one, so a rule on the old path
/// stopped matching and was pruned as dead - the user's decision lost at every Store
/// update (a Calculator rule in the 0.99.161 bundle). This finds the same file in the
/// newest installed version of the same package.
///
/// WindowsApps cannot be listed even elevated; a full path inside it can be opened. So
/// installed versions come from the registry's package lists, and the candidate file is
/// confirmed by opening its full path.
/// </summary>
public static class StorePackagePaths
{
    private const string Marker = @"\WindowsApps\";

    /// <summary>The same file in a newer installed version of its package, or null.</summary>
    public static string? FindUpdated(string oldPath)
    {
        try
        {
            int i = oldPath.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string root = oldPath[..(i + Marker.Length)];
            string rest = oldPath[(i + Marker.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash <= 0) return null;
            string? next = PickNewer(rest[..slash], InstalledFullNames());
            if (next == null) return null;
            string candidate = Path.Combine(root, next, rest[(slash + 1)..]);
            return File.Exists(candidate) ? candidate : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The newest full name of the same package - name, architecture, resource id and
    /// publisher id equal, version higher - or null. Package names cannot contain '_',
    /// so a full name always splits into five parts (resource id usually empty).
    /// </summary>
    public static string? PickNewer(string oldFullName, IEnumerable<string> installed)
    {
        var o = oldFullName.Split('_');
        if (o.Length != 5 || !Version.TryParse(o[1], out var oldV)) return null;
        string? best = null; Version? bestV = null;
        foreach (var name in installed)
        {
            var p = name.Split('_');
            if (p.Length != 5 || !Version.TryParse(p[1], out var v)) continue;
            if (!Same(p[0], o[0]) || !Same(p[2], o[2]) || !Same(p[3], o[3]) || !Same(p[4], o[4])) continue;
            if (v <= oldV || (bestV != null && v <= bestV)) continue;
            best = name; bestV = v;
        }
        return best;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A path inside WindowsApps, split into the folder above the package,
    /// the package full name's five parts and the path inside the package; or null.</summary>
    public sealed record Parts(string Root, string[] Name, Version Version, string Rest);

    public static Parts? Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        int i = path.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = path[(i + Marker.Length)..];
        int slash = rest.IndexOf('\\');
        if (slash <= 0 || slash == rest.Length - 1) return null;
        var n = rest[..slash].Split('_');
        if (n.Length != 5 || !Version.TryParse(n[1], out var v)) return null;
        return new Parts(path[..(i + Marker.Length)], n, v, rest[(slash + 1)..]);
    }

    /// <summary>True when <paramref name="newer"/> is the same file in a later version
    /// of the same package (name, architecture, resource id and publisher id) as
    /// <paramref name="older"/> (0.99.234.3). Only Windows' package installer can write
    /// into WindowsApps, and it checks the package signature, so a matching package
    /// identity is the publisher check - no file signature is needed.</summary>
    public static bool IsNewerVersionOf(string newer, string older)
    {
        var a = Split(newer); var b = Split(older);
        return a != null && b != null
            && Same(a.Root, b.Root) && Same(a.Rest, b.Rest)
            && Same(a.Name[0], b.Name[0]) && Same(a.Name[2], b.Name[2])
            && Same(a.Name[3], b.Name[3]) && Same(a.Name[4], b.Name[4])
            && a.Version > b.Version;
    }

    /// <summary>
    /// Every installed package full name the registry lists. 0.99.224 moved this into
    /// the background service, which runs as SYSTEM: there HKEY_CLASSES_ROOT's
    /// per-user half is SYSTEM's own, so packages installed for the signed-in user
    /// (WhatsApp, and the user's updated copies of Store and Game Bar) were never
    /// found - their rules were pruned at boot and they asked again (0.99.234.3). So
    /// the machine-wide package repository, every user's AppxAllUserStore list and
    /// every loaded user hive are read as well.
    /// </summary>
    private static IEnumerable<string> InstalledFullNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Microsoft.Win32.RegistryKey hive, string path)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k != null) foreach (var n in k.GetSubKeyNames()) names.Add(n);
            }
            catch { }
        }
        const string allUsers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";
        const string userRepo = @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        var hklm = Microsoft.Win32.Registry.LocalMachine;
        Add(hklm, allUsers + @"\Applications");
        Add(hklm, @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages");
        Add(Microsoft.Win32.Registry.ClassesRoot, userRepo);
        try
        {
            using var store = hklm.OpenSubKey(allUsers);
            if (store != null)
                foreach (var sid in store.GetSubKeyNames())
                    if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) Add(hklm, allUsers + "\\" + sid);
        }
        catch { }
        try
        {
            // A user's classes are their own hive, HKEY_USERS\<sid>_Classes.
            foreach (var sid in Microsoft.Win32.Registry.Users.GetSubKeyNames())
                if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    Add(Microsoft.Win32.Registry.Users, sid + "\\" + userRepo);
        }
        catch { }
        return names;
    }
}
