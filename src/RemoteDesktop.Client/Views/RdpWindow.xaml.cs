using System;
using System.Windows;
using System.Windows.Threading;
using RemoteDesktop.Client.Rendering;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// A self-contained window that embeds a real Windows RDP session (mstscax.dll ActiveX) inside
/// LiteRemote — no external mstsc is launched. RDP signs in with the Windows account, so unlike
/// LiteRemote's own protocol it can also drive the login/lock screen.
/// </summary>
public partial class RdpWindow : Window
{
    private readonly RdpHost _rdp = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _wasConnected;

    public RdpWindow(string host, string? user = null)
    {
        InitializeComponent();
        FormsHost.Child = _rdp;
        HostBox.Text = host ?? string.Empty;
        UserBox.Text = user ?? string.Empty;

        _poll.Tick += (_, _) => UpdateConnectionState();
        Loaded += (_, _) => { _poll.Start(); if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Focus(); else PassBox.Focus(); };
        Closed += (_, _) =>
        {
            _poll.Stop();
            try { if (_rdp.IsConnected) _rdp.Ocx.Disconnect(); } catch { /* control tearing down */ }
        };
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { Status("Isi alamat host dulu."); HostBox.Focus(); return; }

        // The RDP control wants server and port split; accept "host:port" for convenience.
        int port = 3389;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var p)) { port = p; host = host[..colon]; }

        try
        {
            dynamic ocx = _rdp.Ocx;
            ocx.Server = host;
            if (UserBox.Text.Trim().Length > 0) ocx.UserName = UserBox.Text.Trim();

            // Scale the remote desktop to fit our frame, and size the initial session to the frame.
            int w = (int)Math.Max(640, RdpFrame.ActualWidth);
            int h = (int)Math.Max(480, RdpFrame.ActualHeight);
            try { ocx.DesktopWidth = w; ocx.DesktopHeight = h; } catch { }

            TryAdvanced(ocx, port, PassBox.Password);

            ocx.Connect();
            Status($"Menghubungkan ke {host}:{port} …");
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Status("Gagal memulai RDP: " + ex.Message);
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
        }
    }

    // AdvancedSettings exposes a growing list of versioned interfaces; we probe from newest to oldest
    // and set what each supports. ClearTextPassword lets us skip the in-session credential prompt when
    // the control allows it; if it's blocked, RDP simply prompts inside the embedded window instead.
    private static void TryAdvanced(dynamic ocx, int port, string password)
    {
        foreach (var name in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7",
                                     "AdvancedSettings6", "AdvancedSettings5", "AdvancedSettings2", "AdvancedSettings" })
        {
            try
            {
                dynamic adv = GetMember(ocx, name);
                if (adv is null) continue;
                try { adv.RDPPort = port; } catch { }
                try { adv.SmartSizing = true; } catch { }
                try { adv.EnableCredSspSupport = true; } catch { }         // NLA
                try { adv.AuthenticationLevel = 0; } catch { }             // don't hard-fail on cert
                if (password.Length > 0) { try { adv.ClearTextPassword = password; } catch { } }
                return;
            }
            catch { /* this version not present — try older */ }
        }
    }

    private static object? GetMember(object obj, string name)
    {
        try { return obj.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.GetProperty, null, obj, null); }
        catch { return null; }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try { if (_rdp.IsConnected) _rdp.Ocx.Disconnect(); } catch { }
        Status("Terputus.");
    }

    private void UpdateConnectionState()
    {
        bool connected = _rdp.IsConnected;
        if (connected == _wasConnected) return;
        _wasConnected = connected;
        if (connected)
        {
            Status("Terhubung.");
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
        }
        else
        {
            Status("Terputus. Isi kredensial lalu tekan Hubungkan.");
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
        }
    }

    private void Status(string msg) => StatusText.Text = msg;

    /// <summary>
    /// Headless smoke test: instantiate the ActiveX, issue Connect() (no password — enough to prove the
    /// control loads and negotiates), then record the control's Connected state (0=idle, 1=up,
    /// 2=connecting) for <paramref name="seconds"/> and hand the report back. Used by --rdp-test.
    /// </summary>
    internal void RunConnectProbe(int seconds, Action<string> done)
    {
        var log = new System.Text.StringBuilder();
        string state()
        {
            try { return ((int)_rdp.Ocx.Connected).ToString(); }
            catch (Exception ex) { return "err:" + ex.GetType().Name; }
        }

        try { Connect_Click(this, new RoutedEventArgs()); log.AppendLine("Connect() issued OK; OCX instantiated"); }
        catch (Exception ex) { log.AppendLine("Connect() threw: " + ex); done(log.ToString()); return; }

        int ticks = 0, max = Math.Max(1, seconds * 2);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            ticks++;
            log.AppendLine($"t+{ticks * 0.5:0.0}s Connected={state()}");
            if (ticks >= max) { t.Stop(); done(log.ToString()); }
        };
        t.Start();
    }
}
