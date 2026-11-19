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

        // Single instance: two copies would each save a stale client.json snapshot over the other's
        // sessions/secrets. A second launch pokes the first one to the front and exits.
        if (!AcquireSingleInstance()) { Shutdown(0); return; }

        // A remote-desktop client should never hard-crash on a transient network/codec hiccup —
        // log and keep the session alive where we safely can.
        DispatcherUnhandledException += OnUnhandledException;

        // Apply the saved UI theme + language before any window is shown.
        var cfg = Services.ClientConfig.Shared;
        Services.ThemeManager.Init(Services.ThemeManager.Parse(cfg.Theme));
        Services.Loc.Init(cfg.Language);

        // Every bordered window must fit its monitor's work area — on first show and after it lands on a
        // monitor with a different scale (a DIP size grows ×1.5 at 150% and the title bar goes off-screen).
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w)
                w.Dispatcher.BeginInvoke(() => SessionRegistry.FitToWorkArea(w, full: true), DispatcherPriority.Loaded);
        }));
        EventManager.RegisterClassHandler(typeof(Window), Window.DpiChangedEvent, new DpiChangedEventHandler((s, _) =>
        {
            if (s is not Window w) return;
            // Wait until the user lets go of the window (a DPI change fires mid-drag across monitors).
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) =>
            {
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
                t.Stop();
                SessionRegistry.FitToWorkArea(w, full: false);
            };
            t.Start();
        }));

        // The window is created here (not via StartupUri) so the headless self-test above can skip it.
        new Views.MainWindow().Show();
    }

    private const string InstanceMutexName = @"Local\LiteRemote.Client";
    private const string ActivateEventName = @"Local\LiteRemote.Client.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    private bool AcquireSingleInstance()
    {
        // An elevated first instance owns the mutex with an admin-only DACL; access denied means
        // another instance exists — exit quietly instead of crashing before the global handler is hooked.
        try { _instanceMutex = new Mutex(false, InstanceMutexName); }
        catch (UnauthorizedAccessException) { return false; }
        bool owned;
        try { owned = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }   // previous owner crashed — we own it now
        if (!owned)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
                return false;
            }
            catch (WaitHandleCannotBeOpenedException) { }
            // Elevated instance's event is not openable from medium integrity — can't raise it, just exit.
            catch (UnauthorizedAccessException) { return false; }
            // The other instance is still exiting (e.g. updater relaunch) — give it a moment, then take over.
            try { owned = _instanceMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var t = new Thread(() =>
        {
            while (true)
            {
                try { _activateEvent.WaitOne(); } catch { return; }
                Dispatcher.BeginInvoke(BringMainWindowToFront);
            }
        }) { IsBackground = true, Name = "SingleInstanceActivate" };
        t.Start();
        return true;
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        // Topmost toggle defeats the foreground lock so the window really comes to the front.
        w.Activate(); w.Topmost = true; w.Topmost = false; w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        // Safety net: never leave a VPN tunnel running in the background after the app quits.
        try { Services.VpnProcessTracker.KillAll(); } catch { }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Diag.Log("UNHANDLED: " + e.Exception);
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "LiteRemote",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
