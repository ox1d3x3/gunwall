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
            fw = new FirewallManager();
            fw.Initialize();
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
            Say("  Verify with:  netsh wfp show filters file=%TEMP%\\gw.xml");
            Say("  then search that file for 8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00");
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
