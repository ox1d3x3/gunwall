using System.Text.RegularExpressions;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Holds the colors used for the app-category dots. Ships with sensible defaults
/// but each can be overridden by the user (persisted in the profile). The brush
/// converter reads from here; the Settings screen writes here. Kept WPF-free so it
/// only stores/validates hex strings - the conversion to a brush happens in the UI.
/// </summary>
public static class CategoryPalette
{
    public static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Signed"]   = "#3FB868", // valid, trusted signature
            ["Unsigned"] = "#E0A53F", // no signature
            ["System"]   = "#5B8DEF", // Windows / system
            ["Invalid"]  = "#E25C5C", // invalid / untrusted signature
            ["Unknown"]  = "#7A828C", // unknown
        };

    private static readonly Dictionary<string, string> Current =
        new(Defaults, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex HexRe = new("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    public static bool IsValidHex(string? s) => !string.IsNullOrWhiteSpace(s) && HexRe.IsMatch(s.Trim());

    /// <summary>Current hex for a named key, falling back to the default.</summary>
    public static string Get(string key) =>
        Current.TryGetValue(key, out var v) && IsValidHex(v) ? v
        : (Defaults.TryGetValue(key, out var d) ? d : "#7A828C");

    public static string ForCategory(AppCategory cat) => cat switch
    {
        AppCategory.Signed   => Get("Signed"),
        AppCategory.Unsigned => Get("Unsigned"),
        AppCategory.System   => Get("System"),
        AppCategory.Invalid  => Get("Invalid"),
        _                    => Get("Unknown")
    };

    /// <summary>Replaces the palette with defaults plus any valid saved overrides.</summary>
    public static void Load(IDictionary<string, string>? saved)
    {
        foreach (var k in Defaults.Keys) Current[k] = Defaults[k];
        if (saved == null) return;
        foreach (var kv in saved)
            if (Defaults.ContainsKey(kv.Key) && IsValidHex(kv.Value))
                Current[kv.Key] = kv.Value.Trim();
    }

    public static void Set(string key, string hex)
    {
        if (Defaults.ContainsKey(key) && IsValidHex(hex)) Current[key] = hex.Trim();
    }

    public static void Reset()
    {
        foreach (var k in Defaults.Keys) Current[k] = Defaults[k];
    }

    public static Dictionary<string, string> ToDict() =>
        new(Current, StringComparer.OrdinalIgnoreCase);
}
