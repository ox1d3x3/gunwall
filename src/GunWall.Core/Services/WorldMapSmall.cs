using System.Collections.Generic;

namespace GunWall.Services;

/// <summary>
/// Countries too small to have an outline in the map data (0.99.227): Singapore,
/// Hong Kong, Bahrain, Malta's neighbours, island states. They had no point either,
/// so traffic to them showed in TOP COUNTRIES but drew no dot and no arc on the map
/// (Mahabub, Singapore). Their latitude / longitude, placed with the same projection
/// as everything else on the map.
/// </summary>
public static partial class WorldMapData
{
    /// <summary>x 0..1000 from longitude, y 0..500 from latitude - equirectangular,
    /// the projection the outlines use (Luxembourg 6.13E 49.8N -> 517, 112).</summary>
    public static (double X, double Y) Project(double lat, double lon) =>
        (Math.Round((lon + 180.0) / 360.0 * 1000.0, 1), Math.Round((90.0 - lat) / 180.0 * 500.0, 1));

    private static readonly (string Code, double Lat, double Lon)[] SmallCountries =
    {
        ("AD", 42.55, 1.60), ("AG", 17.07, -61.80), ("AI", 18.22, -63.06), ("AS", -14.27, -170.70),
        ("AW", 12.52, -69.97), ("AX", 60.18, 19.92), ("BB", 13.19, -59.54), ("BH", 26.07, 50.56),
        ("BL", 17.90, -62.83), ("BQ", 12.18, -68.24), ("BV", -54.42, 3.41), ("CK", -21.24, -159.78),
        ("CV", 15.12, -23.61), ("CW", 12.17, -68.99), ("DM", 15.41, -61.37), ("FM", 6.92, 158.16),
        ("FO", 62.00, -6.79), ("GD", 12.12, -61.68), ("GG", 49.46, -2.59), ("GI", 36.14, -5.35),
        ("GP", 16.27, -61.55), ("GS", -54.43, -36.59), ("GU", 13.44, 144.79), ("HK", 22.32, 114.17),
        ("HM", -53.08, 73.50), ("IM", 54.24, -4.55), ("JE", 49.21, -2.13), ("KI", 1.45, 173.00),
        ("KM", -11.65, 43.33), ("KN", 17.36, -62.78), ("KY", 19.31, -81.25), ("LC", 13.91, -60.98),
        ("LI", 47.17, 9.56), ("MC", 43.74, 7.42), ("MF", 18.07, -63.05), ("MH", 7.13, 171.18),
        ("MO", 22.20, 113.54), ("MP", 15.18, 145.75), ("MQ", 14.64, -61.02), ("MS", 16.74, -62.19),
        ("MU", -20.35, 57.55), ("MV", 3.20, 73.22), ("NF", -29.04, 167.95), ("NR", -0.52, 166.93),
        ("NU", -19.05, -169.87), ("PF", -17.68, -149.41), ("PM", 46.94, -56.27), ("PN", -24.70, -127.44),
        ("PW", 7.51, 134.58), ("RE", -21.12, 55.54), ("SC", -4.68, 55.49), ("SG", 1.35, 103.82),
        ("SH", -15.97, -5.71), ("SJ", 78.22, 15.65), ("SM", 43.94, 12.46), ("ST", 0.19, 6.61),
        ("SX", 18.04, -63.05), ("TC", 21.69, -71.80), ("TK", -9.20, -171.85), ("TO", -21.18, -175.20),
        ("TV", -7.11, 177.65), ("VA", 41.90, 12.45), ("VC", 13.25, -61.20), ("VG", 18.42, -64.64),
        ("VI", 18.34, -64.90), ("WF", -13.77, -177.16), ("WS", -13.76, -172.10), ("XK", 42.60, 20.90),
        ("YT", -12.83, 45.17),
    };

    // Runs after the generated file's initialisers: adds only what has no point yet.
    static WorldMapData()
    {
        foreach (var (code, lat, lon) in SmallCountries)
            CountryPoints.TryAdd(code, Project(lat, lon));
    }
}
