using System.IO;
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
    public const string CurrentVersion = "0.99.124";

    private const string ReleasesApi =
        "https://api.github.com/repos/ox1d3x3/gunwall/releases/latest";
    private const string ReleasesPage =
        "https://github.com/ox1d3x3/gunwall/releases";

    /// <summary>For the version check: a small JSON request that should be quick
    /// or not at all.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>For the installer download, which is around 190 MB.
    ///
    /// A SEPARATE client, because HttpClient.Timeout covers the whole operation
    /// rather than just the response headers. Sharing the 12-second client above
    /// would have aborted every download on every connection - 15 seconds at
    /// 100 Mbit/s, five minutes on a slow line - and reported it as a network
    /// failure rather than as the deadline it was.</summary>
    private static readonly HttpClient Downloader = new() { Timeout = TimeSpan.FromMinutes(30) };

    public sealed record Result(bool Ok, bool UpdateAvailable, string Latest, string Url, string Message)
    {
        /// <summary>Direct download URL of the release installer, or "" if the
        /// release carries none.</summary>
        public string AssetUrl { get; init; } = "";
        /// <summary>Installer size in bytes, for the progress display.</summary>
        public long AssetSize { get; init; }
        /// <summary>Expected SHA-256, lowercase hex, or "" if the release did not
        /// publish one. Empty is reported to the user rather than treated as
        /// "verified" - an unverifiable download is a fact they should decide on,
        /// not one this code should quietly absorb.</summary>
        public string AssetSha256 { get; init; } = "";
    }

    /// <summary>Hosts a release binary may be downloaded from.
    ///
    /// Pinned, and this is the important part of the whole feature. The download
    /// URL arrives inside a JSON response; GunWall then runs the result ELEVATED.
    /// If that response were ever tampered with - a compromised account, a proxy,
    /// a poisoned DNS answer - following the URL it names would hand administrator
    /// rights to whatever it pointed at.
    ///
    /// TLS makes that unlikely rather than impossible, and "unlikely" is not the
    /// standard for something that installs a firewall. The host is therefore
    /// checked against this list rather than trusted because it arrived over
    /// HTTPS.</summary>
    private static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",   // where GitHub redirects asset downloads
        "release-assets.githubusercontent.com",
    };

    /// <summary>Downloads the installer, verifies it, and returns its path.
    ///
    /// Three things this deliberately does NOT do. It does not follow a URL to any
    /// host but this project's own. It does not accept a file whose SHA-256
    /// disagrees with the release. And it does not launch anything - the caller
    /// decides that, after seeing the verification result, because "downloaded"
    /// and "safe to run elevated" are different claims.
    ///
    /// A hash that could not be established is reported as such rather than
    /// treated as a pass. That distinction is the whole point: on an unsigned
    /// binary the checksum is the only integrity story there is.</summary>
    public static async Task<DownloadResult> DownloadInstallerAsync(
        Result release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!IsAllowedDownloadUrl(release.AssetUrl))
            return new DownloadResult(false, "", false,
                "The release did not provide a download from this project's own repository.");

        // The version comes from a GitHub tag, which is a string from the same
        // response whose host is deliberately not trusted above. Putting it
        // straight into a path let a crafted tag such as "../../Users/Public/x"
        // write the downloaded installer outside the temp directory - and this
        // code then offers to run what it wrote, elevated.
        //
        // Pinning the host while trusting the tag was an inconsistent threat
        // model. Reduced to characters that can appear in a version and nothing
        // else; anything unexpected becomes a fixed name rather than an error,
        // since the file name carries no meaning beyond being recognisable.
        string safeVersion = new string(release.Latest
            .Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray());
        if (safeVersion.Length == 0 || safeVersion.Length > 32) safeVersion = "update";

        // A fresh directory per download, named unpredictably.
        //
        // The file is verified and then launched, and between those two moments it
        // sits on disk. At a predictable path, another process running as this
        // user could pre-create it, hold it, or replace it after the hash was
        // taken. A name nobody can guess removes that without needing to reason
        // about how likely it is.
        //
        // It also means a stale file from an interrupted download can never be
        // mistaken for a fresh one.
        string dir = Path.Combine(Path.GetTempPath(), "GunWall-update-" + Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(dir, $"GunWall-{safeVersion}-setup.exe");

        // Belt to that brace: confirm the resolved path really is inside temp.
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!Path.GetFullPath(dest).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            return new DownloadResult(false, "", false,
                "Refusing to write the download outside the temporary folder.");
        Directory.CreateDirectory(dir);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
            // The version check sets one because the GitHub API rejects requests
            // without it. The asset host is more tolerant today, and relying on
            // that is the kind of assumption this project does not make.
            req.Headers.Add("User-Agent", "GunWall-Updater");

            using (var resp = await Downloader.SendAsync(req,
                       HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();

                // Redirects are followed by HttpClient, so the host is checked
                // again on whatever actually answered.
                string finalHost = resp.RequestMessage?.RequestUri?.Host ?? "";
                if (finalHost.Length > 0 &&
                    !AllowedDownloadHosts.Contains(finalHost, StringComparer.OrdinalIgnoreCase))
                    return new DownloadResult(false, "", false,
                        $"The download redirected to {finalHost}, which is not a "
                        + "GitHub release host. Nothing was saved.");

                long total = resp.Content.Headers.ContentLength ?? release.AssetSize;
                using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                using var file = File.Create(dest);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            if (release.AssetSha256.Length != 64)
                return new DownloadResult(true, dest, false,
                    "Downloaded, but the release did not publish a SHA-256 for this "
                    + "file, so it could not be verified.");

            string actual;
            using (var f = File.OpenRead(dest))
                actual = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(f, ct))
                    .ToLowerInvariant();

            if (!string.Equals(actual, release.AssetSha256, StringComparison.Ordinal))
            {
                try { File.Delete(dest); } catch { }
                return new DownloadResult(false, "", false,
                    "The downloaded file does not match the checksum published with "
                    + "the release. It has been deleted rather than run.");
            }

            return new DownloadResult(true, dest, true, "Downloaded and verified.");
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(dest); } catch { }
            return new DownloadResult(false, "", false, "Download cancelled.");
        }
        catch (Exception ex)
        {
            try { File.Delete(dest); } catch { }
            return new DownloadResult(false, "", false, $"Download failed: {ex.Message}");
        }
    }

    /// <summary><c>Verified</c> is false both when a checksum was absent and when
    /// one failed - but a failure returns Ok false and no path, so the two are
    /// never confused at the call site.</summary>
    public sealed record DownloadResult(bool Ok, string Path, bool Verified, string Message);

    private static bool IsAllowedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedDownloadHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The download URL of the release's installer, or "" if it has none.
    ///
    /// Matched on the name ending in "-setup.exe", which is what the Inno Setup
    /// script in tools/installer produces. Deliberately narrow: a release also
    /// carries the portable executable and the source archives, and offering
    /// someone the wrong one is worse than offering them the releases page.</summary>
    private static (string Url, long Size, string Sha256) FindSetupAsset(
        System.Text.Json.JsonElement release, string releaseBody)
    {
        try
        {
            if (!release.TryGetProperty("assets", out var assets)) return ("", 0, "");
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!a.TryGetProperty("browser_download_url", out var d)) continue;

                string url = d.GetString() ?? "";
                long size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

                // GitHub reports a digest on newer releases as "sha256:<hex>".
                string sha = "";
                if (a.TryGetProperty("digest", out var dg))
                {
                    string v = dg.GetString() ?? "";
                    if (v.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = v[7..].Trim().ToLowerInvariant();
                }
                // Otherwise look for it in the release notes, where this project
                // publishes checksums by hand. Matched against THIS file's name so
                // the portable build's hash cannot be picked up by mistake.
                if (sha.Length == 0) sha = FindShaInBody(releaseBody, name);

                return (url, size, sha);
            }
        }
        catch { }
        return ("", 0, "");
    }

    /// <summary>Finds a 64-character hex string on or beside a line naming the
    /// file. Returns "" when nothing matches - never a guess.</summary>
    private static string FindShaInBody(string body, string fileName)
    {
        if (string.IsNullOrEmpty(body) || fileName.Length == 0) return "";
        foreach (string line in body.Split('\n'))
        {
            if (line.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, "\\b[A-Fa-f0-9]{64}\\b");
            if (m.Success) return m.Value.ToLowerInvariant();
        }
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
            string body = doc.RootElement.TryGetProperty("body", out var bd)
                ? (bd.GetString() ?? "") : "";
            var (assetUrl, assetSize, assetSha) = FindSetupAsset(doc.RootElement, body);

            // The page stays the link a person clicks; the asset is what GunWall
            // downloads. Kept separate so a release with no installer still offers
            // somewhere sensible to go.
            string msg = newer
                ? $"Version {latest} is available (you have {CurrentVersion})."
                : $"You're up to date (v{CurrentVersion}).";
            return new Result(true, newer, latest, url, msg)
            {
                AssetUrl = IsAllowedDownloadUrl(assetUrl) ? assetUrl : "",
                AssetSize = assetSize,
                AssetSha256 = assetSha,
            };
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
