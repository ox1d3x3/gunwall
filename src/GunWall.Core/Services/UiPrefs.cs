using System.IO;
using System.Text.Json;

namespace GunWall.Services;

/// <summary>
/// How GunWall looks and behaves for the person using it (0.99.214): theme, zoom,
/// font, icon size, tray and notification habits, confirmations, category colours,
/// muted alert categories. None of it decides what may connect.
///
/// These used to live in the machine-wide profile (rules.json). Moved to a file of
/// their own, per Windows user, so that
/// - a window that uses the background service changes them without going through
///   the service (they never need it, and from 4c never need administrator rights);
/// - two people on one PC each keep their own.
///
/// Installed: %AppData%\GunWall\ui.json. Portable: beside the profile, so the
/// portable copy stays self-contained. On first start the values are copied from
/// the profile once; the profile keeps its copies, so an older build still finds
/// them.
/// </summary>
public sealed class UiPrefsData
{
    /// <summary>2 (0.99.219): UiZoomPercent counts from the new 100%, which draws
    /// what 80% drew before. Version 1 files are converted once on load.</summary>
    public int Version { get; set; } = 2;
    public bool ThemeDark { get; set; } = true;
    public string UiFontFamily { get; set; } = "";
    public int UiZoomPercent { get; set; } = 100;
    public int AppIconSize { get; set; } = 22;
    public bool StartMinimized { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool TraySingleClick { get; set; }
    public bool TrayNotifications { get; set; }
    public bool NotificationSound { get; set; }
    public bool ConfirmClearLogs { get; set; } = true;
    public bool AlwaysConfirmExit { get; set; }
    public List<string> MutedAlertCategories { get; set; } = new();
    public Dictionary<string, string> CategoryColors { get; set; } = new();

    /// <summary>Converts a size saved before 0.99.219 to the new scale, keeping how
    /// it looks: the old default (90%) becomes the new default (100%); any other
    /// size the one that draws the same, to the nearest 10.</summary>
    public static int ZoomFromV1(int old) =>
        old == 90 ? 100 : Math.Clamp((int)Math.Round(old / UiScale.BaseFactor / 10.0, MidpointRounding.AwayFromZero) * 10, 50, 150);

    /// <summary>The values the profile carried before 0.99.214 (sizes still on the
    /// old scale: Version 1).</summary>
    public static UiPrefsData From(StoreData d) => new()
    {
        Version = 1,
        ThemeDark = d.ThemeDark,
        UiFontFamily = d.UiFontFamily ?? "",
        UiZoomPercent = d.UiZoomPercent,
        AppIconSize = d.AppIconSize,
        StartMinimized = d.StartMinimized,
        AlwaysOnTop = d.AlwaysOnTop,
        TraySingleClick = d.TraySingleClick,
        TrayNotifications = d.TrayNotifications,
        NotificationSound = d.NotificationSound,
        ConfirmClearLogs = d.ConfirmClearLogs,
        AlwaysConfirmExit = d.AlwaysConfirmExit,
        MutedAlertCategories = new List<string>(d.MutedAlertCategories ?? new()),
        CategoryColors = new Dictionary<string, string>(d.CategoryColors ?? new()),
    };

    /// <summary>Copied back into the profile in memory: an exported profile still
    /// carries them, and an older build started later finds the current ones.</summary>
    public void CopyTo(StoreData d)
    {
        d.ThemeDark = ThemeDark;
        d.UiFontFamily = UiFontFamily;
        d.UiZoomPercent = (int)Math.Round(UiZoomPercent * UiScale.BaseFactor);   // the profile keeps the old scale
        d.AppIconSize = AppIconSize;
        d.StartMinimized = StartMinimized;
        d.AlwaysOnTop = AlwaysOnTop;
        d.TraySingleClick = TraySingleClick;
        d.TrayNotifications = TrayNotifications;
        d.NotificationSound = NotificationSound;
        d.ConfirmClearLogs = ConfirmClearLogs;
        d.AlwaysConfirmExit = AlwaysConfirmExit;
        d.MutedAlertCategories = new List<string>(MutedAlertCategories);
        d.CategoryColors = new Dictionary<string, string>(CategoryColors);
    }
}

public sealed class UiPrefsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string FilePath { get; }
    public UiPrefsData Data { get; private set; } = new();

    public UiPrefsStore(string filePath) { FilePath = filePath; }

    /// <summary>The file for this user. Portable copies keep it beside the profile.</summary>
    public static string DefaultPath =>
        ProfilePaths.IsPortable
            ? ProfilePaths.FileIn("ui.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunWall", "ui.json");

    /// <summary>For logs and diagnostics: the folder named, not the user.</summary>
    public string DisplayPath =>
        ProfilePaths.IsPortable ? FilePath
        : FilePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            ? @"%AppData%\GunWall\ui.json" : FilePath;

    /// <summary>
    /// Reads the file. Missing: seeded from <paramref name="profile"/> and written -
    /// returns "migrated". Unreadable: kept aside as ui.json.bad, seeded the same
    /// way - returns "replaced". Otherwise "loaded".
    /// </summary>
    public string Load(StoreData profile)
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var d = JsonSerializer.Deserialize<UiPrefsData>(File.ReadAllText(FilePath), Json);
                if (d != null)
                {
                    d.MutedAlertCategories ??= new();
                    d.CategoryColors ??= new();
                    d.UiFontFamily ??= "";
                    Data = d;
                    if (UpgradeScale()) { Save(); return "rescaled"; }
                    return "loaded";
                }
            }
            catch (Exception ex) { DiagnosticLog.LogException("UiPrefs.Load", ex); }
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            Data = UiPrefsData.From(profile);
            UpgradeScale();
            Save();
            return "replaced";
        }
        Data = UiPrefsData.From(profile);
        UpgradeScale();
        Save();
        return "migrated";
    }

    /// <summary>Version 1 sizes to the 0.99.219 scale, once. True if it changed.</summary>
    private bool UpgradeScale()
    {
        if (Data.Version >= 2) return false;
        Data.UiZoomPercent = UiPrefsData.ZoomFromV1(Data.UiZoomPercent);
        Data.Version = 2;
        return true;
    }

    /// <summary>Written beside, then renamed over: never half a file.</summary>
    public void Save()
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, FilePath, overwrite: true); return; }
            catch (IOException) when (i < 4) { Thread.Sleep(50); }
        }
    }
}

/// <summary>The UI scale (0.99.219). "100%" draws at <see cref="BaseFactor"/> of the
/// layout's design size - what 80% drew before: at 90-100% the window looked
/// oversized at an ordinary display scale (Mahabub chose 80% as right).</summary>
public static class UiScale
{
    public const double BaseFactor = 0.8;
    public const int DefaultPercent = 100;

    /// <summary>The transform factor for a UI size in percent.</summary>
    public static double Factor(int percent) => percent / 100.0 * BaseFactor;
}
