using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GunWall.Services.Remote;

/// <summary>Who is on the other end of one engine-pipe connection (stage 4c,
/// STAGE-4 §2.6). Read from Windows when the window connects, never from anything
/// the window says about itself.</summary>
/// <param name="ProcessId">From GetNamedPipeClientProcessId.</param>
/// <param name="ImagePath">That process's program file.</param>
/// <param name="SessionId">Its Windows session.</param>
/// <param name="Verified">The program is the installed GunWall.exe - the same file
/// the service runs from.</param>
/// <param name="InConsoleSession">It runs in the session at the physical console
/// (Mahabub, Q5 = A: only that window may change things).</param>
public sealed record EngineCaller(int ProcessId, string ImagePath = "", int SessionId = -1,
                                  bool Verified = false, bool InConsoleSession = false);

/// <summary>What the service does with one call.</summary>
public enum CallVerdict
{
    /// <summary>Runs.</summary>
    Allow,
    /// <summary>Refused for good: not the installed GunWall.exe, or not at the console.</summary>
    Denied,
    /// <summary>Refused until Protect settings is unlocked (--authorize).</summary>
    Locked,
}

/// <summary>
/// The rule (stage 4c). Reads go to anyone the pipe lets in. Changes only from the
/// installed GunWall.exe in the console session. With Protect settings on, a change
/// that loosens protection also needs the 5-minute unlock; one that tightens never
/// does - stricter cannot be abused.
/// </summary>
public static class CallerPolicy
{
    public static CallVerdict Decide(EngineCaller caller, Access access, bool protectSettings, bool unlocked)
    {
        if (access is Access.Read or Access.ReadProfile) return CallVerdict.Allow;
        if (!caller.Verified || !caller.InConsoleSession) return CallVerdict.Denied;
        // Anything not known to be harmless counts as loosening (an unresolved tag too).
        bool loosens = access is not (Access.Tighten or Access.Preference);
        if (loosens && protectSettings && !unlocked) return CallVerdict.Locked;
        return CallVerdict.Allow;
    }

    /// <summary>The same program file: full paths, case-insensitive (Windows).</summary>
    public static bool SameImage(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

/// <summary>Reads who a connected process is, from Windows (stage 4c).</summary>
public static class CallerIdentity
{
    /// <summary>The caller with process id <paramref name="pid"/>, checked against the
    /// service's own program file. Unverified when anything cannot be read.</summary>
    public static EngineCaller Of(int pid, string? serviceImage)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return new EngineCaller(pid);
        string image = ImageOf(pid);
        int session = -1;
        try { if (ProcessIdToSessionId((uint)pid, out uint s)) session = (int)s; } catch { }
        int console = -1;
        try { console = (int)WTSGetActiveConsoleSessionId(); } catch { }
        return new EngineCaller(pid, image, session,
                                Verified: CallerPolicy.SameImage(image, serviceImage),
                                InConsoleSession: session >= 0 && session == console);
    }

    private static string ImageOf(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

/// <summary>
/// Protect settings' unlock (stage 4c, Q3 = A: 5 minutes, locked again early when the
/// window closes).
///
/// 1. The window asks the service for a nonce (BeginSettingsUnlock) - bound to that
///    window's process.
/// 2. It starts "GunWall.exe --authorize &lt;nonce&gt;" as administrator: one UAC.
/// 3. That process hands the nonce to the service over the hand-over pipe, which
///    admits only SYSTEM and elevated administrators - getting through IS the proof.
/// 4. The window's process is unlocked for 5 minutes.
/// A nonce is single-use and expires after 2 minutes; nothing the window sends can
/// unlock it without the elevated step.
/// </summary>
public sealed class SettingsGate
{
    public static readonly TimeSpan UnlockFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NonceLife = TimeSpan.FromMinutes(2);

    /// <summary>The one gate of the service process: the hand-over pipe authorizes
    /// on it, every engine session checks it.</summary>
    public static readonly SettingsGate Service = new();

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Pid, DateTime Issued)> _nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DateTime> _unlockedUntil = new();

    public SettingsGate(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>A fresh nonce for the window with process id <paramref name="pid"/>.</summary>
    public string Begin(int pid)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            var now = _now();
            foreach (var k in _nonces.Where(kv => now - kv.Value.Issued > NonceLife).Select(kv => kv.Key).ToList())
                _nonces.Remove(k);
            if (_nonces.Count > 32) _nonces.Clear();   // nobody legitimately asks this often
            _nonces[nonce] = (pid, now);
        }
        return nonce;
    }

    /// <summary>The elevated helper's step. True when <paramref name="nonce"/> was
    /// issued, is unused and is fresh; its window is then unlocked.</summary>
    public bool Authorize(string? nonce, out int pid)
    {
        pid = 0;
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (_lock)
        {
            if (!_nonces.Remove(nonce, out var n)) return false;
            var now = _now();
            if (now - n.Issued > NonceLife) return false;
            _unlockedUntil[n.Pid] = now + UnlockFor;
            pid = n.Pid;
            return true;
        }
    }

    public bool IsUnlocked(int pid) => SecondsLeft(pid) > 0;

    /// <summary>Seconds of unlock left for that window, 0 when locked.</summary>
    public int SecondsLeft(int pid)
    {
        lock (_lock)
        {
            if (!_unlockedUntil.TryGetValue(pid, out var until)) return 0;
            double s = (until - _now()).TotalSeconds;
            if (s <= 0) { _unlockedUntil.Remove(pid); return 0; }
            return (int)Math.Ceiling(s);
        }
    }

    /// <summary>Locked again: the window closed, or "Lock now".</summary>
    public void Lock(int pid)
    {
        lock (_lock)
        {
            _unlockedUntil.Remove(pid);
            foreach (var k in _nonces.Where(kv => kv.Value.Pid == pid).Select(kv => kv.Key).ToList())
                _nonces.Remove(k);
        }
    }
}
