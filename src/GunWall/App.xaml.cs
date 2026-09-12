using System.Windows;
using System.Windows.Threading;
using GunWall.Services;

namespace GunWall;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DiagnosticLog.Log("App starting (OnStartup).");

        // ------------------------------------------------ emergency recovery
        //
        // GunWall's filters are PERSISTENT by design: they keep enforcing after a
        // crash, a close or a reboot, which is what a kernel firewall must do. The
        // cost is that a machine can be left filtered by software that is not
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
            if (a is "purge-sublayer" or "purgesublayer")
            {
                int pcode = RunPurgeSublayer();
                Shutdown(pcode);
                return;
            }

            if (a is not ("unblock" or "panic" or "reset")) continue;

            int code = RunEmergencyUnblock();
            Shutdown(code);
            return;
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

            fw = new FirewallManager();
            fw.Initialize();

            var ids = fw.FindAllSublayerFilterIds();
            if (ids.Count == 0)
            {
                Say("  No filters found in GunWall's sublayer. Nothing to remove.");
                DiagnosticLog.Log("Sublayer purge: sublayer already empty.");
                return 0;
            }

            Say($"  {ids.Count} filter(s) found in GunWall's sublayer.");
            Say("");

            int ok = 0, gone = 0, failed = 0;
            foreach (ulong id in ids)
            {
                uint r = fw.TryDeleteFilter(id);
                if (r == 0) { Say($"    {id,-12} removed"); ok++; }
                else if (r == 0x80320003) { Say($"    {id,-12} not present"); gone++; }
                else { Say($"    {id,-12} FAILED 0x{r:X8}"); failed++; }
                DiagnosticLog.Log($"Sublayer purge: filter {id} -> 0x{r:X8}");
            }

            Say("");
            Say($"  removed={ok}  already-gone={gone}  failed={failed}");

            uint sr = fw.TryDeleteSublayer();
            Say(sr switch
            {
                0          => "  Sublayer removed. The machine is back to Windows defaults.",
                0x80320007 => "  Sublayer was already absent.",
                0x8032000A => "  Sublayer still IN USE - something still holds a filter in it.",
                _          => $"  Sublayer delete returned 0x{sr:X8}."
            });
            DiagnosticLog.Log($"Sublayer purge: sublayer delete -> 0x{sr:X8}");

            var left = fw.FindAllSublayerFilterIds();
            Say("");
            Say(left.Count == 0
                ? "  Verified: zero filters remain in GunWall's sublayer."
                : $"  WARNING: {left.Count} filter(s) STILL present: "
                  + string.Join(", ", left));

            Say("");
            Say("  A reboot is worth doing either way: these filters are marked");
            Say("  persistent, so anything the kernel has not released yet is");
            Say("  re-read from the persistent store at boot.");

            return failed == 0 && left.Count == 0 ? 0 : 2;
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
    private static bool IsDwmCompositionFault(Exception ex)
    {
        if (ex is not System.Runtime.InteropServices.COMException com) return false;
        if (com.HResult != DwmCompositionDisabled) return false;
        string trace = com.StackTrace ?? "";
        return trace.Contains("WindowChromeWorker", StringComparison.Ordinal);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
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
        // of focus from whatever holds the display. See ENGINEERING.md 2.24.
        if (IsDwmCompositionFault(e.Exception))
        {
            DiagnosticLog.NoteBenignFault("DWM composition handoff (exclusive-fullscreen app)");
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
