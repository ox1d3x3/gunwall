using System.IO;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// What the detector needs from the firewall. FirewallManager implements it; the
/// bench tests a fake, so the decisions below are proven without Windows.
/// </summary>
public interface IDetectionPolicy
{
    bool AlertsEnabled { get; }
    bool StrictMode { get; }
    /// <summary>Allow Windows' core processes without asking (0.99.195).</summary>
    bool AutoAllowCoreWindows { get; }
    bool IsBlocked(string exePath);
    bool IsAllowed(string exePath);
    bool IsSilent(string exePath);
    bool MarkKnown(string exePath);
    void SeedKnownApps(IEnumerable<string> exePaths);
    string? ApplyEntityBlocks(string appPath, string remoteIp);
    string ExplainVerdict(string exePath, string remoteAddress, int remotePort,
                          string protocol, bool outbound, out bool blocked);
    void LogPacketToFile(DateTime time, bool blocked, string app, string protocol,
                         string direction, string remote, string exePath);
}

/// <summary>An undecided application to ask the user about.</summary>
public sealed record PromptRequest(string ProcessName, string ExePath, string RemoteAddress,
                                   int RemotePort, string Protocol, DateTime Time);

/// <summary>One Packet Log row: GunWall's verdict for a kernel event, and why.</summary>
public sealed record PacketVerdict(string AppName, string ExePath, string Protocol,
                                   string Direction, string RemoteEndpoint, bool Blocked,
                                   string Reason);

/// <summary>Everything one detection pass decided. The window only displays it.</summary>
public sealed class DetectionResult
{
    public List<NetActivityEvent> Activity { get; } = new();
    public PacketVerdict? Packet { get; set; }
    public List<PromptRequest> Prompts { get; } = new();
    /// <summary>Windows core processes to allow without a popup (0.99.195): the
    /// setting is on and the path is one of CoreWindowsProcesses.</summary>
    public List<PromptRequest> AutoAllow { get; } = new();
    /// <summary>Apps seen on the network for the first time (monitoring mode).</summary>
    public List<string> FirstSeen { get; } = new();
    /// <summary>The window should try to show the next queued prompt: a prompt was
    /// added (event path), or the poll reached its prompting pass. Same points at
    /// which the window called ShowNextAlert before this moved.</summary>
    public bool ShowQueued { get; set; }
}

/// <summary>
/// The approval pipeline: which applications to ask about, which connections to
/// block for a country / ASN rule, and what the Packet Log says (0.99.191, service
/// split stage 1b). Moved out of the window unchanged, so a background service can
/// run the same decisions later and send the prompts to the window.
///
/// Two inputs, as before: the kernel event path (one connection, as it happens)
/// and the 300 ms poll (every connection). Both share one prompted-this-session
/// set, so an app asked about by one is never asked again by the other.
///
/// Not thread-safe: call from one thread. Today that is the window's UI thread,
/// exactly where this logic ran before it moved.
/// </summary>
public sealed class ConnectionDetector
{
    private readonly IDetectionPolicy _fw;
    private readonly string _self;
    private readonly string? _system32;
    private readonly HashSet<string> _prompted = new(StringComparer.OrdinalIgnoreCase);
    private bool _knownSeeded;

    public ConnectionDetector(IDetectionPolicy firewall, string? selfPath, string? system32Dir = null)
    {
        _fw = firewall;
        _self = selfPath ?? "";
        _system32 = system32Dir;
    }

    /// <summary>Either a popup or, for a Windows core process with the setting on,
    /// an automatic allow. Called once per app per session.</summary>
    private void Ask(DetectionResult r, PromptRequest q)
    {
        if (_fw.AutoAllowCoreWindows && CoreWindowsProcesses.IsCore(q.ExePath, _system32))
            r.AutoAllow.Add(q);
        else
            r.Prompts.Add(q);
    }

    private bool IsSelf(string path) =>
        string.Equals(path, _self, StringComparison.OrdinalIgnoreCase);

    /// <summary>A mode change re-prompts for every undecided app.</summary>
    public void ResetPrompts() => _prompted.Clear();

    /// <summary>An app already asked about by other means - a prompt the background
    /// service queued while the window was closed (0.99.199) - so detection does not
    /// ask again this session. False if it was already marked.</summary>
    public bool MarkPrompted(string exePath) => !string.IsNullOrEmpty(exePath) && _prompted.Add(exePath);

    /// <summary>The kernel event path: one filtered connection.</summary>
    public DetectionResult OnKernelEvent(string appPath, string? remoteAddress, int remotePort,
                                         string protocol, bool dropped, DateTime now)
    {
        var r = new DetectionResult();
        if (string.IsNullOrEmpty(appPath) || IsSelf(appPath)) return r;

        string appName = Path.GetFileNameWithoutExtension(appPath);
        string remote = remoteAddress ?? "";

        // Activity feed: every event, drop or allow.
        string verb = dropped ? "blocked" : "connected to";
        string where = remote.Length == 0 ? "" : $" {remote}:{remotePort}";
        r.Activity.Add(new NetActivityEvent { ProcessName = appName, Detail = $"{verb}{where} ({protocol})" });

        // Packet Log: verdict and reason from GunWall's own rule state, the same
        // precedence the engine enforces.
        string reason = _fw.ExplainVerdict(appPath, remote, remotePort, protocol,
                                           outbound: true, out bool blocked);

        // Where GunWall's opinion and the kernel's record disagree, the
        // disagreement is the useful fact. Dropped by the kernel but not by
        // GunWall: something else on this machine is filtering (Windows Firewall,
        // an antivirus, a VPN client) - saying "Allowed" would point at the wrong
        // program. Allowed by the kernel against a GunWall block: a filter is
        // missing or outranked.
        if (dropped && !blocked)
        {
            blocked = true;
            reason = "Blocked by something else - not GunWall";
        }
        else if (!dropped && blocked)
        {
            reason += " (kernel allowed it - filter may be missing)";
        }
        r.Packet = new PacketVerdict(appName, appPath, protocol, "Out",
            remote.Length == 0 ? "—" : $"{remote}:{remotePort}", blocked, reason);
        _fw.LogPacketToFile(now, blocked, appName, protocol, "Out",
            remote.Length == 0 ? "" : $"{remote}:{remotePort}", appPath);

        // Country / continent / ASN rules: reactive, independent of the mode.
        string? entReason = _fw.ApplyEntityBlocks(appPath, remote);
        if (entReason != null)
            r.Activity.Add(new NetActivityEvent { ProcessName = appName, Detail = $"blocked {remote} ({entReason})" });

        // Approval: prompt once for an undecided app, and only when protection is
        // on - monitoring mode observes silently.
        if (_fw.IsBlocked(appPath) || _fw.IsAllowed(appPath) || _fw.IsSilent(appPath)) return r;
        if (!_fw.AlertsEnabled) return r;
        if (!_fw.StrictMode) return r;
        if (!_prompted.Add(appPath)) return r;
        Ask(r, new PromptRequest(appName, appPath, remote, remotePort, protocol, now));
        r.ShowQueued = true;
        return r;
    }

    /// <summary>The 300 ms poll: every current connection.</summary>
    public DetectionResult OnPoll(List<ConnectionInfo> conns,
                                  Dictionary<int, (string Name, string Path)> procs, DateTime now)
    {
        var r = new DetectionResult();
        DetectNewApps(conns, procs, now, r);
        ApplyEntityBlocks(conns, procs, r);
        return r;
    }

    private void DetectNewApps(List<ConnectionInfo> conns,
                               Dictionary<int, (string Name, string Path)> procs,
                               DateTime now, DetectionResult r)
    {
        bool strict = _fw.StrictMode;

        // Monitoring mode (allow by default) seeds what is already running, so it
        // does not announce everything at once. Zero Trust prompts for every
        // undecided app, so it never seeds.
        if (!strict && !_knownSeeded)
        {
            _knownSeeded = true;
            var seed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in conns)
            {
                if (c.RemoteAddress is "127.0.0.1" or "::1") continue;
                if (procs.TryGetValue(c.ProcessId, out var p) && !string.IsNullOrEmpty(p.Path))
                    seed.Add(p.Path);
            }
            _fw.SeedKnownApps(seed);
            return;
        }

        if (!_fw.AlertsEnabled) return;
        r.ShowQueued = true;

        foreach (var c in conns)
        {
            if (c.RemoteAddress is "127.0.0.1" or "::1") continue;   // loopback is always permitted
            if (!procs.TryGetValue(c.ProcessId, out var proc)) continue;
            if (string.IsNullOrEmpty(proc.Path) || IsSelf(proc.Path)) continue;

            // A decided app never prompts again.
            if (_fw.IsBlocked(proc.Path) || _fw.IsAllowed(proc.Path) || _fw.IsSilent(proc.Path)) continue;

            if (strict)
            {
                // Zero Trust: already blocked by default-deny; ask once per session.
                if (!_prompted.Add(proc.Path)) continue;
            }
            else
            {
                // Monitoring: record first activity, never prompt.
                if (!string.IsNullOrEmpty(c.RemoteAddress) && c.RemoteAddress is not ("0.0.0.0" or "::"))
                    if (_fw.MarkKnown(proc.Path))
                        r.FirstSeen.Add(proc.Path);
                continue;
            }

            string remote = c.RemoteAddress;
            if (remote is "0.0.0.0" or "::") remote = "";   // unbound -> pending
            Ask(r, new PromptRequest(proc.Name, proc.Path, remote ?? "", c.RemotePort, c.Protocol, now));
        }
    }

    /// <summary>Polling-path country / ASN enforcement. Runs beside the event path;
    /// the firewall manager dedups per app and address, so a connection seen by
    /// both costs no extra filter work.</summary>
    private void ApplyEntityBlocks(List<ConnectionInfo> conns,
                                   Dictionary<int, (string Name, string Path)> procs,
                                   DetectionResult r)
    {
        foreach (var c in conns)
        {
            if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
            if (c.RemoteAddress is "127.0.0.1" or "::1" or "0.0.0.0" or "::") continue;
            if (!procs.TryGetValue(c.ProcessId, out var p) || string.IsNullOrEmpty(p.Path)) continue;
            if (IsSelf(p.Path)) continue;
            string? reason = _fw.ApplyEntityBlocks(p.Path, c.RemoteAddress);
            if (reason != null)
                r.Activity.Add(new NetActivityEvent
                {
                    ProcessName = Path.GetFileNameWithoutExtension(p.Path),
                    Detail = $"blocked {c.RemoteAddress} ({reason})"
                });
        }
    }
}
