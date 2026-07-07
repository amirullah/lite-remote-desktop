using System.Windows;
using System.Windows.Threading;

namespace RemoteDesktop.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A remote-desktop client should never hard-crash on a transient network/codec hiccup —
        // log and keep the session alive where we safely can.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "LiteRemote",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
