using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
public partial class RdpWindow : Window, Services.ISessionWindow
{
    private readonly RdpHost _rdp = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _wasConnected;
    private bool _rdpReady;   // true once the ActiveX control is attached & created without error
    private SplitTunnelVpn? _vpn;   // active split tunnel for this session, if any
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly System.Windows.Forms.Panel _panel = new() { Dock = System.Windows.Forms.DockStyle.Fill };
    private volatile bool _sessionUp;   // true only after the RDP session is fully logged in (safe to resize)
    private bool _fullscreen;
    private bool _autoConnect;   // set by ApplySaved/ApplyDirect → connect automatically once ready
    private bool _directMode;    // one-form connect: the connect bar is hidden (creds came from the hub)
    private string? _directLabel; // optional display name for the saved session (from the main form)
    // If a hidden-bar auto-connect doesn't come up in time (bad host/creds), reveal the bar so the user
    // isn't stuck staring at a black window with no controls.
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(12) };
    private WindowState _prevState = WindowState.Normal;
    private WindowStyle _prevStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResize = ResizeMode.CanResize;
    private Rect _prevBounds;   // windowed position/size, restored when leaving fullscreen
    private Window? _bar;   // auto-hide session toolbar shown on top-edge hover in fullscreen
    private System.Windows.Controls.StackPanel? _barSwitch;   // switch-to-other-session buttons inside _bar
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(120) };

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

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
        // NOTE: VpnBox is intentionally NOT prefilled from LastVpnProfile. A non-empty VpnBox makes
        // Connect_Click treat the target as VPN-only and hard-stop for VPN creds — which silently broke
        // plain (non-VPN) reconnects. VPN is now set explicitly only when the user chooses it.
        LoadSavedRdpCreds();   // prefill remembered RDP user/password for this host, if any

        _watchdog.Tick += (_, _) =>
        {
            // Read the raw ActiveX state: 1 = connected, 2 = still negotiating, 0 = idle/failed.
            int st = 0; try { st = (int)_rdp.Ocx.Connected; } catch { }
            if (st == 1) { _watchdog.Stop(); return; }   // came up — nothing to do
            if (st == 2) return;                          // still connecting — keep waiting (timer repeats)
            _watchdog.Stop();                             // genuinely failed/idle — reveal the form
            RevealForm(Loc.T("Rdp.Status.ConnectFailedCheck"));
        };
        _poll.Tick += (_, _) => UpdateConnectionState();
        _resize.Tick += (_, _) => { _resize.Stop(); ApplyRemoteSize(); };
        _hover.Tick += Hover_Tick;
        SizeChanged += (_, _) => { _resize.Stop(); _resize.Start(); };   // debounce window resizes
        Loaded += OnLoaded;

        // Register as a live session so OTHER windows can switch to this one, and keep our own switch
        // buttons current as sessions open/close.
        SessionRegistry.Register(this, SessionLabel, "RDP");
        SessionRegistry.Changed += OnSessionsChanged;
        RefreshSwitch();

        Closed += (_, _) =>
        {
            SessionRegistry.Changed -= OnSessionsChanged;
            SessionRegistry.Unregister(this);
            _poll.Stop(); _hover.Stop(); _watchdog.Stop();
            try { _bar?.Close(); } catch { }
            try { if (_rdpReady && _rdp.IsConnected) _rdp.Ocx.Disconnect(); } catch { /* control tearing down */ }
            // Tear the split tunnel down and WAIT briefly so it's actually gone before we return — a
            // closed window must never leave a background VPN. Run off-thread to dodge a UI-thread await
            // deadlock (the dispose does socket I/O only, no WPF), bounded so close never hangs.
            try { System.Threading.Tasks.Task.Run(() => DisposeVpnAsync()).Wait(TimeSpan.FromMilliseconds(1500)); } catch { }
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
            ConnectFields.Visibility = Visibility.Visible;   // a hands-off connect may have hidden it
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = false;
            Status(Loc.F("Rdp.Status.ControlUnavailableEx", ex.Message));
            return;
        }

        _poll.Start();
        if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Focus(); else PassBox.Focus();

        if (_autoConnect)   // one-click reconnect / one-form direct connect
            Dispatcher.BeginInvoke(new Action(() => Connect_Click(this, new RoutedEventArgs())), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Prefill this window from a saved RDP session and auto-connect once ready (one-click reconnect).</summary>
    public void ApplySaved(SavedSession s, ClientConfig cfg)
    {
        _directLabel = string.IsNullOrWhiteSpace(s.Label) ? null : s.Label;
        HostBox.Text = s.Port is 0 or 3389 ? s.Host : $"{s.Host}:{s.Port}";
        if (!string.IsNullOrWhiteSpace(s.Username)) UserBox.Text = s.Username;
        bool hasVpn = false;
        if (s.UseVpn && cfg.GetVpn(s.VpnProfileId) is { } v)
        {
            VpnBox.Text = v.OvpnPath;
            if (!string.IsNullOrWhiteSpace(v.Username) && VpnUserBox.Text.Trim().Length == 0) VpnUserBox.Text = v.Username;
            hasVpn = true;
        }
        LoadSavedRdpCreds();   // fill user/password from the saved secret for this host

        // Only go hands-off when we have EVERYTHING needed: the Windows password, and (if this target is
        // behind VPN) the VPN user+password too. Otherwise auto-connecting would hide the form then
        // immediately re-show it with an error — show the prefilled form and let the user finish instead.
        bool haveRdpPwd = PassBox.Password.Length > 0;
        bool vpnReady = !hasVpn || (VpnUserBox.Text.Trim().Length > 0 && VpnPassBox.Password.Length > 0);
        if (haveRdpPwd && vpnReady)
        {
            BeginDirect(hasVpn);
        }
        else
        {
            if (hasVpn) VpnExpander.IsExpanded = true;
            if (!haveRdpPwd) { PassBox.Focus(); Status(Loc.T("Rdp.Status.EnterWindowsPassword")); }
            else { VpnPassBox.Focus(); Status(Loc.T("Rdp.Status.FillVpnCreds")); }
        }
    }

    /// <summary>
    /// Connect straight from the main connect form: fill the Windows credentials (and optional VPN
    /// profile) the hub collected, then connect without showing this window's own login form.
    /// </summary>
    public void ApplyDirect(string? user, string? password, bool remember,
                            string? vpnProfilePath = null, string? vpnUser = null, string? vpnPass = null,
                            string? label = null)
    {
        _directLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (!string.IsNullOrWhiteSpace(user)) UserBox.Text = user.Trim();
        if (password != null) PassBox.Password = password;

        bool hasVpn = !string.IsNullOrWhiteSpace(vpnProfilePath);
        if (hasVpn)
        {
            VpnBox.Text = vpnProfilePath!.Trim();
            if (!string.IsNullOrWhiteSpace(vpnUser)) VpnUserBox.Text = vpnUser.Trim();
            if (!string.IsNullOrEmpty(vpnPass)) VpnPassBox.Password = vpnPass;
            LoadSavedVpnPass(vpnProfilePath!.Trim());   // fill a remembered VPN password if none was passed
        }
        // Apply the remember choice AFTER VpnBox.Text (whose TextChanged→LoadSavedVpnPass may force-check
        // it), so the user's explicit choice from the main form wins.
        if (SaveCredsCheck != null) SaveCredsCheck.IsChecked = remember;
        BeginDirect(hasVpn);
    }

    /// <summary>
    /// Arrange a hands-off connect: hide THIS window's connect inputs entirely (everything was entered on
    /// the main form) and auto-connect. Disconnect/Fullscreen stay in the always-visible action strip; a
    /// watchdog (started once RDP begins negotiating) reveals the inputs again if the connect fails.
    /// </summary>
    private void BeginDirect(bool hasVpn)
    {
        _autoConnect = true;
        _directMode = true;
        if (!hasVpn) VpnBox.Text = string.Empty;   // guarantee no phantom-VPN gate diverts a plain connect
        ConnectFields.Visibility = Visibility.Collapsed;
        VpnExpander.Visibility = Visibility.Collapsed;
        RetryBtn.Visibility = Visibility.Collapsed;
        Status(hasVpn ? Loc.T("Rdp.Status.StartingVpnAndRdp") : Loc.T("Rdp.Status.ConnectingRdp"));
    }

    /// <summary>
    /// A connect attempt failed / the session dropped. For a hands-off (main-form-driven) window we keep
    /// THIS window's connect form HIDDEN and just show the error + a Retry button — everything was entered
    /// on the hub, so to change credentials the user closes this window and edits there. Only when the
    /// form was already showing (a saved RDP with no stored password) do we surface the fields to fix.
    /// </summary>
    private void RevealForm(string msg)
    {
        _watchdog.Stop();
        DisconnectBtn.IsEnabled = false;
        Status(msg);
        if (_directMode)
        {
            RetryBtn.Visibility = Visibility.Visible;   // stay form-free; offer a retry
        }
        else
        {
            ConnectFields.Visibility = Visibility.Visible;
            VpnExpander.Visibility = Visibility.Visible;
            if (VpnBox.Text.Trim().Length > 0) VpnExpander.IsExpanded = true;
            ConnectBtn.IsEnabled = true;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryBtn.Visibility = Visibility.Collapsed;
        Status(Loc.T(VpnBox.Text.Trim().Length > 0 ? "Rdp.Status.StartingVpnAndRdp" : "Rdp.Status.ConnectingRdp"));
        Connect_Click(this, new RoutedEventArgs());
    }

    /// <summary>Remember this RDP target (and any VPN it went through) so it appears in the Recent list
    /// and one-click reconnect can bring the whole thing back up.</summary>
    private void SaveRdpSessionToRecent(string host, int port)
    {
        try
        {
            var cfg = ClientConfig.Shared;
            string? vpnId = null;
            var vpnPath = VpnBox.Text.Trim();
            if (vpnPath.Length > 0)
            {
                var v = cfg.UpsertVpn(new VpnProfile
                {
                    OvpnPath = vpnPath, Username = VpnUserBox.Text.Trim(),
                    SavePassword = SaveCredsCheck?.IsChecked == true,
                });
                vpnId = v.Id;
            }
            cfg.UpsertSession(new SavedSession
            {
                Kind = SessionKind.Rdp, Host = host, Port = port, Label = _directLabel ?? "",
                Username = UserBox.Text.Trim(), SavePassword = SaveCredsCheck?.IsChecked == true,
                UseVpn = vpnId != null, VpnProfileId = vpnId,
            });
        }
        catch { }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!_rdpReady) { Status(Loc.T("Rdp.Status.ControlUnavailable")); return; }
        var raw = HostBox.Text.Trim();
        if (raw.Length == 0) { Status(Loc.T("Rdp.Status.EnterHostFirst")); HostBox.Focus(); return; }
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
                    RevealForm(Loc.T("Rdp.Status.VpnEngineMissing"));
                    return;
                }
                if (VpnUserBox.Text.Trim().Length == 0 || VpnPassBox.Password.Length == 0)
                {
                    RevealForm(Loc.T("Rdp.Status.FillVpnToStart"));
                    return;
                }

                // Bring up a split tunnel that routes ONLY this host through the VPN.
                await DisposeVpnAsync();
                _vpn = new SplitTunnelVpn();
                bool up = await _vpn.ConnectAsync(vpn, VpnUserBox.Text.Trim(), VpnPassBox.Password, host, Status, CancellationToken.None);
                if (!up) { await DisposeVpnAsync(); RevealForm(Loc.T("Rdp.Status.VpnStartFailed")); return; }

                if (!await WaitReachableAsync(host, port, TimeSpan.FromSeconds(15)))
                {
                    RevealForm(Loc.F("Rdp.Status.VpnUpHostUnreachable", host, port));
                    return;
                }
            }

            StartRdp(host, port);
            SaveCredsIfWanted(VpnBox.Text.Trim(), host);   // remember passwords if the box is ticked
            SaveRdpSessionToRecent(host, port);            // show this target in the Recent list
        }
        catch (Exception ex)
        {
            RevealForm(Loc.F("Rdp.Status.StartRdpFailed", ex.Message));
        }
    }

    /// <summary>Configure the ActiveX control and begin the RDP connection to host:port.</summary>
    private void StartRdp(string host, int port)
    {
        _sessionUp = false;
        dynamic ocx = _rdp.Ocx;
        ocx.Server = host;
        if (UserBox.Text.Trim().Length > 0) ocx.UserName = UserBox.Text.Trim();

        // Size the initial session to the frame IN DEVICE PIXELS (not WPF DIPs). On connect/resize we call
        // UpdateSessionDisplaySettings to re-size the REMOTE to match (crisp 1:1) where the server supports
        // dynamic resolution. SmartSizing = TRUE is the guarantee that fullscreen fits ANY viewer screen
        // WITHOUT scrollbars even when the server can't resize (then it scales-to-fit). Set BEFORE Connect so
        // it never has to be toggled live (a live toggle is what trips the SmartSizing blank-to-black bug).
        var (w, h) = FramePx();
        try { ocx.DesktopWidth = w; ocx.DesktopHeight = h; } catch { }
        try { ocx.AdvancedSettings2.SmartSizing = true; } catch { }

        TryAdvanced(ocx, port, PassBox.Password);

        // Send Windows shortcuts (Win, Alt+Tab, Ctrl+Esc) to the REMOTE while this session has focus.
        try { ocx.SecuredSettings.KeyboardHookMode = 1; }
        catch { try { ocx.SecuredSettings2.KeyboardHookMode = 1; } catch { } }

        ocx.Connect();
        Status(Loc.F("Rdp.Status.ConnectingRdpTo", host, port));
        ConnectBtn.IsEnabled = false;
        DisconnectBtn.IsEnabled = true;
        // Now that RDP is actually negotiating, arm the reveal-the-form watchdog for a hands-off connect.
        // (Started here, not in OnLoaded, so a slow VPN bring-up beforehand can't trip a false failure.)
        if (_directMode) _watchdog.Start();
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
            Title = Loc.T("Rdp.PickVpnDialogTitle"),
            Filter = Loc.T("Common.OvpnFileFilter"),
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
            var pass = ClientConfig.Shared.GetSecret("vpn:" + profile);
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
            var combo = ClientConfig.Shared.GetSecret("rdp:" + SplitHostPort(host).host);
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
            var cfg = ClientConfig.Shared;
            bool save = SaveCredsCheck?.IsChecked == true;
            if (profile.Length > 0) cfg.SetSecret("vpn:" + profile, save ? VpnPassBox.Password : null);
            cfg.SetSecret("rdp:" + host, save ? (UserBox.Text.Trim() + "\n" + PassBox.Password) : null);
            cfg.Save();
        }
        catch { }
    }

    private static void RememberVpn(string path)
    {
        try { var cfg = Services.ClientConfig.Shared; cfg.LastVpnProfile = path; cfg.Save(); } catch { }
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
            Status(Loc.F("Rdp.Status.WaitingTunnel", host, port, (int)(DateTime.UtcNow - start).TotalSeconds));
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

                // --- speed + stability over a (possibly TCP) VPN link ---
                // Disable eye-candy the link doesn't need: wallpaper(0x1), full-window-drag(0x2),
                // menu animations(0x4), theming(0x8), cursor shadow(0x20), cursor settings(0x40) → far
                // less to send, snappier. (0x80 font-smoothing / 0x100 desktop-composition are ENABLE
                // bits — deliberately left OFF so they don't add bandwidth.)
                try { adv.PerformanceFlags = 0x6F; } catch { }
                try { adv.BitmapPersistence = 1; } catch { }               // cache tiles to disk → don't resend
                try { adv.CachePersistenceActive = 1; } catch { }
                try { adv.Compress = 1; } catch { }
                // Let RDP measure the link and auto-tune its codec/effects (big win on a slow VPN).
                try { adv.NetworkConnectionType = 7; } catch { }           // 7 = auto-detect
                try { adv.BandwidthDetection = true; } catch { }
                // Nothing to redirect for a viewer session — skip the negotiation/overhead.
                try { adv.RedirectDrives = false; } catch { }
                try { adv.RedirectPrinters = false; } catch { }
                try { adv.RedirectPorts = false; } catch { }
                try { adv.RedirectSmartCards = false; } catch { }
                try { adv.EnableAutoReconnect = true; } catch { }          // survive brief VPN drops
                try { adv.MaxReconnectAttempts = 20; } catch { }
                try { adv.keepAliveInterval = 20000; } catch { }           // detect dead links promptly

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
        Status(Loc.T("Rdp.Status.Disconnected"));
    }

    // ---- Fullscreen (mstsc-style): hide the top bar, borderless-maximize; F11/Esc exits. ----
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; }
    }

    public bool IsFullscreen => _fullscreen;

    public System.Windows.Forms.Screen CurrentScreen =>
        System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle());

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    /// <summary>Enter/leave fullscreen. When entering, cover EXACTLY one monitor (<paramref name="onScreen"/>
    /// or the current one) — a borderless WindowState.Maximized can otherwise span the whole multi-monitor
    /// desktop. Entering while already fullscreen just moves the session to the requested monitor (used by
    /// the switcher to bring another session onto this screen).</summary>
    public void SetFullscreen(bool on, System.Windows.Forms.Screen? onScreen = null)
    {
        if (on)
        {
            var target = onScreen ?? CurrentScreen;
            if (_fullscreen) { PositionToScreen(target); return; }
            _prevState = WindowState; _prevStyle = WindowStyle; _prevResize = ResizeMode;
            _prevBounds = new Rect(Left, Top, Width, Height);
            TopBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;   // drop any maximized state before positioning manually
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            PositionToScreen(target);
            _fullscreen = true;
            _hover.Start();   // reveal the overlay toolbar on top-edge hover (Esc/F11 are eaten by RDP)
            Status(Loc.T("Rdp.Status.FullscreenHint"));
        }
        else
        {
            if (!_fullscreen) return;
            _hover.Stop(); HideBar();
            TopBar.Visibility = Visibility.Visible;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            Left = _prevBounds.Left; Top = _prevBounds.Top;
            Width = _prevBounds.Width; Height = _prevBounds.Height;
            WindowState = _prevState;
            _fullscreen = false;
            Status(_rdp.IsConnected ? Loc.T("Rdp.Status.Connected") : Loc.T("Rdp.StatusDefault"));
        }
    }

    private void PositionToScreen(System.Windows.Forms.Screen screen)
    {
        var b = screen.Bounds; var (dx, dy) = DpiScale();
        Left = b.Left / dx; Top = b.Top / dy; Width = b.Width / dx; Height = b.Height / dy;
    }

    // ---- Auto-hide overlay toolbar (mstsc connection-bar style) ----
    // Polls the physical cursor (works even while the RDP control has mouse/keyboard capture). When the
    // cursor touches the top edge in fullscreen, a separate top-most window slides in over the remote
    // surface (a separate HWND, so no WPF/WinForms airspace issue and no resize of the RDP session).
    private void Hover_Tick(object? sender, EventArgs e)
    {
        if (!_fullscreen || WindowState == WindowState.Minimized) { HideBar(); return; }
        if (!GetCursorPos(out var c)) return;
        var tl = PointToScreen(new Point(0, 0));             // window top-left  (physical px)
        var tr = PointToScreen(new Point(ActualWidth, 0));   // window top-right (physical px)
        double barH = 44 * DpiScale().sy;
        // Constrain to THIS window's own monitor horizontally. A bare Y check also fires when the cursor
        // touches the top edge of the OTHER monitor (tops are aligned), popping the bar over the viewer.
        bool inX = c.X >= tl.X && c.X < tr.X;
        bool nearTop = inX && c.Y <= tl.Y + 2;
        bool overBar = _bar is { IsVisible: true } && inX && c.Y <= tl.Y + barH + 6;
        if (nearTop || overBar) ShowBar(tl); else HideBar();
    }

    private void ShowBar(Point topLeftPhysical)
    {
        EnsureBar();
        RefreshBarSwitch();   // keep the switch buttons current each time the bar appears
        var (dx, dy) = DpiScale();
        _bar!.Left = topLeftPhysical.X / dx;
        _bar.Top = topLeftPhysical.Y / dy;
        _bar.Width = ActualWidth;
        if (!_bar.IsVisible) _bar.Show();
    }

    private void HideBar() { try { if (_bar?.IsVisible == true) _bar.Hide(); } catch { } }

    private void EnsureBar()
    {
        if (_bar != null) return;
        var bg = ThemeBrush("Panel");   // follow the app theme (dark/light)
        var dock = new System.Windows.Controls.DockPanel { Background = bg, LastChildFill = false };

        var hostLbl = new System.Windows.Controls.TextBlock
        {
            Text = "  🖥  " + HostBox.Text.Trim() + "     " + Loc.T("Rdp.Bar.FullscreenSuffix"),
            Foreground = ThemeBrush("Fg"), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(hostLbl, System.Windows.Controls.Dock.Left);

        _barSwitch = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.DockPanel.SetDock(_barSwitch, System.Windows.Controls.Dock.Left);

        var exit = MakeBarButton(Loc.T("Rdp.Bar.ExitFullscreen"), ThemeBrush("Accent"));
        exit.Click += (_, _) => ToggleFullscreen();
        System.Windows.Controls.DockPanel.SetDock(exit, System.Windows.Controls.Dock.Right);

        var disc = MakeBarButton(Loc.T("Common.Disconnect"), ThemeBrush("Bad"));
        disc.Click += (_, _) => { Disconnect_Click(this, new RoutedEventArgs()); ToggleFullscreen(); };
        System.Windows.Controls.DockPanel.SetDock(disc, System.Windows.Controls.Dock.Right);

        // Minimize straight from fullscreen (restoring returns to fullscreen — WindowState was Normal+bounds).
        var min = MakeBarButton(Loc.T("Rdp.Bar.Minimize"), ThemeBrush("GhostBg"));
        min.Click += (_, _) => { HideBar(); WindowState = WindowState.Minimized; };
        System.Windows.Controls.DockPanel.SetDock(min, System.Windows.Controls.Dock.Right);

        dock.Children.Add(hostLbl);
        dock.Children.Add(_barSwitch);
        dock.Children.Add(disc);
        dock.Children.Add(exit);
        dock.Children.Add(min);

        _bar = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            Topmost = true, ShowInTaskbar = false, Height = 44, Content = dock, Owner = this, Background = bg,
        };
    }

    private static System.Windows.Media.Brush ThemeBrush(string key)
        => Application.Current?.Resources[key] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    private static System.Windows.Controls.Button MakeBarButton(string text, System.Windows.Media.Brush brush)
    {
        return new System.Windows.Controls.Button
        {
            Content = text, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(14, 4, 14, 4),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
            Background = brush, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold,
        };
    }

    // ---- switch to another live session (RDP or protocol), incl. from fullscreen ----

    /// <summary>Short label for THIS session shown in other windows' switchers (name → host → "RDP").</summary>
    private string SessionLabel()
    {
        var n = (_directLabel ?? "").Trim();
        if (n.Length > 0) return n;
        var h = HostBox.Text.Trim();
        return h.Length > 0 ? h : "RDP";
    }

    private void OnSessionsChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        RefreshSwitch();
        if (_bar is { IsVisible: true }) RefreshBarSwitch();
    }));

    /// <summary>Rebuild the windowed top-bar switch buttons — one per OTHER live session.</summary>
    private void RefreshSwitch()
    {
        if (SwitchPanel == null) return;
        SwitchPanel.Children.Clear();
        foreach (var e in SessionRegistry.Others(this))
        {
            var target = e.Window;
            var b = new System.Windows.Controls.Button
            {
                Content = SwitchText(e), Style = (Style)FindResource("Ghost"),
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                ToolTip = Loc.F("Rdp.SwitchTip", e.Label()),
            };
            b.Click += (_, _) => SessionRegistry.SwitchTo(this, target);
            SwitchPanel.Children.Add(b);
        }
    }

    /// <summary>Rebuild the fullscreen hover-bar switch buttons.</summary>
    private void RefreshBarSwitch()
    {
        if (_barSwitch == null) return;
        _barSwitch.Children.Clear();
        foreach (var e in SessionRegistry.Others(this))
        {
            var target = e.Window;
            var b = MakeBarButton(SwitchText(e), ThemeBrush("GhostBg"));
            b.ToolTip = Loc.F("Rdp.SwitchTip", e.Label());
            b.Click += (_, _) => { HideBar(); SessionRegistry.SwitchTo(this, target); };
            _barSwitch.Children.Add(b);
        }
    }

    private static string SwitchText(SessionRegistry.Entry e)
    {
        var s = e.Label();
        if (s.Length > 22) s = s.Substring(0, 21) + "…";
        return (e.Kind == "RDP" ? "🖥 " : "◆ ") + s;
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
            _watchdog.Stop();       // it came up — cancel the reveal fallback
            RetryBtn.Visibility = Visibility.Collapsed;
            // NOTE: _directMode stays true for the window's life, so a LATER drop reveals a Retry button
            // (not this window's connect form) — everything was entered on the hub.
            Status(Loc.T("Rdp.Status.Connected"));
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
            ScheduleSessionUp();   // mark session up after a settle, then fit to the window
        }
        else
        {
            _sessionUp = false;
            // A session that came up then dropped: offer a retry (hands-off) or the form (form-driven).
            if (!_fullscreen) RevealForm(Loc.T("Rdp.Status.DisconnectedReconnect"));
            else Status(Loc.T("Rdp.Status.DisconnectedReconnect"));
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
        // Give the control focus so its keyboard hook bites (Win/Alt+Tab reach the remote).
        try { _panel.Focus(); _rdp.Focus(); } catch { }
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
