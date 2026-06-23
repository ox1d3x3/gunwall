namespace GunWall.Services;

/// <summary>
/// Static GeoIP reference data with no I/O: maps an ISO-3166 alpha-2 country code
/// to a continent code (AF / AN / AS / EU / NA / OC / SA). Used by the §1 entity
/// rule engine to roll a remote's country up to a continent for "continent:" rules.
/// Pure logic — fully unit-testable off-device.
/// </summary>
public static class GeoData
{
    /// <summary>Continent code for an ISO country code, or "" if unknown.</summary>
    public static string Continent(string country)
    {
        if (string.IsNullOrEmpty(country)) return "";
        return _continent.TryGetValue(country.Trim().ToUpperInvariant(), out var c) ? c : "";
    }

    /// <summary>Human label for a continent code (for UI display).</summary>
    public static string ContinentName(string code) => code?.ToUpperInvariant() switch
    {
        "AF" => "Africa",
        "AN" => "Antarctica",
        "AS" => "Asia",
        "EU" => "Europe",
        "NA" => "North America",
        "OC" => "Oceania",
        "SA" => "South America",
        _ => code ?? ""
    };

    private static readonly Dictionary<string, string> _continent = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(260, System.StringComparer.OrdinalIgnoreCase);
        void Add(string continent, params string[] codes)
        {
            foreach (var c in codes) d[c] = continent;
        }

        Add("AF",
            "DZ","AO","BJ","BW","BF","BI","CM","CV","CF","TD","KM","CG","CD","CI","DJ","EG",
            "GQ","ER","SZ","ET","GA","GM","GH","GN","GW","KE","LS","LR","LY","MG","MW","ML",
            "MR","MU","YT","MA","MZ","NA","NE","NG","RE","RW","SH","ST","SN","SC","SL","SO",
            "ZA","SS","SD","TZ","TG","TN","UG","EH","ZM","ZW");

        Add("AS",
            "AF","AM","AZ","BH","BD","BT","BN","KH","CN","CY","GE","HK","IN","ID","IR","IQ",
            "IL","JP","JO","KZ","KW","KG","LA","LB","MO","MY","MV","MN","MM","NP","KP","OM",
            "PK","PS","PH","QA","SA","SG","KR","LK","SY","TW","TJ","TH","TL","TR","TM","AE",
            "UZ","VN","YE");

        Add("EU",
            "AL","AD","AT","BY","BE","BA","BG","HR","CZ","DK","EE","FO","FI","FR","DE","GI",
            "GR","GG","HU","IS","IE","IM","IT","JE","XK","LV","LI","LT","LU","MT","MD","MC",
            "ME","NL","MK","NO","PL","PT","RO","RU","SM","RS","SK","SI","ES","SJ","SE","CH",
            "UA","GB","VA","AX");

        Add("NA",
            "AI","AG","AW","BS","BB","BZ","BM","BQ","VG","CA","KY","CR","CU","CW","DM","DO",
            "SV","GL","GD","GP","GT","HT","HN","JM","MQ","MX","MS","NI","PA","PR","BL","KN",
            "LC","MF","PM","VC","SX","TT","TC","US","VI");

        Add("SA",
            "AR","BO","BR","CL","CO","EC","FK","GF","GY","PY","PE","SR","UY","VE");

        Add("OC",
            "AS","AU","CK","FJ","PF","GU","KI","MH","FM","NR","NC","NZ","NU","NF","MP","PW",
            "PG","PN","WS","SB","TK","TO","TV","VU","WF");

        Add("AN", "AQ","BV","GS","HM","TF");

        return d;
    }
}
