using System.Net.Http;
using System.Text.Json;

namespace GunWall.Services;

/// <summary>
/// Checks the project's GitHub releases for a newer version. Only reads the
/// latest release tag — no downloading or auto-installing — and returns a
/// result the UI can act on. Best-effort: network or parse failures return a
/// descriptive, non-throwing result.
/// </summary>
public static class UpdateService
{
    // Current shipped version. Bump alongside the csproj <Version>.
    public const string CurrentVersion = "0.99.108";

    private const string ReleasesApi =
        "https://api.github.com/repos/ox1d3x3/gunwall/releases/latest";
    private const string ReleasesPage =
        "https://github.com/ox1d3x3/gunwall/releases";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public sealed record Result(bool Ok, bool UpdateAvailable, string Latest, string Url, string Message);

    /// <summary>The download URL of the release's installer, or "" if it has none.
    ///
    /// Matched on the name ending in "-setup.exe", which is what the Inno Setup
    /// script in tools/installer produces. Deliberately narrow: a release also
    /// carries the portable executable and the source archives, and offering
    /// someone the wrong one is worse than offering them the releases page.</summary>
    private static string FindSetupAsset(System.Text.Json.JsonElement release)
    {
        try
        {
            if (!release.TryGetProperty("assets", out var assets)) return "";
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.TryGetProperty("browser_download_url", out var d))
                    return d.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            // GitHub requires a User-Agent.
            req.Headers.Add("User-Agent", "GunWall-UpdateChecker");
            req.Headers.Add("Accept", "application/vnd.github+json");

            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(true, false, CurrentVersion, ReleasesPage,
                    "No published releases yet — you're on the latest build.");
            if (!resp.IsSuccessStatusCode)
                return new Result(false, false, "", ReleasesPage,
                    $"Couldn't reach GitHub ({(int)resp.StatusCode}). Try again later.");

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? (u.GetString() ?? ReleasesPage) : ReleasesPage;

            string latest = NormalizeVersion(tag);
            bool newer = CompareVersions(latest, CurrentVersion) > 0;

            // Point at the installer asset when the release carries one, rather
            // than at the releases page. The page is a correct answer and a poor
            // one: it asks someone to work out which of several files they want,
            // and the wrong choice here is a firewall that does not upgrade
            // cleanly. Falls back to the page when no installer is attached, so a
            // source-only release still behaves.
            string setup = FindSetupAsset(doc.RootElement);
            if (setup.Length > 0) url = setup;

            string msg = newer
                ? $"Version {latest} is available (you have {CurrentVersion})."
                : $"You're up to date (v{CurrentVersion}).";
            return new Result(true, newer, latest, url, msg);
        }
        catch (Exception ex)
        {
            return new Result(false, false, "", ReleasesPage, $"Update check failed: {ex.Message}");
        }
    }

    private static string NormalizeVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "0.0.0";
        tag = tag.Trim();
        if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
        return tag;
    }

    /// <summary>Returns &gt;0 if a is newer, &lt;0 if older, 0 if equal.</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int na = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
            int nb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }
}
