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

    /// <summary>Full English country name for an ISO-3166 alpha-2 code; falls back to the code itself.</summary>
    public static string CountryName(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        string k = code.Trim().ToUpperInvariant();
        return _countryName.TryGetValue(k, out var n) ? n : k;
    }

    private static readonly Dictionary<string, string> _continent = Build();

    private static readonly Dictionary<string, string> _countryName =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Africa
        ["DZ"]="Algeria", ["AO"]="Angola", ["BJ"]="Benin", ["BW"]="Botswana", ["BF"]="Burkina Faso",
        ["BI"]="Burundi", ["CM"]="Cameroon", ["CV"]="Cape Verde", ["CF"]="Central African Republic",
        ["TD"]="Chad", ["KM"]="Comoros", ["CG"]="Congo", ["CD"]="DR Congo", ["CI"]="Cote d'Ivoire",
        ["DJ"]="Djibouti", ["EG"]="Egypt", ["GQ"]="Equatorial Guinea", ["ER"]="Eritrea", ["SZ"]="Eswatini",
        ["ET"]="Ethiopia", ["GA"]="Gabon", ["GM"]="Gambia", ["GH"]="Ghana", ["GN"]="Guinea",
        ["GW"]="Guinea-Bissau", ["KE"]="Kenya", ["LS"]="Lesotho", ["LR"]="Liberia", ["LY"]="Libya",
        ["MG"]="Madagascar", ["MW"]="Malawi", ["ML"]="Mali", ["MR"]="Mauritania", ["MU"]="Mauritius",
        ["YT"]="Mayotte", ["MA"]="Morocco", ["MZ"]="Mozambique", ["NA"]="Namibia", ["NE"]="Niger",
        ["NG"]="Nigeria", ["RE"]="Reunion", ["RW"]="Rwanda", ["SH"]="Saint Helena",
        ["ST"]="Sao Tome and Principe", ["SN"]="Senegal", ["SC"]="Seychelles", ["SL"]="Sierra Leone",
        ["SO"]="Somalia", ["ZA"]="South Africa", ["SS"]="South Sudan", ["SD"]="Sudan", ["TZ"]="Tanzania",
        ["TG"]="Togo", ["TN"]="Tunisia", ["UG"]="Uganda", ["EH"]="Western Sahara", ["ZM"]="Zambia",
        ["ZW"]="Zimbabwe",
        // Asia
        ["AF"]="Afghanistan", ["AM"]="Armenia", ["AZ"]="Azerbaijan", ["BH"]="Bahrain", ["BD"]="Bangladesh",
        ["BT"]="Bhutan", ["BN"]="Brunei", ["KH"]="Cambodia", ["CN"]="China", ["CY"]="Cyprus",
        ["GE"]="Georgia", ["HK"]="Hong Kong", ["IN"]="India", ["ID"]="Indonesia", ["IR"]="Iran",
        ["IQ"]="Iraq", ["IL"]="Israel", ["JP"]="Japan", ["JO"]="Jordan", ["KZ"]="Kazakhstan",
        ["KW"]="Kuwait", ["KG"]="Kyrgyzstan", ["LA"]="Laos", ["LB"]="Lebanon", ["MO"]="Macao",
        ["MY"]="Malaysia", ["MV"]="Maldives", ["MN"]="Mongolia", ["MM"]="Myanmar", ["NP"]="Nepal",
        ["KP"]="North Korea", ["OM"]="Oman", ["PK"]="Pakistan", ["PS"]="Palestine", ["PH"]="Philippines",
        ["QA"]="Qatar", ["SA"]="Saudi Arabia", ["SG"]="Singapore", ["KR"]="South Korea", ["LK"]="Sri Lanka",
        ["SY"]="Syria", ["TW"]="Taiwan", ["TJ"]="Tajikistan", ["TH"]="Thailand", ["TL"]="Timor-Leste",
        ["TR"]="Turkey", ["TM"]="Turkmenistan", ["AE"]="United Arab Emirates", ["UZ"]="Uzbekistan",
        ["VN"]="Vietnam", ["YE"]="Yemen",
        // Europe
        ["AL"]="Albania", ["AD"]="Andorra", ["AT"]="Austria", ["BY"]="Belarus", ["BE"]="Belgium",
        ["BA"]="Bosnia and Herzegovina", ["BG"]="Bulgaria", ["HR"]="Croatia", ["CZ"]="Czechia",
        ["DK"]="Denmark", ["EE"]="Estonia", ["FO"]="Faroe Islands", ["FI"]="Finland", ["FR"]="France",
        ["DE"]="Germany", ["GI"]="Gibraltar", ["GR"]="Greece", ["GG"]="Guernsey", ["HU"]="Hungary",
        ["IS"]="Iceland", ["IE"]="Ireland", ["IM"]="Isle of Man", ["IT"]="Italy", ["JE"]="Jersey",
        ["XK"]="Kosovo", ["LV"]="Latvia", ["LI"]="Liechtenstein", ["LT"]="Lithuania", ["LU"]="Luxembourg",
        ["MT"]="Malta", ["MD"]="Moldova", ["MC"]="Monaco", ["ME"]="Montenegro", ["NL"]="Netherlands",
        ["MK"]="North Macedonia", ["NO"]="Norway", ["PL"]="Poland", ["PT"]="Portugal", ["RO"]="Romania",
        ["RU"]="Russia", ["SM"]="San Marino", ["RS"]="Serbia", ["SK"]="Slovakia", ["SI"]="Slovenia",
        ["ES"]="Spain", ["SJ"]="Svalbard and Jan Mayen", ["SE"]="Sweden", ["CH"]="Switzerland",
        ["UA"]="Ukraine", ["GB"]="United Kingdom", ["VA"]="Vatican City", ["AX"]="Aland Islands",
        // North America
        ["AI"]="Anguilla", ["AG"]="Antigua and Barbuda", ["AW"]="Aruba", ["BS"]="Bahamas", ["BB"]="Barbados",
        ["BZ"]="Belize", ["BM"]="Bermuda", ["BQ"]="Caribbean Netherlands", ["VG"]="British Virgin Islands",
        ["CA"]="Canada", ["KY"]="Cayman Islands", ["CR"]="Costa Rica", ["CU"]="Cuba", ["CW"]="Curacao",
        ["DM"]="Dominica", ["DO"]="Dominican Republic", ["SV"]="El Salvador", ["GL"]="Greenland",
        ["GD"]="Grenada", ["GP"]="Guadeloupe", ["GT"]="Guatemala", ["HT"]="Haiti", ["HN"]="Honduras",
        ["JM"]="Jamaica", ["MQ"]="Martinique", ["MX"]="Mexico", ["MS"]="Montserrat", ["NI"]="Nicaragua",
        ["PA"]="Panama", ["PR"]="Puerto Rico", ["BL"]="Saint Barthelemy", ["KN"]="Saint Kitts and Nevis",
        ["LC"]="Saint Lucia", ["MF"]="Saint Martin", ["PM"]="Saint Pierre and Miquelon",
        ["VC"]="Saint Vincent and the Grenadines", ["SX"]="Sint Maarten", ["TT"]="Trinidad and Tobago",
        ["TC"]="Turks and Caicos Islands", ["US"]="United States", ["VI"]="U.S. Virgin Islands",
        // South America
        ["AR"]="Argentina", ["BO"]="Bolivia", ["BR"]="Brazil", ["CL"]="Chile", ["CO"]="Colombia",
        ["EC"]="Ecuador", ["FK"]="Falkland Islands", ["GF"]="French Guiana", ["GY"]="Guyana",
        ["PY"]="Paraguay", ["PE"]="Peru", ["SR"]="Suriname", ["UY"]="Uruguay", ["VE"]="Venezuela",
        // Oceania
        ["AS"]="American Samoa", ["AU"]="Australia", ["CK"]="Cook Islands", ["FJ"]="Fiji",
        ["PF"]="French Polynesia", ["GU"]="Guam", ["KI"]="Kiribati", ["MH"]="Marshall Islands",
        ["FM"]="Micronesia", ["NR"]="Nauru", ["NC"]="New Caledonia", ["NZ"]="New Zealand", ["NU"]="Niue",
        ["NF"]="Norfolk Island", ["MP"]="Northern Mariana Islands", ["PW"]="Palau",
        ["PG"]="Papua New Guinea", ["PN"]="Pitcairn Islands", ["WS"]="Samoa", ["SB"]="Solomon Islands",
        ["TK"]="Tokelau", ["TO"]="Tonga", ["TV"]="Tuvalu", ["VU"]="Vanuatu", ["WF"]="Wallis and Futuna",
        // Antarctica
        ["AQ"]="Antarctica", ["BV"]="Bouvet Island", ["GS"]="South Georgia & South Sandwich Islands",
        ["HM"]="Heard & McDonald Islands", ["TF"]="French Southern Territories",
    };

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
