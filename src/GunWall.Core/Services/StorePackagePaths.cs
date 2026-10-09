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

    private static IEnumerable<string> InstalledFullNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications"),
            (Microsoft.Win32.Registry.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k != null) foreach (var n in k.GetSubKeyNames()) names.Add(n);
            }
            catch { }
        }
        return names;
    }
}
