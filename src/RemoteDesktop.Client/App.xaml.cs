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

        // Headless embedded-RDP smoke test (no interaction): LiteRemote --rdp-test <host[:port]> [logPath].
        // Proves the mstscax.dll ActiveX control instantiates in-process and initiates a connection.
        if (e.Args.Length >= 2 && e.Args[0] == "--rdp-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string log = e.Args.Length >= 3 ? e.Args[2] : "rdp-test.log";
            var win = new Views.RdpWindow(e.Args[1]) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual };
            win.Show();
            win.Dispatcher.BeginInvoke(new Action(() =>
                win.RunConnectProbe(8, report =>
                {
                    try { System.IO.File.WriteAllText(log, report); } catch { }
                    Shutdown(0);
                })), DispatcherPriority.ApplicationIdle);
            return;
        }

        // A remote-desktop client should never hard-crash on a transient network/codec hiccup —
        // log and keep the session alive where we safely can.
        DispatcherUnhandledException += OnUnhandledException;

        // Apply the saved UI theme + language before any window is shown.
        var cfg = Services.ClientConfig.Shared;
        Services.ThemeManager.Init(Services.ThemeManager.Parse(cfg.Theme));
        Services.Loc.Init(cfg.Language);

        // The window is created here (not via StartupUri) so the headless self-test above can skip it.
        new Views.MainWindow().Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Diag.Log("UNHANDLED: " + e.Exception);
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "LiteRemote",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
