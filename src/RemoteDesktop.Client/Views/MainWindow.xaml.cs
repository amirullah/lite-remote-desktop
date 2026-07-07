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
    private readonly ClientConfig _config = ClientConfig.Load();
    private readonly PinStore _pins = new(AppPaths.PinStore);

    private RemoteConnection? _connection;
    private VpnService? _vpn;
    private FrameSurface? _surface;
    private RemoteInputController? _input;
    private ClipboardBridge? _clipboard;

    private SessionSettings _settings = new();
    private bool _fullscreen;
    private WindowState _preFullscreenState;

    public MainWindow()
    {
        InitializeComponent();
        RelayBox.Text = _config.RelayAddress;
        if (!string.IsNullOrEmpty(_config.GoogleClientId)) GoogleClientIdBox.Text = _config.GoogleClientId;
        Loaded += (_, _) => _clipboard = new ClipboardBridge(this);
        Closing += (_, _) => Cleanup();
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
                if (ok) _config.Remember(new SavedConnection { Host = host, Port = port, Label = host });
            }

            if (ok) EnterSession();
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
        if (AuthMethodBox.SelectedIndex == 0)
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

        conn.ConfirmFingerprint = (endpoint, fingerprint) => Dispatcher.Invoke(() =>
            MessageBox.Show(this,
                $"First time connecting to {endpoint}.\n\n" +
                $"Verify this certificate fingerprint matches the one shown on the host " +
                $"(host tray → Show status):\n\n{fingerprint}\n\nTrust this host?",
                "Verify host identity", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        conn.StateChanged += (state, msg) => Dispatcher.Invoke(() =>
        {
            SetStatus(msg);
            if (state is ConnectionState.Disconnected or ConnectionState.Failed)
                LeaveSession();
        });

        conn.VideoConfigured += cfg => _surface?.Configure(cfg.Width, cfg.Height);
        conn.FrameReceived += (_, _, tiles, _) => _surface?.ApplyFrame(tiles);
        conn.DisplaysReceived += displays => Dispatcher.Invoke(() => PopulateDisplays(displays));
        conn.StatReceived += stat => Dispatcher.Invoke(() =>
            StatText.Text = $"{stat.Fps} fps · {stat.MbitsPerSecond:F1} Mbit/s · enc {stat.EncodeMs} ms · {stat.EncoderName}");
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
        if (_fullscreen) Fullscreen_Click(this, new RoutedEventArgs());
    }

    private void OnLocalClipboardChanged(Shared.Protocol.ClipboardData data)
    {
        if (_settings.ClipboardSync) _connection?.SendClipboard(data);
    }

    // ---------------- settings plumbing ----------------

    private SessionSettings BuildSettings() => _settings = new SessionSettings
    {
        FrameRateMode = FpsModeBox.SelectedIndex == 1 ? FrameRateMode.Fixed : FrameRateMode.Auto,
        TargetFps = (int)FpsSlider.Value,
        MaxFps = (int)FpsSlider.Value,
        ResolutionMode = (ResolutionMode)ResolutionBox.SelectedIndex,
        DisplayIndex = Math.Max(0, DisplayBox.SelectedIndex),
        Quality = (int)QualitySlider.Value,
        ClipboardSync = ClipboardCheck.IsChecked == true,
        BlankHostScreen = BlankScreenCheck.IsChecked == true,
        LockHostInput = LockInputCheck.IsChecked == true,
        PreferredCodec = VideoCodec.JpegTiles,
    };

    private async void PushSettings()
    {
        if (_connection is null || SessionPanel.Visibility != Visibility.Visible) return;
        await _connection.SendSettingsAsync(_settings);
    }

    private void PopulateDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        DisplayBox.SelectionChanged -= Display_Changed;
        DisplayBox.Items.Clear();
        foreach (var d in displays)
            DisplayBox.Items.Add($"#{d.Index} {d.Width}×{d.Height}{(d.IsPrimary ? " (primary)" : "")}");
        DisplayBox.SelectedIndex = Math.Min(_settings.DisplayIndex, displays.Count - 1);
        DisplayBox.SelectionChanged += Display_Changed;
    }

    // ---------------- UI event handlers ----------------

    private void AuthMethod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordArea is null) return;
        bool google = AuthMethodBox.SelectedIndex == 1;
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

    private void FpsMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FpsValueLabel is null) return;
        bool fixedMode = FpsModeBox.SelectedIndex == 1;
        FpsValueLabel.Text = fixedMode ? "Target" : "Max";
    }

    private void Fps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValueLabel is null) return;
        FpsValueLabel.Text = $"{(FpsModeBox.SelectedIndex == 1 ? "Target" : "Max")} {(int)e.NewValue}";
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityLabel != null) QualityLabel.Text = ((int)e.NewValue).ToString();
    }

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayBox.SelectedIndex < 0) return;
        _settings = _settings with { DisplayIndex = DisplayBox.SelectedIndex };
        _connection?.RequestKeyFrame();
        PushSettings();
    }

    private void SessionFpsMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        _settings = _settings with
        {
            FrameRateMode = SessionFpsModeBox.SelectedIndex == 1 ? FrameRateMode.Fixed : FrameRateMode.Auto,
        };
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
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
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
