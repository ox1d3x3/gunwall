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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
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
