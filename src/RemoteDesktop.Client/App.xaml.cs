using System.Windows;
using System.Windows.Threading;
using RemoteDesktop.Client.Services;

namespace RemoteDesktop.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless H.264 end-to-end check (no UI): LiteRemote --selftest-h264 <host> <port> <password>.
        // Used to validate the codec path against a running host without driving the WPF viewer.
        if (e.Args.Length >= 4 && e.Args[0] == "--selftest-h264")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int port = int.TryParse(e.Args[2], out var p) ? p : 7443;
            string codec = e.Args.Length >= 5 ? e.Args[4] : "auto"; // optional: auto|h264|jpeg
            string res = e.Args.Length >= 6 ? e.Args[5] : "";      // optional: e.g. 960x540
            _ = Task.Run(async () =>
            {
                int code = await H264SelfTest.RunAsync(e.Args[1], port, e.Args[3], codec, res);
                Dispatcher.Invoke(() => Shutdown(code));
            });
            return;
        }

        // A remote-desktop client should never hard-crash on a transient network/codec hiccup —
        // log and keep the session alive where we safely can.
        DispatcherUnhandledException += OnUnhandledException;

        // The window is created here (not via StartupUri) so the headless self-test above can skip it.
        new Views.MainWindow().Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "LiteRemote",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
