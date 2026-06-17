namespace GunWall.Models;

/// <summary>
/// One curated blocklist category. Entries are public, well-known hostnames
/// (and optional literal IPs/CIDRs). When a category is enabled, GunWall
/// resolves the hostnames to their current IPv4 addresses and blocks those
/// outbound. These lists are GunWall's own, compiled from publicly documented
/// endpoints — not copied from any third-party data file.
/// </summary>
public sealed record BlocklistCategory(
    string Key,
    string Name,
    string Description,
    string[] Hosts,
    string[] SourceUrls);

public static class BlocklistCatalog
{
    // Windows diagnostic / telemetry endpoints (publicly documented).
    private static readonly string[] TelemetryHosts =
    {
        "vortex.data.microsoft.com",
        "vortex-win.data.microsoft.com",
        "telecommand.telemetry.microsoft.com",
        "telemetry.microsoft.com",
        "watson.telemetry.microsoft.com",
        "watson.microsoft.com",
        "settings-win.data.microsoft.com",
        "settings-sandbox.data.microsoft.com",
        "v10.events.data.microsoft.com",
        "v20.events.data.microsoft.com",
        "functional.events.data.microsoft.com",
        "browser.events.data.microsoft.com",
        "self.events.data.microsoft.com",
        "oca.telemetry.microsoft.com",
        "sqm.telemetry.microsoft.com",
        "df.telemetry.microsoft.com",
        "reports.wes.df.telemetry.microsoft.com",
        "services.wes.df.telemetry.microsoft.com",
        "wes.df.telemetry.microsoft.com",
        "statsfe2.ws.microsoft.com",
        "statsfe1.ws.microsoft.com",
        "redir.metaservices.microsoft.com",
        "telemetry.appex.bing.net",
        "telemetry.urs.microsoft.com",
        "i1.services.social.microsoft.com",
        "feedback.windows.com",
        "feedback.microsoft-hohm.com",
        "feedback.search.microsoft.com",
    };

    // Windows Update delivery endpoints. Blocking these stops updates, so this
    // category is OFF by default and clearly labelled.
    private static readonly string[] UpdateHosts =
    {
        "windowsupdate.microsoft.com",
        "update.microsoft.com",
        "fe2.update.microsoft.com",
        "sls.update.microsoft.com",
        "download.windowsupdate.com",
        "au.download.windowsupdate.com",
        "dl.delivery.mp.microsoft.com",
        "tlu.dl.delivery.mp.microsoft.com",
        "ntservicepack.microsoft.com",
        "wustat.windows.com",
        "test.stats.update.microsoft.com",
    };

    // Common ad / tracking endpoints.
    private static readonly string[] AdsHosts =
    {
        "ads.msn.com",
        "ads1.msn.com",
        "ads2.msn.com",
        "adnexus.net",
        "adsyndication.msn.com",
        "a.ads1.msn.com",
        "a.ads2.msn.com",
        "live.rads.msn.com",
        "rad.msn.com",
        "g.msn.com",
        "flex.msn.com",
        "c.msn.com",
        "ec.atdmt.com",
        "cdn.atdmt.com",
        "ad.doubleclick.net",
        "static.ads-twitter.com",
        "analytics.google.com",
    };

    public static readonly BlocklistCategory Telemetry = new(
        "telemetry", "Windows telemetry & tracking",
        "Blocks known Windows diagnostic and telemetry domains.",
        TelemetryHosts,
        new[] { "https://raw.githubusercontent.com/crazy-max/WindowsSpyBlocker/master/data/hosts/spy.txt" });

    public static readonly BlocklistCategory Update = new(
        "update", "Windows Update servers",
        "Blocks Windows Update delivery domains. Leave OFF unless you intend to stop updates.",
        UpdateHosts,
        new[] { "https://raw.githubusercontent.com/crazy-max/WindowsSpyBlocker/master/data/hosts/update.txt" });

    public static readonly BlocklistCategory Ads = new(
        "ads", "Ads & trackers",
        "Blocks ads and trackers at the DNS layer via AdGuard.",
        AdsHosts,
        new[]
        {
            "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
            "https://raw.githubusercontent.com/crazy-max/WindowsSpyBlocker/master/data/hosts/extra.txt",
        });

    public static readonly IReadOnlyList<BlocklistCategory> All = new[] { Telemetry, Update, Ads };

    // Online lists are fetched from these open-source, MIT-licensed projects:
    //   WindowsSpyBlocker (crazy-max) and StevenBlack/hosts.
}
