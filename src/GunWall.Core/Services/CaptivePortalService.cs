using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace GunWall.Services;

/// <summary>
/// §9 captive-portal helper. Detects hotel/airport-style login pages the same way
/// Windows NCSI does: fetch a known plain-text URL with redirects disabled. A
/// clean answer means open internet; a redirect or altered body means a portal
/// is intercepting traffic; a network error means no connectivity at all.
/// </summary>
public static class CaptivePortalService
{
    private const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string Expected = "Microsoft Connect Test";

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>One probe. Captive=true means a portal is intercepting.</summary>
    public static async Task<(bool Ok, bool Captive, string Detail)> CheckAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(ProbeUrl);
            int code = (int)resp.StatusCode;

            if (code is >= 300 and < 400)
                return (true, true,
                    "Redirected - a captive portal is intercepting traffic. Open a browser to its login page.");

            string body = (await resp.Content.ReadAsStringAsync()).Trim();
            if (code == 200 && body == Expected)
                return (true, false, "Internet is reachable - no captive portal detected.");

            return (true, true,
                $"Unexpected reply (HTTP {code}) - a captive portal is likely rewriting responses.");
        }
        catch (TaskCanceledException)
        {
            return (false, false, "Probe timed out - no connectivity, or traffic is being blocked.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Probe failed: {ex.Message}");
        }
    }
}
