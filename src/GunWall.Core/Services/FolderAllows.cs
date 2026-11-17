namespace GunWall.Services;

/// <summary>
/// "Allow a folder" (0.99.241, Mahabub 2026-11-17: Q1 = A, every program inside it).
///
/// A game library or a launcher's folder holds dozens of programs that each ask on
/// their first connection; Kaspersky runs klswapiproxy from a new randomly named
/// file under its cache after each update and asked again after every restart.
/// One folder rule answers all of them: a program inside an allowed folder gets an
/// ordinary allow rule the first time it connects, with no popup.
///
/// What it must not become is a way to allow everything by accident. A folder that
/// any program can write to without administrator rights - the Downloads folder,
/// the desktop, Temp - would let whatever lands there connect, so those are refused
/// outright, as are the roots everything else sits under (a drive, Windows, Program
/// Files, ProgramData, Users, a user's own folder). The checks use the path alone,
/// so the background service, running as SYSTEM, applies exactly the rules the
/// window does: no "current user" is needed to recognise C:\Users\name\Downloads.
/// </summary>
public static class FolderAllows
{
    /// <summary>Folders inside a user's own folder that collect files from anywhere.</summary>
    private static readonly string[] OpenUserFolders =
    {
        "Desktop", "Downloads", "Documents", "Music", "Pictures", "Videos", "OneDrive",
        "AppData", "Favorites", "Links", "Saved Games", "Searches", "Contacts",
    };

    /// <summary>A Windows folder path in one form: backslashes, "." and ".." resolved,
    /// no trailing separator except on a drive root ("C:\"); "" for anything that is
    /// not a full drive or network path. Done by hand rather than with Path, whose
    /// rules are the host's: the bench runs on Linux, where "C:\" is not a root.</summary>
    public static string Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        string f = folder.Trim().Trim('"').Replace('/', '\\');
        string root;
        if (f.Length >= 3 && char.IsAsciiLetter(f[0]) && f[1] == ':' && f[2] == '\\')
            root = char.ToUpperInvariant(f[0]) + ":\\";
        else if (f.StartsWith(@"\\", StringComparison.Ordinal) && !f.StartsWith(@"\\?\", StringComparison.Ordinal) && !f.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            string[] unc = f[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (unc.Length < 2) return "";
            root = @"\\" + unc[0] + "\\" + unc[1] + "\\";
            f = root + string.Join('\\', unc.Skip(2));
        }
        else return "";
        var parts = new List<string>();
        foreach (var seg in f[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = seg.TrimEnd(' ', '.');   // Windows ignores trailing dots and spaces
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            if (t.Length == 0) continue;
            if (t.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0) return "";
            parts.Add(t);
        }
        if (parts.Count == 0) return root.StartsWith(@"\\", StringComparison.Ordinal) ? root.TrimEnd('\\') : root;
        return root + string.Join('\\', parts);
    }

    /// <summary>"C:\" or "\\server\share\" for a normalised path.</summary>
    private static string RootOf(string f)
    {
        if (f.Length >= 3 && f[1] == ':') return f[..3];
        if (!f.StartsWith(@"\\", StringComparison.Ordinal)) return "";
        string[] unc = f[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return unc.Length < 2 ? f : @"\\" + unc[0] + "\\" + unc[1] + "\\";
    }

    /// <summary>True when <paramref name="exePath"/> is inside <paramref name="folder"/>
    /// (any depth). Both are compared as given, without case; the folder must be
    /// normalised. "C:\Games" does not contain "C:\GamesOld\x.exe".</summary>
    public static bool Contains(string folder, string? exePath)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(exePath)) return false;
        if (exePath.Length <= folder.Length) return false;
        if (!exePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        char next = exePath[folder.Length];
        return next is '\\' or '/' || folder.EndsWith('\\');
    }

    /// <summary>The allowed folder that contains <paramref name="exePath"/>, or null.
    /// The deepest one when they nest, so a rule names the folder closest to it.</summary>
    public static string? Match(IEnumerable<string> folders, string? exePath)
    {
        string? best = null;
        foreach (var f in folders)
            if (Contains(f, exePath) && (best == null || f.Length > best.Length)) best = f;
        return best;
    }

    /// <summary>Why <paramref name="folder"/> cannot be allowed, or null when it can.
    /// <paramref name="windowsDir"/> and the Program Files / ProgramData paths are
    /// passed in so the bench can check them without Windows; null reads them from
    /// this machine.</summary>
    public static string? Refusal(string? folder, string? windowsDir = null, string? programFiles = null,
                                  string? programFilesX86 = null, string? programData = null)
    {
        string f = Normalize(folder);
        if (f.Length == 0) return "That is not a folder path.";
        string root = RootOf(f);
        if (f.Length <= root.Length || string.Equals(f.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "A whole drive cannot be allowed - choose the folder of the program or game instead.";

        windowsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        programFilesX86 ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        programData ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        string win = Normalize(windowsDir);
        if (win.Length > 0 && (Same(f, win) || Contains(win, f)))
            return "Windows' own folder cannot be allowed - Windows' programs are allowed one by one, or with \"Allow core Windows processes\" in Settings.";

        foreach (var top in new[] { programFiles, programFilesX86, programData })
        {
            string t = Normalize(top);
            if (t.Length > 0 && Same(f, t))
                return $"{t} holds every installed program - choose the folder of one program or game inside it.";
        }

        // Users: the root, a user's own folder, and what collects files from anywhere.
        string[] parts = f[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 1) return "The Users folder holds everyone's files - choose a program's own folder.";
            if (parts.Length == 2) return "A user's own folder cannot be allowed - anything downloaded or saved there would connect.";
            string inner = parts[2];
            foreach (var open in OpenUserFolders)
                if (inner.Equals(open, StringComparison.OrdinalIgnoreCase) || inner.StartsWith(open + " - ", StringComparison.OrdinalIgnoreCase))
                {
                    // AppData\Local\Programs\<app> and AppData\Roaming\<app> are where
                    // per-user installs live; the AppData folders themselves are not.
                    if (open == "AppData")
                    {
                        if (parts.Length <= 4) return "This AppData folder is shared by every program you run - choose the program's own folder inside it.";
                        if (parts[3].Equals("Local", StringComparison.OrdinalIgnoreCase) && parts[4].Equals("Temp", StringComparison.OrdinalIgnoreCase))
                            return "Temporary folders cannot be allowed - any program can put files there.";
                        if (parts.Length == 5 && parts[3].Equals("Local", StringComparison.OrdinalIgnoreCase) && parts[4].Equals("Programs", StringComparison.OrdinalIgnoreCase))
                            return "AppData\\Local\\Programs holds every program installed for you - choose one program's folder inside it.";
                        break;   // a program's own folder in AppData: on to the Temp check
                    }
                    // The folder itself collects files from anywhere; a folder made inside
                    // it is one you chose to put programs in (0.99.242: Mahabub kept a
                    // test copy of OPxFlasher in a folder inside one and was refused).
                    // Allowed, with the caution below.
                    if (parts.Length == 3)
                        return $"Your {open} folder collects files from anywhere - a program saved there later would connect without asking. "
                             + $"Make a folder inside it for the programs you want allowed, and choose that.";
                    break;
                }
        }

        foreach (var p in parts)
            if (p.Equals("Temp", StringComparison.OrdinalIgnoreCase) || p.Equals("Tmp", StringComparison.OrdinalIgnoreCase))
                return "Temporary folders cannot be allowed - any program can put files there.";
        return null;
    }

    /// <summary>A caution for the confirmation, or null: folders a program can write
    /// to without administrator rights.</summary>
    public static string? Caution(string folder)
    {
        string f = Normalize(folder);
        string root = RootOf(f);
        string[] parts = f.Length > root.Length ? f[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        if (parts.Length >= 4 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
            foreach (var open in OpenUserFolders)
                if (open != "AppData" && (parts[2].Equals(open, StringComparison.OrdinalIgnoreCase) || parts[2].StartsWith(open + " - ", StringComparison.OrdinalIgnoreCase)))
                    return $"This folder is inside your {open} folder. Keep only programs you trust in it, and do not save downloads straight into it: "
                         + "anything placed there will connect without asking.";
        if (parts.Length >= 1 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
            return "This folder is inside a user's own folder, so programs you run can add files to it without asking for administrator rights.";
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return "This is a network folder: anyone who can write to it can add programs that will connect.";
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>When the allows that end by themselves end (0.99.241). Separate so the
/// bench can check the edges.</summary>
public static class TemporaryAllowClock
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>How long a program allowed until it closes may be gone before its
    /// allow ends: long enough for an updater or a launcher that restarts itself.</summary>
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    /// <summary>For a program not seen running since it was allowed - allowed from the
    /// list while it was closed, or after GunWall restarted - a minute to start.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(1);

    /// <summary>True when an until-it-closes allow has ended: the program is not running
    /// now, and has not been since <paramref name="sinceUtc"/> for longer than the grace.</summary>
    public static bool ExitExpired(bool seenRunning, DateTime sinceUtc, DateTime nowUtc) =>
        nowUtc - sinceUtc >= (seenRunning ? ExitGrace : StartGrace);
}
