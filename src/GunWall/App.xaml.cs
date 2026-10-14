using System.Windows;
using System.Windows.Threading;
using GunWall.Services;

namespace GunWall;

public partial class App : Application
{
    private System.IO.FileStream? _engineLock;
    // Held by --unblock / --purge-sublayer until the process exits (a static, so it
    // is not finalised - and the lock released - half way through).
    private static System.IO.FileStream? _recoveryLock;

    private IEngine? _remote;

    /// <summary>The background service's engine, when the window uses the service
    /// (ServiceMode, 0.99.208): connected at startup, handed to the window once.
    /// Null: the window runs the engine itself, as before.</summary>
    public IEngine? TakeRemoteEngine()
    {
        var r = _remote;
        _remote = null;
        return r;
    }

    /// <summary>The engine lock claimed at startup, handed to the window once.</summary>
    public System.IO.FileStream? TakeEngineLock()
    {
        var l = _engineLock;
        _engineLock = null;
        return l;
    }

    /// <summary>
    /// Ends a headless run (--service, --unblock, --purge-sublayer) with its exit
    /// code, at once.
    ///
    /// Not Shutdown(): App.xaml's StartupUri still builds MainWindow after
    /// OnStartup returns - in session 0, as SYSTEM, for the service. And NOT
    /// "StartupUri = null" either: WPF's setter throws ArgumentNullException, which
    /// is exactly what 0.99.199 and 0.99.200 shipped - the service crashed before
    /// its first log line (Event 1026, reported 2026-10-10), and --unblock crashed
    /// after its work, so the uninstaller read a crash code instead of 0 or 1.
    /// </summary>
    private static void ExitHeadless(int code)
    {
        try { DiagnosticLog.Log($"Headless run finished (exit code {code})."); } catch { }
        Environment.Exit(code);
    }

    // ================================================================ stage 4c (0.99.229)

    /// <summary>The window runs as the signed-in user and uses the service: connect
    /// (waiting for it right after Windows starts), or offer to start it. Returns
    /// connected, or ends the process when the person chooses Exit.</summary>
    private void ConnectAsSignedInUser()
    {
        DiagnosticLog.Log("Engine: this window runs as the signed-in user (no administrator rights) and uses the background service.");
        _remote = TryConnectService(out string why);
        if (_remote == null)
        {
            var (state, _) = BackgroundServiceControl.Query();
            var (wait, _) = ServiceMode.ShouldWaitForService(state, Environment.TickCount64);
            if (wait)
            {
                DiagnosticLog.Log($"Engine: the background service is not answering yet (state {state}, Windows started "
                                + $"{Environment.TickCount64 / 1000} s ago) - waiting up to {ServiceMode.StartWait.TotalSeconds:0} s for it.");
                _remote = WaitForService(ServiceMode.StartWait, ref why);
            }
        }
        // The question is a window of its own, before the main one: closing it must
        // not count as "the last window closed" and end GunWall.
        var mode = ShutdownMode;
        if (_remote == null) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        while (_remote == null)
        {
            DiagnosticLog.Log($"Engine: the background service is not available ({why}) - asking whether to start it.");
            var answer = ThemedMessageBox.Show(
                "GunWall's protection service is not running.\n\n"
                + "Your filters keep enforcing, but nothing watches for new programs, and GunWall cannot "
                + "change anything until the service runs.\n\n"
                + "Start it now? Windows will ask for administrator approval.",
                "GunWall", MessageBoxButton.YesNo, MessageBoxImage.Warning, "Start it", "Exit");
            if (answer != MessageBoxResult.Yes)
            {
                DiagnosticLog.Log("Engine: the service is not running and the person chose Exit.");
                Environment.Exit(0);
            }
            int code = Elevation.RunSelfElevated("--elevated start-service", TimeSpan.FromSeconds(90));
            if (code == Elevation.Cancelled) { why = "administrator approval was declined"; continue; }
            DiagnosticLog.Log($"Engine: start-service finished (code {code}) - connecting.");
            _remote = WaitForService(TimeSpan.FromSeconds(45), ref why);
        }
        ShutdownMode = mode;
        DiagnosticLog.Log("Engine: connected to the background service's engine - this window does not run one.");
    }

    /// <summary>One attempt at the engine pipe, refusing a pipe that the GunWall
    /// service's own process does not serve (the pipe is open to the signed-in user,
    /// so another program could create one of the same name).</summary>
    internal static IEngine? TryConnectService(out string why)
    {
        try
        {
            var engine = GunWall.Services.Remote.RemoteEngine.Connect(UpdateService.CurrentVersion, null,
                timeoutMs: 1500, verifyServer: IsServiceProcess);
            why = "connected";
            return engine;
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>The process id is the GunWall service's (from the service manager).</summary>
    internal static bool IsServiceProcess(int pid)
    {
        if (pid <= 0) return false;
        try { return BackgroundServiceControl.Query().Pid == pid; } catch { return false; }
    }

    private static IEngine? WaitForService(TimeSpan max, ref string why)
    {
        var until = DateTime.UtcNow + max;
        string last = "";
        while (DateTime.UtcNow < until)
        {
            var engine = TryConnectService(out why);
            if (engine != null) return engine;
            if (why != last) { DiagnosticLog.Log($"Engine: waiting for the service - {why}"); last = why; }
            System.Threading.Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>A GunWall that is already open, seen without being able to open its
    /// mutex: an elevated one's objects are out of a standard user's reach.</summary>
    private static bool InstanceAlreadyRunning()
    {
        try { using var m = System.Threading.Mutex.OpenExisting(InstanceMutexName); return true; }
        catch (System.Threading.WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch { return false; }
    }

    /// <summary>
    /// This window must run the firewall itself (portable copy, or the background
    /// service off or not installed), which takes administrator rights: GunWall
    /// starts again elevated (one UAC) and this copy exits. Declined: says why, exits.
    /// </summary>
    private static void RelaunchWindowElevated(string[] args)
    {
        string why = ProfilePaths.IsPortable ? "this portable copy runs the firewall in its window"
                   : !BackgroundServiceControl.IsInstalled() ? "the background service is not installed"
                   : !ServiceMode.Preferred ? "Settings has \"This window uses the background service\" off"
                   : "the background service is turned off";
        if (InstanceAlreadyRunning())
        {
            SignalRunningInstance();
            Environment.Exit(0);
        }
        bool again = args.Any(a => a.Trim().TrimStart('-', '/').Equals("relaunched", StringComparison.OrdinalIgnoreCase));
        DiagnosticLog.Log($"Start: {why} - the firewall runs in this window, which needs administrator rights; asking Windows.");
        if (!again)
        {
            int code = Elevation.RunSelfElevated(string.Join(" ", args.Select(Elevation.Quote).Append("--relaunched")), wait: null);
            if (code == 0) Environment.Exit(0);
            DiagnosticLog.Log(code == Elevation.Cancelled ? "Start: administrator approval was declined." : $"Start: could not start as administrator (code {code}).");
        }
        ThemedMessageBox.Show(
            $"GunWall needs administrator rights to run its firewall in this window: {why}.\n\n"
            + "Open GunWall again and choose Yes when Windows asks.",
            "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        Environment.Exit(0);
    }

    /// <summary>A recovery command started without administrator rights: run it
    /// again elevated and return its exit code (2 when declined).</summary>
    private static int RelaunchElevatedAndWait(string[] args)
    {
        AttachConsole(-1);
        void Say(string line) { try { Console.WriteLine(line); } catch { } }
        Say("GunWall needs administrator rights for this - Windows will ask.");
        int code = Elevation.RunSelfElevated(string.Join(" ", args.Select(Elevation.Quote)), TimeSpan.FromMinutes(10));
        if (code == Elevation.Cancelled) { Say("Not run: administrator approval was declined."); return 2; }
        Say(code == 0 ? "Done (it ran in its own window as administrator)." : $"Finished with code {code} - see diagnostics.log.");
        return code;
    }

    /// <summary>"GunWall.exe --authorize &lt;nonce&gt;", run as administrator by the
    /// window: hands the nonce to the service over the hand-over pipe, which admits
    /// only elevated administrators. 0 unlocked, 1 refused, 2 not elevated or no answer.</summary>
    private static int RunAuthorize(string nonce)
    {
        if (!Elevation.IsElevated || nonce.Length == 0) return 2;
        string? answer = EngineOwnership.Ask(EngineOwnership.PipeName, "authorize " + nonce, TimeSpan.FromSeconds(10));
        DiagnosticLog.Log($"Protect settings: unlock {(answer == "unlocked" ? "given" : "not given (" + (answer ?? "no answer") + ")")}.");
        return answer == "unlocked" ? 0 : answer == null ? 2 : 1;
    }

    /// <summary>"GunWall.exe --elevated &lt;task&gt;": one of a fixed list (Elevation.Tasks),
    /// as administrator. 0 done, 1 failed, 2 not elevated or unknown.</summary>
    private static int RunElevatedTask(string task)
    {
        if (!Elevation.IsElevated || Array.IndexOf(Elevation.Tasks, task) < 0) return 2;
        try
        {
            string r;
            switch (task)
            {
                case "start-service":
                    r = BackgroundServiceControl.IsEnabled() ? BackgroundServiceControl.Sc($"start {BackgroundServiceControl.ServiceName}").Code.ToString()
                                                            : BackgroundServiceControl.Enable();
                    for (int i = 0; i < 30 && !BackgroundServiceControl.IsRunning(); i++) System.Threading.Thread.Sleep(1000);
                    DiagnosticLog.Log($"Elevated task: start the background service ({r}) - {(BackgroundServiceControl.IsRunning() ? "running" : "NOT running")}.");
                    return BackgroundServiceControl.IsRunning() ? 0 : 1;
                case "service-on":
                case "service-off":
                    bool on = task == "service-on";
                    r = on ? BackgroundServiceControl.Enable() : BackgroundServiceControl.Disable();
                    BackgroundServiceControl.RecordUserChoice(on);
                    DiagnosticLog.Log($"Elevated task: background service turned {(on ? "on" : "off")}: {r}.");
                    return r == (on ? "on" : "off") ? 0 : 1;
                case "uses-service-on":
                case "uses-service-off":
                    ServiceMode.Set(task == "uses-service-on");
                    DiagnosticLog.Log($"Elevated task: window uses the background service - {(task == "uses-service-on" ? "on" : "off")}.");
                    return 0;
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("ElevatedTask/" + task, ex); return 1; }
        return 2;
    }

    /// <summary>
    /// The service did not answer the window's first question (0.99.225). If it is
    /// starting - typically right after Windows starts, when the window can be
    /// launched at sign-in before the service is up - wait for it rather than take
    /// the engine and keep it for the whole session. Asks "ready" while waiting:
    /// "attach" counts as a request to hand over and would hold a starting service
    /// back. Returns the answer to the final "attach", or null to run the engine here.
    /// </summary>
    private static string? WaitForStartingService()
    {
        try
        {
            var (state, _) = BackgroundServiceControl.Query();
            long uptime = Environment.TickCount64;
            var (wait, startIt) = ServiceMode.ShouldWaitForService(state, uptime);
            if (!wait) return null;
            DiagnosticLog.Log($"Engine: the background service is not answering yet (state {state}, Windows started {uptime / 1000} s ago) - "
                            + $"{(startIt ? "starting it and " : "")}waiting up to {ServiceMode.StartWait.TotalSeconds:0} s for it.");
            if (startIt) BackgroundServiceControl.Sc($"start {BackgroundServiceControl.ServiceName}");
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < ServiceMode.StartWait)
            {
                if (EngineOwnership.Ask(EngineOwnership.PipeName, "ready", TimeSpan.FromSeconds(3)) == "ready")
                {
                    DiagnosticLog.Log($"Engine: the background service is up after {(DateTime.UtcNow - started).TotalSeconds:0} s.");
                    return EngineOwnership.Ask(EngineOwnership.PipeName, "attach", TimeSpan.FromSeconds(15));
                }
                System.Threading.Thread.Sleep(1000);
            }
            DiagnosticLog.Log($"Engine: the background service did not come up within {ServiceMode.StartWait.TotalSeconds:0} s.");
        }
        catch (Exception ex) { DiagnosticLog.LogException("WaitForStartingService", ex); }
        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ------------------------------------------------ background service
        // GunWall.exe --service is started by Windows, never by hand (0.99.199).
        // Checked before anything else: no window, no theme, no UI timing, and
        // its own log (service.log) so it never appends to the window's.
        foreach (string arg in e.Args)
        {
            if (arg.Trim().TrimStart('-', '/').Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                ExitHeadless(ServiceShell.Run());
                return;
            }
        }

        // ------------------------------------------------ elevated helpers (stage 4c, 0.99.229)
        // GunWall.exe started again as administrator by a window that runs as the
        // signed-in user, for one task: --authorize <nonce> (Protect settings) or
        // --elevated <task> (start the service, switch it). Headless, like --service.
        for (int i = 0; i < e.Args.Length; i++)
        {
            string a = e.Args[i].Trim().TrimStart('-', '/').ToLowerInvariant();
            if (a is not ("authorize" or "elevated")) continue;
            string arg = i + 1 < e.Args.Length ? e.Args[i + 1].Trim() : "";
            ExitHeadless(a == "authorize" ? RunAuthorize(arg) : RunElevatedTask(arg));
            return;
        }

        Services.PerfMonitor.MarkStartup("app");
        Services.PerfMonitor.RegisterUiThread();   // before anything is timed
        WindowTheme.Register();   // every standard title bar in the app's theme (0.99.182)
        UiZoom.RegisterPopups();  // right-click menus and hover tips follow the UI size (0.99.229)

        DiagnosticLog.Log("App starting (OnStartup).");

        // ------------------------------------------------ emergency recovery
        //
        // GunWall's filters live in the kernel, not in this process: they keep
        // enforcing after a crash or a close, which is what a kernel firewall must
        // do. (They are not persistent, so a reboot clears them - see WfpEngine.
        // FilterFlags.) The cost is that a machine can be left filtered by software that is not
        // running - and with nothing running, nothing can prompt, so a program
        // without a rule simply fails and says nothing.
        //
        // That state was reached during testing: the app was force-killed with
        // protection on, and the machine lost every connection it had no rule for,
        // including the VPN. Reopening GunWall fixes it - but only if GunWall can
        // still be opened. If the window will not start, or the folder has been
        // deleted, or the person does not know GunWall is the cause, there was no
        // way out at all.
        //
        //     GunWall.exe --unblock
        //
        // is that way out. It tears down everything, restores the hosts file and
        // adapter DNS, prints what it did, and exits without showing a window. It
        // runs before any UI is constructed so a UI fault cannot prevent recovery,
        // which is the whole point of it.
        foreach (string arg in e.Args)
        {
            string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
            if (a is "purge-sublayer" or "purgesublayer" or "unblock" or "panic" or "reset" && !Elevation.IsElevated)
            {
                // GunWall.exe no longer starts as administrator by itself (0.99.229):
                // the recovery commands ask Windows, run elevated, and pass the
                // result on - the uninstaller already runs them elevated.
                ExitHeadless(RelaunchElevatedAndWait(e.Args));
                return;
            }
            if (a is "purge-sublayer" or "purgesublayer")
            {
                int pcode = RunPurgeSublayer();
                ExitHeadless(pcode);
                return;
            }

            if (a is not ("unblock" or "panic" or "reset")) continue;

            int code = RunEmergencyUnblock();
            ExitHeadless(code);
            return;
        }

        // ------------------------------------------------ administrator, or not (stage 4c)
        // The installed window uses the background service and runs as the
        // signed-in user - no UAC to open GunWall. A window that runs the firewall
        // itself (portable copy, or the service turned off) needs administrator
        // rights: it asks Windows, here, before it claims the single instance - the
        // elevated copy it starts must be able to claim it.
        bool elevated = Elevation.IsElevated;
        bool useService = ServiceMode.Requested;
        if (!elevated && !useService)
        {
            RelaunchWindowElevated(e.Args);
            return;
        }

        // ------------------------------------------------ one instance (trap 2.46)
        // Recovery commands above run regardless - they exist for emergencies. A
        // normal launch while GunWall is already running asks it to show its window
        // and exits here, before reading the profile or touching a filter.
        if (!ClaimSingleInstance())
        {
            DiagnosticLog.Log("Second launch: GunWall is already running - asked it to show its window; "
                            + "exiting before touching the profile or any filter.");
            Shutdown(0);
            return;
        }
        ListenForActivation();

        // ------------------------------------------------ data folder access (0.99.200)
        // Before anything opens a file there: 0.99.199's installer could leave the
        // files unreadable even to administrators. Repaired here if so.
        // Only as administrator: the signed-in user reads the folder and never
        // writes it (0.99.229), and could not repair it anyway.
        if (elevated)
        {
            try { DataFolderAccess.EnsureUsable(ProfilePaths.DataFolder, DiagnosticLog.Log); }
            catch (Exception ex) { DiagnosticLog.LogException("DataFolderAccess", ex); }
        }

        // ------------------------------------------------ engine ownership (0.99.199)
        // Taken here, before the window exists, so waiting for the background
        // service to hand over never shows a frozen window. The window adopts the
        // lock (TakeEngineLock) before it reads the profile.
        // The window uses the service (0.99.208; the default since 0.99.224 wherever
        // the service is installed and on): ask it to keep the engine and connect.
        // Anything short of a working connection falls back to the hand-over below -
        // the window is still elevated, so it can always run the engine itself.
        if (elevated) ServiceMode.RemoveLegacyMarker();
        if (useService && !elevated)
        {
            // Stage 4c: no hand-over (its pipe admits administrators only) and no
            // fallback - this window cannot run the firewall. Connected, or the person
            // chooses to start the service or exit (Q5 = A).
            ConnectAsSignedInUser();
        }
        else if (useService)
        {
            try
            {
                DiagnosticLog.Log("Engine: this window uses the background service - asking it to keep the engine.");
                string? answer = EngineOwnership.Ask(EngineOwnership.PipeName, "attach", TimeSpan.FromSeconds(45));
                if (answer == null) answer = WaitForStartingService();
                if (answer == "remote")
                {
                    // The service said yes: a connection that fails now is worth two
                    // more tries before the window takes the engine away from it.
                    for (int attempt = 1; ; attempt++)
                    {
                        try { _remote = GunWall.Services.Remote.RemoteEngine.Connect(UpdateService.CurrentVersion, null); break; }
                        catch (Exception ex) when (attempt < 3)
                        {
                            DiagnosticLog.Log($"Engine: connecting to the service failed (attempt {attempt}: {ex.GetType().Name}: {ex.Message}) - trying again.");
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    DiagnosticLog.Log("Engine: connected to the background service's engine - this window does not run one.");
                }
                else
                    DiagnosticLog.Log($"Engine: the service did not keep the engine (answer: {answer ?? "none"}) - this window runs it.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log($"Engine: could not connect to the background service ({ex.GetType().Name}: {ex.Message}) - this window runs the engine.");
                _remote = null;
            }
        }
        if (_remote == null && elevated)
        {
            try
            {
                _engineLock = EngineOwnership.ClaimForWindow(
                    ProfilePaths.FileIn(EngineOwnership.LockFileName), EngineOwnership.PipeName,
                    TimeSpan.FromSeconds(45), DiagnosticLog.Log, BackgroundServiceControl.Stop);
            }
            catch (Exception ex) { DiagnosticLog.LogException("ClaimEngine", ex); }
        }

        // Surface unhandled UI-thread exceptions instead of silently dying,
        // and record them for the diagnostics bundle.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                DiagnosticLog.LogException("AppDomain.UnhandledException", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Always mark observed: an unobserved task fault must never take
            // the process down.
            args.SetObserved();

            // Cutting the network (lockdown, a rule, the resolver stopping,
            // an adapter dropping) aborts in-flight sockets and any pooled
            // HTTPS connections behind them. Those faults are expected
            // consequences of GunWall doing its job, not defects - recording
            // them as errors buried real ones under dozens of duplicates.
            if (IsExpectedTeardownFault(args.Exception))
                DiagnosticLog.NoteBenignFault("network teardown (aborted socket)");
            // A background call to the service that failed while it was
            // restarting (0.99.213): the window reconnects and said so once.
            else if (args.Exception != null && args.Exception.Flatten().InnerExceptions.Count > 0
                     && args.Exception.Flatten().InnerExceptions.All(x => x is GunWall.Services.Remote.EngineUnavailableException))
                DiagnosticLog.NoteBenignFault("background service not connected (reconnecting)");
            else if (args.Exception != null && args.Exception.Flatten().InnerExceptions.Count > 0
                     && args.Exception.Flatten().InnerExceptions.All(x => x is GunWall.Services.Remote.SettingsLockedException))
                DiagnosticLog.NoteBenignFault("change not made - Protect settings approval not given");
            else
                DiagnosticLog.LogException("UnobservedTaskException", args.Exception);
        };
    }

    /// <summary>
    /// True when every exception in the aggregate is a normal consequence of a
    /// socket or task being shut down: cancellation, a disposed socket, or a
    /// Winsock abort/reset. Anything else - even mixed in - is treated as a
    /// real error so genuine faults are never silently swallowed.
    /// </summary>
    private static bool IsExpectedTeardownFault(AggregateException? aggregate)
    {
        if (aggregate == null) return false;
        var inner = aggregate.Flatten().InnerExceptions;
        if (inner.Count == 0) return false;

        foreach (var ex in inner)
        {
            switch (ex)
            {
                case OperationCanceledException:
                case ObjectDisposedException:
                    continue;
                case System.Net.Sockets.SocketException se
                    when se.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted
                                            or System.Net.Sockets.SocketError.ConnectionAborted
                                            or System.Net.Sockets.SocketError.ConnectionReset
                                            or System.Net.Sockets.SocketError.Interrupted
                                            or System.Net.Sockets.SocketError.Shutdown:
                    continue;
                case System.IO.IOException io
                    when io.InnerException is System.Net.Sockets.SocketException:
                    continue;
                default:
                    return false;   // something genuinely unexpected
            }
        }
        return true;
    }

    /// <summary>WPF's own bug, reachable from any list that changes while an
    /// accessibility client is walking it.
    ///
    /// `MS.Internal.WeakDictionary` backs `ItemPeersStorage`, which is how
    /// `ItemsControlAutomationPeer` remembers the automation peer for each item.
    /// Replace the items while UI Automation is enumerating them and the lookup
    /// throws — dotnet/wpf issues #2152 and #7542, open against the framework,
    /// not against anything here. GunWall refreshes its tables every second, so
    /// it reaches this more often than most applications do.
    ///
    /// Deliberately narrow: the exception type AND a WPF-internal frame. A
    /// KeyNotFoundException thrown by GunWall's own code has neither and still
    /// gets the dialog, which is the point — this suppresses a known framework
    /// fault, not a class of exception.</summary>
    // ================================================================ one instance
    //
    // Trap 2.46. Nothing stopped a second GunWall starting. Opening it from the Start
    // menu or a shortcut while it sat in the tray started a complete second firewall
    // manager on the same profile and the same WFP sublayer. The 0.99.156 bundle
    // shows the result: each instance's repair deleted the other's filters, which
    // the other then "repaired" as tampering, every thirty seconds, leaving a full
    // set of 456 orphans; and six seconds after protection was turned OFF in one
    // window, the other reinstalled every filter - OFF on screen, blocking in fact.
    //
    // Named for the machine, not the session: two users' instances would fight
    // over the one sublayer just the same. Creating Global objects needs a privilege
    // standard users lack, so one cannot squat the name to stop GunWall starting.
    private const string InstanceMutexName = @"Global\GunWall.Instance.8f1d2b40";
    private const string ActivateEventName = @"Global\GunWall.Activate.8f1d2b40";

    // Held in a static for the life of the process: a mutex in a local could be
    // collected, and its release would let the next launch through.
    private static System.Threading.Mutex? _instanceMutex;
    private static System.Threading.EventWaitHandle? _activateEvent;

    /// <summary>True when this is the only GunWall UI; otherwise signals the running
    /// one to show itself and returns false.</summary>
    private static bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new System.Threading.Mutex(true, InstanceMutexName, out bool created);
            if (created) return true;
            try { if (_instanceMutex.WaitOne(0)) return true; }
            catch (System.Threading.AbandonedMutexException) { return true; }   // the last holder died: ours now
            SignalRunningInstance();
            return false;
        }
        catch (Exception ex)
        {
            // Fail open: a guard that cannot be checked must not stop the firewall starting.
            DiagnosticLog.LogException("SingleInstance", ex);
            return true;
        }
    }

    private static void SignalRunningInstance()
    {
        // The running instance is in the background; Windows lets it take the
        // foreground only if the process the user just launched allows it.
        try { AllowSetForegroundWindow(-1); } catch { }          // ASFW_ANY
        try
        {
            using var ev = System.Threading.EventWaitHandle.OpenExisting(ActivateEventName);
            ev.Set();
        }
        catch { /* still starting up: it will show its own window */ }
    }

    /// <summary>Shows the window whenever a later launch asks.</summary>
    private void ListenForActivation()
    {
        try
        {
            var ev = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ActivateEventName);
            _activateEvent = ev;
            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try { ev.WaitOne(); } catch { return; }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Current?.MainWindow is GunWall.MainWindow mw) mw.ShowForSecondLaunch();
                    }));
                }
            }) { IsBackground = true, Name = "GunWall activation" }.Start();
        }
        catch (Exception ex) { DiagnosticLog.LogException("SingleInstance/listen", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>Tears everything down with no window. Returns a process exit code:
    /// 0 = clean, 1 = something remained, 2 = could not run at all.
    ///
    /// Deliberately writes to whatever console launched it, because the person
    /// running this has no working GUI and probably no working network either.
    /// A silent recovery tool is no better than none.</summary>
    /// <summary>
    /// Ends every other GunWall process before filtering is removed.
    ///
    /// Terminate rather than request: there is no channel to ask through, and
    /// the caller is usually an uninstaller that will delete this executable in
    /// a moment. Nothing is lost by not exiting gracefully - the store is saved
    /// on every change, and this process saves the post-removal state itself.
    ///
    /// Its own process is excluded by id, not by name. Both are "GunWall".
    /// </summary>
    private static void StopOtherInstances(Action<string> say)
    {
        int self = Environment.ProcessId;
        int stopped = 0;

        // The background service first (0.99.199). It is the same GunWall.exe, so
        // the kill below would reach it - but Windows restarts a service that dies,
        // and a restarted service takes the engine back and re-applies everything
        // this is about to remove. Stopped through the SCM, and kept from starting
        // with Windows until the user turns it back on in Settings.
        try
        {
            if (BackgroundServiceControl.IsInstalled())
            {
                string r = BackgroundServiceControl.DisableForRecovery();
                DiagnosticLog.Log($"Emergency unblock: background service stopped and set to manual start ({r}).");
                say($"  Background service stopped ({r}). Turn it back on in Settings when ready.");
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("StopOtherInstances/service", ex); }

        System.Diagnostics.Process[] found;
        try { found = System.Diagnostics.Process.GetProcessesByName("GunWall"); }
        catch (Exception ex)
        {
            DiagnosticLog.LogException("StopOtherInstances/enumerate", ex);
            return;
        }

        foreach (var proc in found)
        {
            using (proc)
            {
                if (proc.Id == self) continue;
                try
                {
                    proc.Kill();
                    // Without the wait, filter removal can begin while the other
                    // process is still running its last integrity check.
                    proc.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception ex)
                {
                    // Reported, never fatal. Removing the filters is still worth
                    // attempting, and the caller is told what to expect if the
                    // instance survived.
                    DiagnosticLog.LogException($"StopOtherInstances/pid {proc.Id}", ex);
                    say($"  Warning: could not close GunWall (pid {proc.Id}): {ex.Message}");
                    say("  It may re-apply the filters after this completes.");
                }
            }
        }

        if (stopped > 0)
        {
            DiagnosticLog.Log($"Emergency unblock: closed {stopped} running GunWall "
                            + "instance(s) first, so tamper protection cannot re-apply "
                            + "the filters being removed.");
            say($"  Closed {stopped} running instance(s).");
        }
    }

    /// <summary>
    /// Removes EVERY filter in GunWall's sublayer, tracked or not, and reports
    /// the result of each delete individually.
    ///
    /// --unblock removes what the profile knows about, plus orphans found by
    /// enumeration, but RemoveFilters throws on the first real failure - so a
    /// sweep reports one exception and says nothing about the rest. On
    /// 2026-09-12 that produced "4 orphaned filter(s) found - removing" followed
    /// by "sublayer still in use", with no record of what any single delete
    /// returned. Four block-everything filters stayed in the kernel, marked
    /// persistent, and turning protection off left the machine with no network.
    ///
    /// This exists to be run when that has happened. It prints one line per
    /// filter with the exact code, so the next report contains the fact that
    /// was missing rather than an inference about it.
    /// </summary>
    private static int RunPurgeSublayer()
    {
        // Same headless setup as --unblock: everything that starts
        // asynchronously during a normal launch is still starting when this
        // process exits, and a half-started subsystem looks like a crash.
        DnsEventMonitorService.HeadlessRecovery = true;

        AttachConsole(-1);   // parent console, if any
        void Say(string line) { try { Console.WriteLine(line); } catch { } }

        Say("");
        Say("GunWall sublayer purge");
        Say("----------------------");

        FirewallManager? fw = null;
        try
        {
            DiagnosticLog.Log("=== Sublayer purge requested from the command line ===");
            StopOtherInstances(Say);
            // Held for the rest of this run: nothing else may run the engine while
            // filters are being removed (released when the process exits).
            _recoveryLock ??= EngineOwnership.WaitAcquire(ProfilePaths.FileIn(EngineOwnership.LockFileName), TimeSpan.FromSeconds(10));

            fw = new FirewallManager();
            fw.Initialize();

            // One implementation, shared with the Remove all GunWall filtering
            // button. The loop lives in FirewallManager.PurgeSublayer so the
            // button and this command cannot drift - the button having a weaker
            // sweep than the command line is exactly the gap that left a machine
            // with no network.
            var res = fw.PurgeSublayer(line => Say("  " + line));

            Say("");
            Say($"  passes={res.Passes}  removed={res.Removed}  "
              + $"already-gone={res.AlreadyGone}  failed={res.Failed}");
            Say(res.SublayerGone
                ? "  Sublayer removed. The machine is back to Windows defaults."
                : "  Sublayer still IN USE - something still holds a filter in it.");
            Say("");
            Say(res.Remaining == 0
                ? "  Verified: zero filters remain in GunWall's sublayer."
                : $"  WARNING: {res.Remaining} filter(s) still present.");

            Say("");
            Say("  From 0.99.143 GunWall's filters are no longer persistent, so a");
            Say("  REBOOT clears anything still listed above - including filters");
            Say("  this installation can no longer name. If the network is still");
            Say("  wrong after a reboot, it is not GunWall.");

            return res.Failed == 0 && res.Remaining == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException("PurgeSublayer", ex);
            Say($"  FAILED: {ex.Message}");
            return 1;
        }
        finally { try { fw?.Dispose(); } catch { } }
    }

    private static int RunEmergencyUnblock()
    {
        // Set FIRST, before anything is constructed. Everything that starts
        // asynchronously during a normal launch is still starting when this
        // process exits a tenth of a second later, and a half-started subsystem
        // that never finishes looks exactly like a crash to its own guards.
        DnsEventMonitorService.HeadlessRecovery = true;

        AttachConsole(-1);   // parent console, if any
        void Say(string line) { try { Console.WriteLine(line); } catch { } }

        Say("");
        Say("GunWall emergency unblock");
        Say("-------------------------");

        FirewallManager? fw = null;
        try
        {
            DiagnosticLog.Log("=== Emergency unblock requested from the command line ===");

            // BEFORE the engine is touched, not after.
            //
            // A running GunWall watches its own filters and re-installs them when
            // they vanish, which is what tamper protection is for. It cannot tell
            // a deliberate teardown from an attack, so it treated this one as an
            // attack: on 2026-09-06 the uninstaller removed 24 filters at
            // 18:25:38 and the running instance put 28 back at 18:25:47 - nine
            // seconds later, after --unblock had already reported success. The
            // uninstall then completed, leaving filters enforcing in the kernel
            // with nothing installed that could remove them.
            //
            // Nine seconds is why a lock held for the duration of this call would
            // not have helped; the conflict happens after the call returns. The
            // only thing that works is for the other instance to be gone first.
            //
            // The installer also closes GunWall before invoking this (see
            // InitializeUninstall). That is the primary path and this is not a
            // substitute for it - it is what makes the command correct when it is
            // run by hand, which is exactly when a machine is already broken.
            StopOtherInstances(Say);
            // Held for the rest of this run: nothing else may run the engine while
            // filters are being removed (released when the process exits).
            _recoveryLock ??= EngineOwnership.WaitAcquire(ProfilePaths.FileIn(EngineOwnership.LockFileName), TimeSpan.FromSeconds(10));

            fw = new FirewallManager();
            fw.Initialize();
            // Filters only. ClearStore() is deliberately NOT called here.
            //
            // The uninstaller runs this from InitializeUninstall, BEFORE it asks
            // whether to keep the saved profile. Clearing the store here made
            // that prompt dishonest: answering "No" preserved a file that had
            // already been emptied, so uninstall-then-reinstall lost every rule
            // and the user's VirusTotal key while reporting it had kept them.
            //
            // The in-app reset button means both operations and says so in its
            // confirmation. This command means one.
            bool complete = fw.RemoveAllFiltering();
            // Named explicitly. Without this the log shows the same "Reset:" lines a
            // button press produces, and the only thing distinguishing them is the
            // ABSENCE of a session-started line - which is not something anyone
            // should have to notice.
            DiagnosticLog.Log($"=== Emergency unblock finished (complete={complete}) ===");

            Say(complete
                ? "  All GunWall filtering removed. This machine is back to Windows defaults."
                : "  GunWall's own filters and saved rules are gone, but its sublayer was kept "
                  + "because something in it was not created by this installation.");
            Say("  The hosts file and any adapter DNS GunWall changed have been restored.");
            Say("");
            Say("  Verify BEFORE reopening GunWall:");
            Say("      netsh wfp show filters file=%TEMP%\\gw.xml");
            Say("  then search that file for 8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00");
            Say("  Expect ZERO matches. Reopening GunWall first reinstalls four filters");
            Say("  that permit GunWall's own executable, so four matches after a restart");
            Say("  is also correct - and is not filtering of anything else.");
            Say("");
            return complete ? 0 : 1;
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException("EmergencyUnblock", ex);
            Say($"  FAILED: {ex.Message}");
            Say("  Run this from an elevated command prompt - it needs administrator rights.");
            Say("");
            return 2;
        }
        finally { try { fw?.Dispose(); } catch { } }
    }

    private static bool IsWpfAutomationPeerFault(Exception ex)
    {
        if (ex is not System.Collections.Generic.KeyNotFoundException) return false;
        string trace = ex.StackTrace ?? "";
        return trace.Contains("MS.Internal.WeakDictionary", StringComparison.Ordinal)
            || trace.Contains("System.Windows.Automation.Peers", StringComparison.Ordinal);
    }

    /// <summary>
    /// DWM_E_COMPOSITIONDISABLED, as returned by DwmExtendFrameIntoClientArea
    /// when composition is unavailable. Compared numerically and never against
    /// the exception message: Windows localises that text, and the code does
    /// not translate.
    /// </summary>
    private const int DwmCompositionDisabled = unchecked((int)0x80263001);

    /// <summary>
    /// True for the one COM failure a fullscreen game causes and nobody can act
    /// on.
    ///
    /// A game taking the display in EXCLUSIVE fullscreen makes DWM hand the
    /// composition off, and Windows broadcasts WM_DWMCOMPOSITIONCHANGED to every
    /// top-level window. WPF's WindowChrome - which <c>ui:FluentWindow</c> uses -
    /// answers by calling DwmExtendFrameIntoClientArea against a composition that
    /// is no longer there. It is purely the window border; no filtering, no rule
    /// and no kernel state is involved, and there is nothing the reader could do
    /// about it.
    ///
    /// Three conditions, each load-bearing:
    ///   - the TYPE, or a stack-frame match alone would swallow every COM fault
    ///     raised from that frame whatever it was
    ///   - the HRESULT, or this would swallow every COMException WindowChrome
    ///     ever raises, including ones that mean something
    ///   - the WindowChromeWorker FRAME, or this would swallow the same HRESULT
    ///     raised anywhere else in GunWall
    ///
    /// The frame is matched on WindowChromeWorker specifically and never on the
    /// outer frame, which varies: captures of this fault have arrived via both
    /// HwndSubclass.DispatcherCallbackOperation and HwndWrapper.WndProc.
    /// WM_DWMCOMPOSITIONCHANGED is a broadcast, so the answering window is not
    /// fixed.
    /// </summary>
    /// <summary>
    /// UCEERR_RENDERTHREADFAILURE. Read from the maintainer's log and the dialog
    /// he captured, not recalled.
    /// </summary>
    private const int RenderThreadFailure = unchecked((int)0x88980406);

    /// <summary>
    /// True when WPF's render thread has failed under a display-mode change.
    ///
    /// Changing a monitor's refresh rate - 240Hz to 144Hz from inside a game -
    /// is a mode change. WPF answers the resulting window message through
    /// HwndTarget.UpdateWindowPos, pushes the new geometry to the compositor
    /// with DUCE.Channel.SyncFlush, and the compositor is gone.
    ///
    /// Nothing about filtering is involved. The filters are in the kernel and
    /// keep enforcing; this is the window's ability to draw. The reader cannot
    /// act on it, and the dialog it produced appeared over a fullscreen game -
    /// which is the same harm trap 2.24 was written about.
    ///
    /// Three conditions, matching that trap's shape: the TYPE, the HRESULT, and
    /// a WPF rendering frame. `System.Windows.Media` appears in every captured
    /// stack - DUCE.Channel.SyncFlush and MediaContext.CompleteRender both live
    /// under it - while the outer frames vary, exactly as they did for 2.24.
    ///
    /// Suppressing the dialog is NOT the same as pretending it did not happen.
    /// It is counted in the diagnostics footer, and a line naming the remedy is
    /// written, because unlike the DWM fault this one can leave a window that
    /// will not redraw. Restarting GunWall rebuilds the render target; filtering
    /// is unaffected while it is down.
    /// </summary>
    private static bool IsRenderThreadFault(Exception ex)
    {
        if (ex is not System.Runtime.InteropServices.COMException com) return false;
        if (com.HResult != RenderThreadFailure) return false;
        string trace = com.StackTrace ?? "";
        return trace.Contains("System.Windows.Media", StringComparison.Ordinal);
    }

    private static bool IsDwmCompositionFault(Exception ex)
    {
        if (ex is not System.Runtime.InteropServices.COMException com) return false;
        if (com.HResult != DwmCompositionDisabled) return false;
        string trace = com.StackTrace ?? "";
        return trace.Contains("WindowChromeWorker", StringComparison.Ordinal);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The background service is restarting under a window that uses it
        // (0.99.209): the window reconnects by itself and already said so once.
        // A dialog for every repaint meanwhile is noise, not information.
        if (e.Exception is GunWall.Services.Remote.EngineUnavailableException
            || e.Exception.InnerException is GunWall.Services.Remote.EngineUnavailableException)
        {
            DiagnosticLog.NoteBenignFault("background service not connected (reconnecting)");
            e.Handled = true;
            return;
        }

        // Protect settings (0.99.229): a loosening change whose administrator approval
        // was declined. The person just said No - not an error to report, and the
        // change simply did not happen.
        if (e.Exception is GunWall.Services.Remote.SettingsLockedException
            || e.Exception.InnerException is GunWall.Services.Remote.SettingsLockedException)
        {
            DiagnosticLog.Log("Protect settings: a change was not made - administrator approval was not given.");
            e.Handled = true;
            return;
        }

        // Counted, not shown. Nothing the reader could do about it, and an
        // "unexpected error" dialog for a known framework defect teaches people
        // to dismiss dialogs that sometimes matter.
        if (IsWpfAutomationPeerFault(e.Exception))
        {
            DiagnosticLog.NoteBenignFault("WPF automation peer (dotnet/wpf #2152)");
            e.Handled = true;
            return;
        }

        // Same reasoning, different framework defect. This one raises an
        // unowned, non-topmost MessageBox that renders BEHIND the always-on-top
        // connection prompt, so it is never seen - only felt, as a second loss
        // of focus from whatever holds the display (trap 2.24).
        if (IsDwmCompositionFault(e.Exception))
        {
            DiagnosticLog.NoteBenignFault("DWM composition handoff (exclusive-fullscreen app)");
            e.Handled = true;
            return;
        }

        // Same family: a display-mode change took the render thread down. The
        // remedy is logged rather than shown, because the dialog arrives over a
        // fullscreen game and says nothing the reader can use.
        if (IsRenderThreadFault(e.Exception))
        {
            DiagnosticLog.NoteBenignFault("WPF render thread lost (display mode change)");
            DiagnosticLog.Log("WPF's render thread failed under a display-mode change "
                            + "(refresh rate, resolution or GPU reset). Filtering is "
                            + "unaffected - the filters are in the kernel. If GunWall's "
                            + "window stops redrawing, close it from the tray and reopen "
                            + "it; that rebuilds the render target.");
            e.Handled = true;
            return;
        }

        DiagnosticLog.LogException("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
            "You can export a diagnostics bundle from Settings to report this.",
            "GunWall",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true; // keep the app alive
    }
}
