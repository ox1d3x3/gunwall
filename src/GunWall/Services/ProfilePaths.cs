using System.IO;

namespace GunWall.Services;

/// <summary>Decides, in one place, where GunWall keeps its data.
///
/// This existed in three copies before 0.99.112 — <see cref="RuleStore"/>,
/// <see cref="DiagnosticLog"/>, and implicitly in every caller that reached for
/// <c>AppContext.BaseDirectory</c>. Two of those copies were updated when the
/// profile moved out of the application folder and three call sites were not, so
/// the usage history, the DNS blocklist preset and the observer crash marker
/// carried on being written next to the executable.
///
/// The consequences were the same ones moving the profile was meant to end:
/// replacing the application on upgrade destroyed them, and the uninstaller could
/// not remove them because it never installed them. A rule with three
/// implementations is a rule with two exceptions waiting to be found.
/// </summary>
public static class ProfilePaths
{
    /// <summary>Marker that opts a copy of GunWall into portable mode.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>The folder holding rules, settings, logs and cached data.
    ///
    /// %ProgramData%\GunWall by default, so replacing the executable leaves it
    /// untouched — ProgramData rather than a per-user folder because GunWall runs
    /// elevated and its rules apply to the whole machine.
    ///
    /// Beside the executable instead when <c>portable.txt</c> sits next to it,
    /// which is deliberate and opt-in: the cost of choosing that by accident is
    /// the user's entire configuration.</summary>
    public static string DataFolder
    {
        get
        {
            string appDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(appDir, PortableMarker)))
                return Path.Combine(appDir, "GunWallData");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GunWall");
        }
    }

    /// <summary>A file inside the data folder, creating the folder if needed.
    ///
    /// Named FileIn rather than File: a static method called File in a class that
    /// also calls System.IO.File shadows the type at every use site inside it, and
    /// the compiler reports that as "File.Exists is a method, which is not valid in
    /// the given context" — accurate and thoroughly unhelpful.</summary>
    public static string FileIn(string name)
    {
        string dir = DataFolder;
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, name);
    }

    /// <summary>Moves a file left beside the executable by an older build into the
    /// data folder, once.
    ///
    /// Guarded on the destination not existing, so it cannot overwrite something
    /// newer, and it copies rather than moves — a rollback to a previous build
    /// should still find its data where it left it.</summary>
    public static void MigrateStrayFile(string name)
    {
        try
        {
            string legacy = Path.Combine(AppContext.BaseDirectory, name);
            string target = FileIn(name);
            if (!File.Exists(legacy) || File.Exists(target)) return;
            File.Copy(legacy, target);
            DiagnosticLog.Log($"Migrated {name} from the application folder to {DataFolder}, "
                            + "where replacing the executable will not remove it.");
        }
        catch (Exception ex) { DiagnosticLog.LogException("MigrateStrayFile", ex); }
    }
}
