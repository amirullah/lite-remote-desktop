using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteDesktop.Client.Rendering;
using RemoteDesktop.Client.Services;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Views;

public partial class MainWindow : Window
{
    private ClientConfig _config = ClientConfig.Load();
    private readonly PinStore _pins = new(AppPaths.PinStore);

    private RemoteConnection? _connection;
    private VpnService? _vpn;
    private FrameSurface? _surface;
    private RemoteInputController? _input;
    private ClipboardBridge? _clipboard;

    private SessionSettings _settings = new();
    private bool _fullscreen;
    private WindowState _preFullscreenState;

    // Index 0 is "Automatic"; anything else is a fixed frame-rate choice.
    private static readonly int[] FpsChoices = { 0, 15, 24, 30, 45, 60, 75, 90, 120, 144 };
    // Index 0 is "Default" (native). Others are concrete stream-size targets; the host fits the
    // remote monitor into the box preserving aspect ratio, and never upscales.
    private static readonly (int W, int H)[] ResChoices =
    {
        (0, 0),          // Default (native)
        (3840, 2160),
        (2560, 1600),
        (2560, 1440),
        (1920, 1080),
        (1680, 1050),
        (1366, 768),
        (1280, 1024),
        (1280, 800),
        (1280, 720),
        (1024, 768),
    };
    private IReadOnlyList<DisplayInfo>? _displays;

    public MainWindow()
    {
        InitializeComponent();
        RelayBox.Text = _config.RelayAddress;
        if (!string.IsNullOrEmpty(_config.GoogleClientId)) GoogleClientIdBox.Text = _config.GoogleClientId;
        // Pre-fill the address from the last successful connection so reconnecting is one click.
        if (_config.Recent.Count > 0 && !string.IsNullOrWhiteSpace(_config.Recent[0].Host))
        {
            HostBox.Text = _config.Recent[0].Host;
            PortBox.Text = _config.Recent[0].Port.ToString();
        }
        PopulateSaved();
        RefreshRecent();
        Loaded += (_, _) => _clipboard = new ClipboardBridge(this);
        Closing += (_, _) => Cleanup();
    }

    private bool _suppressSaved;

    /// <summary>Fill the saved-sessions dropdown from stored connections so the user never re-types.</summary>
    private void PopulateSaved()
    {
        _suppressSaved = true;
        SavedBox.Items.Clear();
        // The old saved-sessions combo is superseded by the Recent & Saved column on the right.
        SavedRow.Visibility = Visibility.Collapsed;
        _suppressSaved = false;
    }

    private void Saved_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSaved) return;
        int i = SavedBox.SelectedIndex;
        if (i < 0 || i >= _config.Recent.Count) return;
        HostBox.Text = _config.Recent[i].Host;
        PortBox.Text = _config.Recent[i].Port.ToString();
    }

    // ---------------- Recent / saved sessions (right column) ----------------
    /// <summary>Fill the right-column list from the unified store (pinned first, then most-recent).</summary>
    private void RefreshRecent()
    {
        try
        {
            _config = ClientConfig.Load();   // reload so sessions saved by the RDP window show up too
            RecentList.ItemsSource = _config.Ordered.ToList();
            RecentEmpty.Visibility = _config.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void RecentCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedSession s) ConnectSaved(s);
    }

    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedSession s) { _config.DeleteSession(s.Id); RefreshRecent(); }
    }

    /// <summary>One-click reconnect: prefill the right fields for the session's type and connect.</summary>
    private void ConnectSaved(SavedSession s)
    {
        _config.TouchSession(s.Id);
        switch (s.Kind)
        {
            case SessionKind.Rdp:
                OpenRdp(s);
                break;
            case SessionKind.LiteRemoteId:
                ModeIdRadio.IsChecked = true;
                IdBox.Text = s.RelayId;
                Connect_Click(this, new RoutedEventArgs());
                break;
            default: // LiteRemoteIp
                ModeAddrRadio.IsChecked = true;
                HostBox.Text = s.Host;
                PortBox.Text = s.Port.ToString();
                Connect_Click(this, new RoutedEventArgs());
                break;
        }
        RefreshRecent();
    }

    /// <summary>Open the embedded RDP window (single-window: hide this while it is up), optionally from a saved session.</summary>
    private void OpenRdp(SavedSession? s = null)
    {
        try
        {
            string host = s != null
                ? (s.Port is 0 or 3389 ? s.Host : $"{s.Host}:{s.Port}")
                : HostBox.Text.Trim();
            var win = new RdpWindow(host);
            if (s != null) win.ApplySaved(s, _config);
            win.Closed += (_, _) => { Show(); Activate(); RefreshRecent(); };
            Hide();
            win.Show();
        }
        catch (Exception ex)
        {
            Services.Diag.Log("OpenRdp FAILED: " + ex);
            Show();
            SetStatus($"Gagal membuka RDP tertanam: {ex.Message}");
        }
    }

    /// <summary>
    /// Open a real Windows RDP session embedded inside LiteRemote (the mstscax.dll ActiveX control),
    /// not by launching the external mstsc app. RDP authenticates with the Windows account
    /// (user + password) and can drive the login/lock screen — the one thing LiteRemote's own path
    /// cannot. Requires "Remote Desktop" enabled on the target (TCP 3389).
    /// </summary>
    private void Rdp_Click(object sender, RoutedEventArgs e) => OpenRdp(null);

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        int i = SavedBox.SelectedIndex;
        if (i < 0 || i >= _config.Recent.Count) return;
        _config.Recent.RemoveAt(i);
        _config.Save();
        PopulateSaved();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IdModePanel is null || AddrModePanel is null) return;
        bool idMode = ModeIdRadio.IsChecked == true;
        IdModePanel.Visibility = idMode ? Visibility.Visible : Visibility.Collapsed;
        AddrModePanel.Visibility = idMode ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- connect ----------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            _settings = BuildSettings();
            bool idMode = ModeIdRadio.IsChecked == true;

            Credential? credential = await BuildCredentialAsync();
            if (credential is null) { SetStatus("Choose a valid login method."); return; }

            _connection = new RemoteConnection(_pins);
            WireConnection(_connection);

            bool ok;
            if (idMode)
            {
                string relay = RelayBox.Text.Trim();
                string id = Shared.Relay.RelayProtocol.NormalizeId(IdBox.Text);
                if (relay.Length == 0) { SetStatus("Set a relay server under Advanced first."); return; }
                if (id.Length != 9) { SetStatus("Enter the full 9-digit partner ID."); return; }
                _config.RelayAddress = relay;
                _config.Save();
                ok = await _connection.ConnectViaRelayAsync(relay, id, credential, _settings);
                if (ok) _config.UpsertSession(new SavedSession { Kind = SessionKind.LiteRemoteId, RelayId = id });
            }
            else
            {
                if (!int.TryParse(PortBox.Text, out int port)) port = 7443;
                string host = HostBox.Text.Trim();
                if (host.Length == 0) { SetStatus("Enter a host address."); return; }

                IPAddress? bind = null;
                if (NetworkModeBox.SelectedIndex == 1)
                {
                    bind = await StartVpnAsync(host);
                    if (bind is null) return; // StartVpnAsync reported the error
                }
                ok = await _connection.ConnectAsync(host, port, credential, _settings, bind);
                if (ok) _config.UpsertSession(new SavedSession { Kind = SessionKind.LiteRemoteIp, Host = host, Port = port });
            }

            if (ok) { RefreshRecent(); EnterSession(); }
            else await DisconnectAsync(); // tears down the failed connection + any VPN tunnel
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task<IPAddress?> StartVpnAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(VpnProfileBox.Text) || !System.IO.File.Exists(VpnProfileBox.Text))
        {
            SetStatus("Select a valid .ovpn profile first.");
            return null;
        }
        SetStatus("Bringing up VPN tunnel…");
        try
        {
            _vpn = new VpnService();
            return await _vpn.StartAsync(VpnProfileBox.Text, host);
        }
        catch (Exception ex)
        {
            SetStatus($"VPN failed: {ex.Message}");
            if (_vpn != null) { await _vpn.DisposeAsync(); _vpn = null; }
            return null;
        }
    }

    private async Task<Credential?> BuildCredentialAsync()
    {
        if (AuthPasswordRadio.IsChecked == true)
        {
            var pwd = PasswordBox.Password;
            return string.IsNullOrEmpty(pwd) ? null : new PasswordCredential(pwd);
        }

        var clientId = GoogleClientIdBox.Text.Trim();
        if (clientId.Length == 0) { SetStatus("Enter your Google OAuth client id."); return null; }
        _config.GoogleClientId = clientId;
        _config.Save();

        SetStatus("Waiting for Google sign-in in your browser…");
        var oauth = new GoogleOAuthClient(clientId);
        var idToken = await oauth.SignInAsync();
        return idToken is null ? null : new GoogleCredential(idToken);
    }

    private void WireConnection(RemoteConnection conn)
    {
        // Build the render surface up front so early VideoConfig/VideoFrame events (which can arrive
        // before Connect_Click finishes flipping to the session view) always have somewhere to land.
        _surface = new FrameSurface(Dispatcher);
        _surface.SizeChanged += () => RemoteImage.Source = _surface!.Source;

        conn.ConfirmFingerprint = (endpoint, fingerprint, changed) => Dispatcher.Invoke(() =>
        {
            string body = changed
                ? $"The security certificate for {endpoint} has CHANGED since you last connected.\n\n" +
                  $"This is normal if the host was reinstalled, or if this address now belongs to a " +
                  $"different computer (e.g. after a router/DHCP change). It could, rarely, mean someone " +
                  $"is intercepting the connection.\n\n" +
                  $"New fingerprint (compare with the host tray → Show status):\n\n{fingerprint}\n\n" +
                  $"Trust this certificate and continue?"
                : $"First time connecting to {endpoint}.\n\n" +
                  $"Verify this certificate fingerprint matches the one shown on the host " +
                  $"(host tray → Show status):\n\n{fingerprint}\n\nTrust this host?";
            return MessageBox.Show(this, body,
                changed ? "Host certificate changed" : "Verify host identity",
                MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;
        });

        conn.StateChanged += (state, msg) => Dispatcher.Invoke(() =>
        {
            // Timeouts/refusals are almost always environmental — give the user actionable hints.
            if (state == ConnectionState.Failed &&
                (msg.Contains("did not properly respond") || msg.Contains("refused") || msg.Contains("timed out")))
            {
                msg += "  Checklist: LiteRemote Host is running on the remote PC, the address/ID is " +
                       "correct, and TCP port 7443 is allowed through its firewall.";
            }
            SetStatus(msg);
            if (state is ConnectionState.Disconnected or ConnectionState.Failed)
                LeaveSession();
        });

        conn.VideoConfigured += cfg => _surface?.Configure(cfg.Width, cfg.Height, cfg.Codec);
        conn.FrameReceived += (_, _, tiles, _) => _surface?.ApplyFrame(tiles);
        conn.DisplaysReceived += displays => Dispatcher.Invoke(() => PopulateDisplays(displays));
        conn.StatReceived += stat => Dispatcher.Invoke(() =>
            StatText.Text = $"{stat.Fps} fps · {stat.MbitsPerSecond:F1} Mbit/s · cap {stat.RoundTripMs} + enc {stat.EncodeMs} ms · {stat.EncoderName}");
        conn.ClipboardReceived += data => Dispatcher.Invoke(() => _clipboard?.SetClipboard(data));
    }

    private void EnterSession()
    {
        ConnectPanel.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Visible;

        // If a frame already configured the surface before we flipped views, adopt it now.
        if (_surface?.Source != null) RemoteImage.Source = _surface.Source;

        _input = new RemoteInputController(RemoteImage, _connection!) { Enabled = true };
        _input.Attach(this);

        if (_clipboard != null)
            _clipboard.ClipboardChanged += OnLocalClipboardChanged;
    }

    private void LeaveSession()
    {
        if (SessionPanel.Visibility != Visibility.Visible) return;
        if (_clipboard != null) _clipboard.ClipboardChanged -= OnLocalClipboardChanged;
        _input?.Detach();
        _input = null;
        SessionPanel.Visibility = Visibility.Collapsed;
        ConnectPanel.Visibility = Visibility.Visible;
        RemoteImage.Source = null;
        PopulateSaved(); // reflect a just-added connection
        if (_fullscreen) Fullscreen_Click(this, new RoutedEventArgs());
    }

    private void OnLocalClipboardChanged(Shared.Protocol.ClipboardData data)
    {
        if (_settings.ClipboardSync) _connection?.SendClipboard(data);
    }

    // ---------------- settings plumbing ----------------

    private SessionSettings BuildSettings()
    {
        int fpsSel = Math.Clamp(SessionFpsBox.SelectedIndex, 0, FpsChoices.Length - 1);
        int fps = FpsChoices[fpsSel];
        int displayIndex = Math.Max(0, DisplayBox.SelectedIndex);
        var (resMode, scaledW, scaledH) = ResolveResolution(SessionResBox.SelectedIndex, displayIndex);

        return _settings = new SessionSettings
        {
            FrameRateMode = fps == 0 ? FrameRateMode.Auto : FrameRateMode.Fixed,
            TargetFps = fps == 0 ? 60 : fps,
            MaxFps = fps == 0 ? 144 : fps,
            ResolutionMode = resMode,
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            DisplayIndex = displayIndex,
            Quality = QualityChoices[Math.Clamp(SessionQualityBox.SelectedIndex, 0, QualityChoices.Length - 1)],
            ClipboardSync = ClipboardCheck.IsChecked == true,
            BlankHostScreen = BlankScreenCheck.IsChecked == true,
            LockHostInput = LockInputCheck.IsChecked == true,
            // Otomatis(0) & H.264(1) request H.264 — the host uses its hardware encoder when present
            // and transparently falls back to JPEG (e.g. in a VM). JPEG(2) forces the universal path.
            PreferredCodec = SessionCodecBox.SelectedIndex == 2 ? VideoCodec.JpegTiles : VideoCodec.H264,
        };
    }

    /// <summary>Turns a preset choice into the stream-size box sent to the host.</summary>
    private (ResolutionMode Mode, int W, int H) ResolveResolution(int resIndex, int displayIndex)
    {
        var (w, h) = ResChoices[Math.Clamp(resIndex, 0, ResChoices.Length - 1)];
        if (w == 0) return (ResolutionMode.Native, 0, 0); // "Default"

        // Never upscale: a preset larger than the host monitor would stretch a small screen up to a
        // bigger frame and look torn/blurry. If the chosen size meets or exceeds native, just stream
        // native (sharpest). Scaling only ever shrinks.
        if (displayIndex >= 0 && displayIndex < _displays.Count)
        {
            var d = _displays[displayIndex];
            if (d.Width > 0 && d.Height > 0 && w >= d.Width && h >= d.Height)
                return (ResolutionMode.Native, 0, 0);
        }
        return (ResolutionMode.Scaled, w, h);
    }

    private async void PushSettings()
    {
        if (_connection is null || SessionPanel.Visibility != Visibility.Visible) return;
        await _connection.SendSettingsAsync(_settings);
    }

    private void PopulateDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        _displays = displays;
        DisplayBox.SelectionChanged -= Display_Changed;
        DisplayBox.Items.Clear();
        foreach (var d in displays)
            DisplayBox.Items.Add($"#{d.Index} {d.Width}×{d.Height}{(d.IsPrimary ? " (primary)" : "")}");
        DisplayBox.SelectedIndex = Math.Min(_settings.DisplayIndex, displays.Count - 1);
        DisplayBox.SelectionChanged += Display_Changed;

        // A single-monitor host has nothing to pick — hiding the picker (and its resolution number)
        // removes the confusion with the separate "Res" control. It reappears with 2+ monitors.
        var vis = displays.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorLabel.Visibility = vis;
        DisplayBox.Visibility = vis;
    }

    // ---------------- UI event handlers ----------------

    private void AuthMethod_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordArea is null || GoogleArea is null) return;
        bool google = AuthGoogleRadio.IsChecked == true;
        PasswordArea.Visibility = google ? Visibility.Collapsed : Visibility.Visible;
        GoogleArea.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
        if (google && !string.IsNullOrEmpty(_config.GoogleClientId))
            GoogleClientIdBox.Text = _config.GoogleClientId;
    }

    private void NetworkMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VpnArea is null) return;
        VpnArea.Visibility = NetworkModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseVpn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "OpenVPN profile (*.ovpn)|*.ovpn|All files|*.*" };
        if (dlg.ShowDialog() == true) VpnProfileBox.Text = dlg.FileName;
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityLabel != null) QualityLabel.Text = ((int)e.NewValue).ToString();
    }

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayBox.SelectedIndex < 0) return;
        var (resMode, scaledW, scaledH) = ResolveResolution(SessionResBox.SelectedIndex, DisplayBox.SelectedIndex);
        _settings = _settings with
        {
            DisplayIndex = DisplayBox.SelectedIndex,
            ResolutionMode = resMode,
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
        };
        _connection?.RequestKeyFrame();
        PushSettings();
    }

    private void SessionFps_Changed(object sender, SelectionChangedEventArgs e) => ApplyPerformanceSelection();

    private void SessionRes_Changed(object sender, SelectionChangedEventArgs e) => ApplyPerformanceSelection();

    private void SessionCodec_Changed(object sender, SelectionChangedEventArgs e) => ApplyPerformanceSelection();

    private void SessionQuality_Changed(object sender, SelectionChangedEventArgs e) => ApplyPerformanceSelection();

    // Otomatis, Maksimum, Tinggi, Normal, Hemat data → encoder quality hint (1..100). The host's
    // adaptive controller still throttles under load, so a high pick never floods a slow link.
    private static readonly int[] QualityChoices = { 80, 95, 87, 72, 55 };

    private void ApplyPerformanceSelection()
    {
        if (_connection is null) return; // initial XAML selection during startup
        BuildSettings();
        _connection.RequestKeyFrame();
        PushSettings();
    }

    private void Cad_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl down, Alt down, Del down/up, Alt up, Ctrl up.
        const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_DELETE = 0x2E;
        _connection?.SendKey(new KeyEventData(VK_CONTROL, 0, true, false));
        _connection?.SendKey(new KeyEventData(VK_MENU, 0, true, false));
        _connection?.SendKey(new KeyEventData(VK_DELETE, 0, true, true));
        _connection?.SendKey(new KeyEventData(VK_DELETE, 0, false, true));
        _connection?.SendKey(new KeyEventData(VK_MENU, 0, false, false));
        _connection?.SendKey(new KeyEventData(VK_CONTROL, 0, false, false));
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _preFullscreenState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            // A borderless window that is *already* maximized only covers the work area (taskbar stays
            // visible). Dropping to Normal first forces WPF to recompute the maximized bounds against
            // the whole monitor, so it now covers the screen edge-to-edge including the taskbar.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            WindowState = _preFullscreenState;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
        LeaveSession();
    }

    private async Task DisconnectAsync()
    {
        if (_connection != null) { await _connection.DisposeAsync(); _connection = null; }
        if (_vpn != null) { await _vpn.DisposeAsync(); _vpn = null; }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void Cleanup()
    {
        _clipboard?.Dispose();
        await DisconnectAsync();
        _config.Save();
    }
}
