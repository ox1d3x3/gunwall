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
            DiagnosticLog.LogException("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
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
