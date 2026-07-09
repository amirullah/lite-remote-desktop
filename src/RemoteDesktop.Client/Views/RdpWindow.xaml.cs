using System;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RemoteDesktop.Client.Rendering;
using RemoteDesktop.Client.Services;

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
    private bool _rdpReady;   // true once the ActiveX control is attached & created without error
    private SplitTunnelVpn? _vpn;   // active split tunnel for this session, if any
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly System.Windows.Forms.Panel _panel = new() { Dock = System.Windows.Forms.DockStyle.Fill };
    private volatile bool _sessionUp;   // true only after the RDP session is fully logged in (safe to resize)

    public RdpWindow(string host, string? user = null)
    {
        InitializeComponent();
        // NOTE: the ActiveX child is intentionally NOT attached here. AxHost creates its OLE object
        // lazily, and if we attach it in the ctor that creation happens later during an async layout
        // pass — after Show() returns — so any failure escapes Rdp_Click's try/catch and lands in the
        // global DispatcherUnhandledException handler, leaving a broken/empty window. We attach it in
        // OnLoaded (below), where the window handle already exists and we can catch failure while
        // keeping the window visible.
        HostBox.Text = host ?? string.Empty;
        UserBox.Text = user ?? string.Empty;
        try { VpnBox.Text = Services.ClientConfig.Load().LastVpnProfile ?? string.Empty; } catch { }
        LoadSavedRdpCreds();   // prefill remembered RDP user/password for this host, if any

        _poll.Tick += (_, _) => UpdateConnectionState();
        _resize.Tick += (_, _) => { _resize.Stop(); ApplyRemoteSize(); };
        SizeChanged += (_, _) => { _resize.Stop(); _resize.Start(); };   // debounce window resizes
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _poll.Stop();
            try { if (_rdpReady && _rdp.IsConnected) _rdp.Ocx.Disconnect(); } catch { /* control tearing down */ }
            _ = DisposeVpnAsync();   // tear the split tunnel down (SIGTERM) so it doesn't linger
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create the control ALREADY DOCKED inside a WinForms panel — never re-dock a live control.
            // (Re-docking/resizing an mstscax control after Connect is what trips the Win10/11
            // SmartSizing back-buffer blank-to-black bug.) Bracket with ISupportInitialize (AxHost
            // honours it) and realise the OLE object now, while the handle is live and we can catch.
            var init = (System.ComponentModel.ISupportInitialize)_rdp;
            init.BeginInit();
            FormsHost.Child = _panel;              // panel fills the WindowsFormsHost
            _rdp.Dock = System.Windows.Forms.DockStyle.Fill;   // dock BEFORE realizing
            _panel.Controls.Add(_rdp);
            init.EndInit();
            _rdp.CreateControl();   // realise the child HWND (already docked to full size)
            _ = _rdp.Ocx;           // dereference once so a missing/unregistered control throws here
            _rdpReady = true;
        }
        catch (Exception ex)
        {
            // The control could not load (e.g. mstscax not registered on this machine). Keep the
            // window on screen with a clear message instead of a silent no-op / generic crash dialog.
            Services.Diag.Log("RdpWindow Loaded: ActiveX FAILED: " + ex);
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = false;
            Status("Kontrol Windows RDP tidak tersedia di PC ini: " + ex.Message);
            return;
        }

        _poll.Start();
        if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Focus(); else PassBox.Focus();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!_rdpReady) { Status("Kontrol Windows RDP tidak tersedia di PC ini."); return; }
        var raw = HostBox.Text.Trim();
        if (raw.Length == 0) { Status("Isi alamat host dulu."); HostBox.Focus(); return; }
        var (host, port) = SplitHostPort(raw);

        ConnectBtn.IsEnabled = false;
        try
        {
            // VPN-first (optional): if a profile is set and the host isn't reachable yet, open the
            // profile in the system's OpenVPN client (OpenVPN Connect) and wait for the tunnel to make
            // the host reachable before starting RDP. We can't run the tunnel ourselves (needs
            // openvpn.exe + the TAP/Wintun driver + admin), so we orchestrate the installed client and
            // gate RDP on TCP reachability — the whole thing is still one button for the user.
            string vpn = VpnBox.Text.Trim();
            if (vpn.Length > 0 && !await IsReachableAsync(host, port, 700))
            {
                RememberVpn(vpn);
                if (!SplitTunnelVpn.EngineAvailable)
                {
                    Status("Mesin VPN belum ada di build ini. Install ulang LiteRemote (versi ber-VPN), " +
                           "atau nyalakan VPN manual lalu tekan Hubungkan.");
                    ConnectBtn.IsEnabled = true;
                    return;
                }
                if (VpnUserBox.Text.Trim().Length == 0 || VpnPassBox.Password.Length == 0)
                {
                    Status("Isi VPN user & VPN pass dulu untuk menyalakan tunnel.");
                    ConnectBtn.IsEnabled = true;
                    return;
                }

                // Bring up a split tunnel that routes ONLY this host through the VPN.
                await DisposeVpnAsync();
                _vpn = new SplitTunnelVpn();
                bool up = await _vpn.ConnectAsync(vpn, VpnUserBox.Text.Trim(), VpnPassBox.Password, host, Status, CancellationToken.None);
                if (!up) { await DisposeVpnAsync(); ConnectBtn.IsEnabled = true; return; }

                if (!await WaitReachableAsync(host, port, TimeSpan.FromSeconds(15)))
                {
                    Status($"VPN tersambung, tapi {host}:{port} belum terjangkau — cek IP host / RDP aktif di sana.");
                    ConnectBtn.IsEnabled = true;
                    return;
                }
            }

            StartRdp(host, port);
            SaveCredsIfWanted(VpnBox.Text.Trim(), host);   // remember passwords if the box is ticked
        }
        catch (Exception ex)
        {
            Status("Gagal memulai RDP: " + ex.Message);
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
        }
    }

    /// <summary>Configure the ActiveX control and begin the RDP connection to host:port.</summary>
    private void StartRdp(string host, int port)
    {
        _sessionUp = false;
        dynamic ocx = _rdp.Ocx;
        ocx.Server = host;
        if (UserBox.Text.Trim().Length > 0) ocx.UserName = UserBox.Text.Trim();

        // Size the initial session to the frame IN DEVICE PIXELS (not WPF DIPs), and pick dynamic-
        // resolution mode (SmartSizing off) so the remote renders 1:1 and fills crisply. On connect/
        // resize we call UpdateSessionDisplaySettings to re-fit — gated on a real "session up" signal.
        var (w, h) = FramePx();
        try { ocx.DesktopWidth = w; ocx.DesktopHeight = h; } catch { }
        try { ocx.AdvancedSettings2.SmartSizing = false; } catch { }

        TryAdvanced(ocx, port, PassBox.Password);

        ocx.Connect();
        Status($"Menghubungkan RDP ke {host}:{port} …");
        ConnectBtn.IsEnabled = false;
        DisconnectBtn.IsEnabled = true;
    }

    // --- DPI-aware sizing helpers (the RDP control wants device pixels; WPF ActualWidth is in DIPs) ---
    private (double sx, double sy) DpiScale()
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        return m is { } t ? (t.M11, t.M22) : (1.0, 1.0);
    }

    private (uint w, uint h) FramePx()
    {
        var (sx, sy) = DpiScale();
        int w = (int)Math.Round(RdpFrame.ActualWidth * sx); w -= w & 1;   // even
        int h = (int)Math.Round(RdpFrame.ActualHeight * sy); h -= h & 1;
        return ((uint)Math.Clamp(w, 640, 8192), (uint)Math.Clamp(h, 480, 8192));
    }

    private uint DesktopScalePct() => (uint)Math.Round(DpiScale().sx * 100);          // 100/125/150…
    private uint DeviceScale() => DesktopScalePct() switch { >= 170 => 180, >= 130 => 140, _ => 100 };

    // The RDP control wants server and port split; accept "host:port" for convenience.
    private static (string host, int port) SplitHostPort(string raw)
    {
        int port = 3389;
        int colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var p)) { port = p; raw = raw[..colon]; }
        return (raw, port);
    }

    private void PickVpn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pilih profil OpenVPN (.ovpn)",
            Filter = "Profil OpenVPN (*.ovpn)|*.ovpn|Semua berkas (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) { VpnBox.Text = dlg.FileName; RememberVpn(dlg.FileName); }
    }

    private void ClearVpn_Click(object sender, RoutedEventArgs e) { VpnBox.Text = string.Empty; VpnUserBox.Text = string.Empty; VpnPassBox.Password = string.Empty; }

    // When a profile is chosen: prefill the VPN username from the profile (myITS profiles embed
    // setenv USERNAME "NRP@student.its.ac.id"), then load any remembered VPN password for it.
    private void VpnBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (VpnUserBox is null) return;
        var path = VpnBox.Text.Trim();
        if (VpnUserBox.Text.Trim().Length == 0 && path.Length > 0 && File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                var m = Regex.Match(text, "setenv\\s+USERNAME\\s+\"?([^\"\\r\\n]+)\"?", RegexOptions.IgnoreCase);
                if (m.Success) VpnUserBox.Text = m.Groups[1].Value.Trim();
            }
            catch { }
        }
        LoadSavedVpnPass(path);
    }

    private void HostBox_LostFocus(object sender, RoutedEventArgs e) => LoadSavedRdpCreds();

    private void LoadSavedVpnPass(string profile)
    {
        if (VpnPassBox is null || profile.Length == 0 || VpnPassBox.Password.Length > 0) return;
        try
        {
            var pass = ClientConfig.Load().GetSecret("vpn:" + profile);
            if (!string.IsNullOrEmpty(pass)) { VpnPassBox.Password = pass; if (SaveCredsCheck != null) SaveCredsCheck.IsChecked = true; }
        }
        catch { }
    }

    private void LoadSavedRdpCreds()
    {
        if (PassBox is null) return;
        var host = HostBox.Text.Trim();
        if (host.Length == 0) return;
        try
        {
            var combo = ClientConfig.Load().GetSecret("rdp:" + SplitHostPort(host).host);
            if (string.IsNullOrEmpty(combo)) return;
            var parts = combo.Split('\n');
            if (UserBox.Text.Trim().Length == 0 && parts.Length > 0) UserBox.Text = parts[0];
            if (PassBox.Password.Length == 0 && parts.Length > 1) PassBox.Password = parts[1];
            if (SaveCredsCheck != null) SaveCredsCheck.IsChecked = true;
        }
        catch { }
    }

    private void SaveCredsIfWanted(string profile, string host)
    {
        try
        {
            var cfg = ClientConfig.Load();
            bool save = SaveCredsCheck?.IsChecked == true;
            if (profile.Length > 0) cfg.SetSecret("vpn:" + profile, save ? VpnPassBox.Password : null);
            cfg.SetSecret("rdp:" + host, save ? (UserBox.Text.Trim() + "\n" + PassBox.Password) : null);
            cfg.Save();
        }
        catch { }
    }

    private static void RememberVpn(string path)
    {
        try { var cfg = Services.ClientConfig.Load(); cfg.LastVpnProfile = path; cfg.Save(); } catch { }
    }

    private async Task DisposeVpnAsync()
    {
        var v = _vpn; _vpn = null;
        if (v != null) { try { await v.DisposeAsync(); } catch { } }
    }

    /// <summary>One quick TCP connect attempt with a timeout; true if the port accepts within it.</summary>
    private static async Task<bool> IsReachableAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (done != connect)
            {
                // Timed out; disposing the client cancels the pending connect. Observe its eventual
                // fault so it doesn't surface as an unobserved task exception.
                _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            await connect;               // surface a real connect fault as false via the catch
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>Poll until the host:port is reachable (VPN tunnel came up) or the budget elapses.</summary>
    private async Task<bool> WaitReachableAsync(string host, int port, TimeSpan max)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < max)
        {
            if (await IsReachableAsync(host, port, 1200)) return true;
            Status($"Menunggu tunnel VPN & host {host}:{port} … ({(int)(DateTime.UtcNow - start).TotalSeconds}s)");
            await Task.Delay(1500);
        }
        return false;
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

    /// <summary>
    /// Fit the remote desktop to the window by changing the REMOTE resolution to the frame's pixel size
    /// (dynamic resolution). It renders 1:1 — crisp, edge-to-edge, no scaling, no black. Only valid once
    /// the session is fully logged in (<see cref="_sessionUp"/>); calling it too early returns E_FAIL and
    /// leaves the surface black, which is exactly what bit the earlier attempts.
    /// </summary>
    private void ApplyRemoteSize()
    {
        if (!_rdpReady || !_sessionUp) return;
        var (w, h) = FramePx();
        try
        {
            // Late-binding (all args are by-value uint → safe VARIANT marshaling). UpdateSessionDisplaySettings
            // is on the control's default dispatch (IMsRdpClient8+), so no hand-declared interface is needed.
            _rdp.Ocx.UpdateSessionDisplaySettings(w, h, w, h, 0u, DesktopScalePct(), DeviceScale());
        }
        catch (Exception ex) { Services.Diag.Log("[rdp] updatedisplay: " + ex.Message); }
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
            ScheduleSessionUp();   // mark session up after a settle, then fit to the window
        }
        else
        {
            _sessionUp = false;
            Status("Terputus. Isi kredensial lalu tekan Hubungkan.");
            ConnectBtn.IsEnabled = true;
            DisconnectBtn.IsEnabled = false;
        }
    }

    /// <summary>
    /// The Connected flag flips before the RDP session surface is fully established; calling
    /// UpdateSessionDisplaySettings in that window returns E_FAIL and blanks the view. Wait a beat, then
    /// mark the session up and fit it to the window.
    /// </summary>
    private async void ScheduleSessionUp()
    {
        try { await Task.Delay(1500); } catch { }
        if (!_rdpReady || !_rdp.IsConnected) return;
        _sessionUp = true;
        ApplyRemoteSize();
    }

    // Thread-safe: the VPN read loop reports state from a background thread, and touching a WPF element
    // off the UI thread throws. Marshal to the dispatcher.
    private void Status(string msg)
    {
        if (Dispatcher.CheckAccess()) StatusText.Text = msg;
        else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = msg));
    }

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

        try
        {
            var (h, pt) = SplitHostPort(HostBox.Text.Trim());
            StartRdp(h, pt);
            log.AppendLine("Connect() issued OK; OCX instantiated");
        }
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
