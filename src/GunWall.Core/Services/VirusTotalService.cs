using System.Net.Http;
using System.Text.Json;

namespace GunWall.Services;

/// <summary>
/// Looks up a file's reputation on VirusTotal by its SHA-256 hash, using the
/// user's own API key. Only a hash is sent (never the file), so this is a
/// privacy-friendly reputation check. All calls are best-effort and never throw
/// into the UI — failures return a descriptive result instead.
/// </summary>
public sealed class VirusTotalService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record Result(bool Ok, int Malicious, int Total, string Message);

    /// <summary>
    /// Queries VirusTotal for the given SHA-256. Returns detection counts, or a
    /// message explaining why it couldn't (no key, not found, rate limited).
    /// </summary>
    public static async Task<Result> LookupAsync(string sha256, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new Result(false, 0, 0, "No VirusTotal API key set (add one in Settings).");
        if (string.IsNullOrWhiteSpace(sha256))
            return new Result(false, 0, 0, "No file hash available to look up.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.virustotal.com/api/v3/files/{sha256}");
            req.Headers.Add("x-apikey", apiKey);

            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(false, 0, 0, "Not found on VirusTotal (file unknown to the service).");
            if (resp.StatusCode == (System.Net.HttpStatusCode)429)
                return new Result(false, 0, 0, "VirusTotal rate limit reached. Try again shortly.");
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new Result(false, 0, 0, "VirusTotal rejected the API key. Check it in Settings.");
            if (!resp.IsSuccessStatusCode)
                return new Result(false, 0, 0, $"VirusTotal returned {(int)resp.StatusCode}.");

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var stats = doc.RootElement
                .GetProperty("data").GetProperty("attributes")
                .GetProperty("last_analysis_stats");

            int malicious = stats.TryGetProperty("malicious", out var m) ? m.GetInt32() : 0;
            int suspicious = stats.TryGetProperty("suspicious", out var s) ? s.GetInt32() : 0;
            int harmless = stats.TryGetProperty("harmless", out var h) ? h.GetInt32() : 0;
            int undetected = stats.TryGetProperty("undetected", out var u) ? u.GetInt32() : 0;
            int total = malicious + suspicious + harmless + undetected;

            int flagged = malicious + suspicious;
            string verdict = flagged == 0
                ? "Clean — no engines flagged this file."
                : $"{flagged} of {total} engines flagged this file.";
            return new Result(true, flagged, total, verdict);
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, $"VirusTotal lookup failed: {ex.Message}");
        }
    }
}
