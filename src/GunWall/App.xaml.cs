using System.Windows;
using System.Windows.Threading;

namespace GunWall;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface unhandled UI-thread exceptions instead of silently dying.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "GunWall",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true; // keep the app alive
    }
}
