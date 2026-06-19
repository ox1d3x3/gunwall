using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace GunWall.Services;

/// <summary>Identity for a Microsoft Store / UWP app.</summary>
public sealed record StoreAppInfo(bool IsStore, string DisplayName, string PackageFamily);

/// <summary>
/// Detects Microsoft Store / UWP (AppContainer) apps from their executable path
/// and resolves a friendly name + package identity. Store apps live under
/// %ProgramFiles%\WindowsApps or %WinDir%\SystemApps; their folder name is the
/// package full name (Name_Version_Arch_ResourceId_PublisherId). We read the real
/// DisplayName from the package's AppxManifest.xml when it's a literal, and fall
/// back to a readable form of the package name otherwise. Cached per path.
/// </summary>
public static class StoreAppService
{
    private static readonly ConcurrentDictionary<string, StoreAppInfo> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Markers = { @"\WindowsApps\", @"\SystemApps\" };

    public static bool IsStoreApp(string exePath) => Resolve(exePath).IsStore;

    public static StoreAppInfo Resolve(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new StoreAppInfo(false, "", "");
        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                int marker = -1, mlen = 0;
                foreach (var m in Markers)
                {
                    int i = path.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0) { marker = i; mlen = m.Length; break; }
                }
                if (marker < 0) return new StoreAppInfo(false, "", "");

                string rest = path.Substring(marker + mlen);
                string fullName = rest.Split('\\')[0];          // PackageFullName
                if (string.IsNullOrEmpty(fullName)) return new StoreAppInfo(false, "", "");

                // Name_Version_Arch_ResourceId_PublisherId
                var parts = fullName.Split('_');
                string namePart = parts[0];                      // e.g. SpotifyAB.SpotifyMusic
                string publisherId = parts.Length > 1 ? parts[^1] : "";
                string family = string.IsNullOrEmpty(publisherId) ? namePart : $"{namePart}_{publisherId}";

                string display = ReadManifestName(path, marker + mlen, fullName)
                                 ?? Readable(namePart);

                return new StoreAppInfo(true, display, family);
            }
            catch
            {
                return new StoreAppInfo(false, "", "");
            }
        });
    }

    /// <summary>Reads a literal DisplayName from the package's AppxManifest.xml.
    /// Returns null when missing or an unresolved ms-resource reference.</summary>
    private static string? ReadManifestName(string fullPath, int packageRootStart, string fullName)
    {
        try
        {
            string packageFolder = fullPath.Substring(0, packageRootStart) + fullName;
            string manifest = Path.Combine(packageFolder, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;

            var doc = XDocument.Load(manifest);
            // Prefer the per-application VisualElements DisplayName, then the package one.
            string? name =
                doc.Descendants().Where(e => e.Name.LocalName == "VisualElements")
                   .Select(e => (string?)e.Attribute("DisplayName")).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return null;
            return name;
        }
        catch { return null; }
    }

    /// <summary>Turns a package name like "SpotifyAB.SpotifyMusic" into "Spotify Music".</summary>
    private static string Readable(string namePart)
    {
        string tail = namePart;
        int dot = namePart.LastIndexOf('.');
        if (dot >= 0 && dot < namePart.Length - 1) tail = namePart.Substring(dot + 1);

        var sb = new StringBuilder();
        for (int i = 0; i < tail.Length; i++)
        {
            char c = tail[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(tail[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : namePart;
    }
}
