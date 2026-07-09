using System.Net;
using System.Windows;
using System.Windows.Controls;
using RemoteDesktop.Client.Rendering;
using RemoteDesktop.Client.Services;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// Everything needed to bring up one LiteRemote-protocol session. Built by <see cref="MainWindow"/>
/// (which owns the connect form) and handed to a fresh <see cref="SessionWindow"/> so each session
/// lives in its own window — several can run at once.
/// </summary>
public sealed class SessionRequest
{
    public bool IdMode;
    public string Host = "";
    public int Port = 7443;
    public string Relay = "";
    public string Id = "";
    public required Credential Credential;
    public SessionSettings Settings = new();
    public string? VpnProfilePath;      // null = direct (no VPN)
    public SavedSession Descriptor = new();  // saved to Recent on a successful connect
}

/// <summary>
/// A single live LiteRemote-protocol session in its own window (surface + toolbar + input). Mirrors the
/// embedded-RDP window pattern so multiple sessions can run concurrently; <see cref="MainWindow"/> stays
/// a pure connect hub.
/// </summary>
public partial class SessionWindow : Window
{
    private readonly SessionRequest _request;
    private readonly PinStore _pins;
    private ClientConfig _config = ClientConfig.Load();

    private RemoteConnection? _connection;
    private VpnService? _vpn;
    private FrameSurface? _surface;
    private RemoteInputController? _input;
    private ClipboardBridge? _clipboard;

    private SessionSettings _settings;
    private bool _inSession;
    private string? _lastError;
    private bool _fullscreen;
    private WindowState _preFullscreenState;

    private static readonly int[] FpsChoices = { 0, 15, 24, 30, 45, 60, 75, 90, 120, 144 };
    private static readonly (int W, int H)[] ResChoices =
    {
        (0, 0), (3840, 2160), (2560, 1600), (2560, 1440), (1920, 1080),
        (1680, 1050), (1366, 768), (1280, 1024), (1280, 800), (1280, 720), (1024, 768),
    };
    // Otomatis, Maksimum, Tinggi, Normal, Hemat data → encoder quality hint (1..100).
    private static readonly int[] QualityChoices = { 80, 95, 87, 72, 55 };
    private IReadOnlyList<DisplayInfo>? _displays;

    public SessionWindow(SessionRequest request, PinStore pins)
    {
        InitializeComponent();
        _request = request;
        _pins = pins;
        _settings = request.Settings;
        Title = "LiteRemote — " + request.Descriptor.DisplayName;
        Loaded += OnLoaded;
        Closing += (_, _) => Cleanup();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clipboard = new ClipboardBridge(this);
        await ConnectAndRunAsync();
    }

    // ---------------- connection lifecycle ----------------

    private async Task ConnectAndRunAsync()
    {
        try
        {
            IPAddress? bind = null;
            if (_request.VpnProfilePath != null)
            {
                SetOverlay("Menghubungkan…", "Menyalakan terowongan VPN…");
                _vpn = new VpnService();
                bind = await _vpn.StartAsync(_request.VpnProfilePath, _request.Host);
                if (bind is null)
                {
                    ShowDisconnected("Gagal menyalakan VPN — periksa profil .ovpn dan koneksi.");
                    await DisconnectAsync();
                    return;
                }
            }

            SetOverlay("Menghubungkan…",
                _request.IdMode ? $"ID {_request.Id} lewat relay…" : $"{_request.Host}:{_request.Port}…");

            _connection = new RemoteConnection(_pins);
            WireConnection(_connection);

            bool ok = _request.IdMode
                ? await _connection.ConnectViaRelayAsync(_request.Relay, _request.Id, _request.Credential, _settings)
                : await _connection.ConnectAsync(_request.Host, _request.Port, _request.Credential, _settings, bind);

            if (ok)
            {
                try { _config.UpsertSession(_request.Descriptor); } catch { }
                EnterSession();
            }
            else
            {
                ShowDisconnected(_lastError ?? "Gagal terhubung.");
                await DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            ShowDisconnected($"Error: {ex.Message}");
            await DisconnectAsync();
        }
    }

    private void WireConnection(RemoteConnection conn)
    {
        _surface = new FrameSurface(Dispatcher);
        _surface.SizeChanged += () =>
        {
            RemoteImage.Source = _surface!.Source;
            if (_inSession) HideOverlay();   // first real frame — drop the "connecting" cover
        };

        conn.ConfirmFingerprint = (endpoint, fingerprint, changed) => Dispatcher.Invoke(() =>
        {
            string body = changed
                ? $"Sertifikat keamanan untuk {endpoint} BERUBAH sejak terakhir Anda terhubung.\n\n" +
                  $"Ini normal bila host diinstal ulang, atau alamat ini kini milik komputer lain " +
                  $"(mis. setelah ganti router/DHCP). Jarang, bisa berarti ada yang menyadap koneksi.\n\n" +
                  $"Sidik jari baru (bandingkan dengan host tray → Show status):\n\n{fingerprint}\n\n" +
                  $"Percayai sertifikat ini dan lanjutkan?"
                : $"Pertama kali terhubung ke {endpoint}.\n\n" +
                  $"Pastikan sidik jari sertifikat ini sama dengan yang ditampilkan di host " +
                  $"(host tray → Show status):\n\n{fingerprint}\n\nPercayai host ini?";
            return MessageBox.Show(this, body,
                changed ? "Sertifikat host berubah" : "Verifikasi identitas host",
                MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;
        });

        conn.StateChanged += (state, msg) => Dispatcher.Invoke(() =>
        {
            if (state == ConnectionState.Failed &&
                (msg.Contains("did not properly respond") || msg.Contains("refused") || msg.Contains("timed out")))
            {
                msg += "  Periksa: LiteRemote Host berjalan di PC tujuan, alamat/ID benar, dan " +
                       "port 7443 diizinkan lewat firewall-nya.";
            }
            _lastError = msg;
            if (state is ConnectionState.Disconnected or ConnectionState.Failed)
            {
                if (_inSession) ShowDisconnected(msg);
                else SetOverlay("Menghubungkan…", msg);
            }
            else if (!_inSession)
            {
                SetOverlay("Menghubungkan…", msg);
            }
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
        _inSession = true;
        if (_surface?.Source != null) { RemoteImage.Source = _surface.Source; HideOverlay(); }

        _input = new RemoteInputController(RemoteImage, _connection!) { Enabled = true };
        _input.Attach(this);   // Windows shortcuts (Win/Alt+Tab/Ctrl+Esc) route to the remote while focused

        if (_clipboard != null)
            _clipboard.ClipboardChanged += OnLocalClipboardChanged;
    }

    private void OnLocalClipboardChanged(Shared.Protocol.ClipboardData data)
    {
        if (_settings.ClipboardSync) _connection?.SendClipboard(data);
    }

    // ---------------- overlay ----------------

    private void SetOverlay(string title, string status)
    {
        OverlayTitle.Text = title;
        OverlayStatus.Text = status;
        OverlayClose.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    private void ShowDisconnected(string msg)
    {
        _inSession = false;
        _input?.Detach();
        _input = null;
        RemoteImage.Source = null;
        OverlayTitle.Text = "Terputus";
        OverlayStatus.Text = msg;
        OverlayClose.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        if (_fullscreen) Fullscreen_Click(this, new RoutedEventArgs());
    }

    private void OverlayClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- settings plumbing ----------------

    private SessionSettings BuildSettings()
    {
        int fps = FpsChoices[Math.Clamp(SessionFpsBox.SelectedIndex, 0, FpsChoices.Length - 1)];
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
            // These three come from the connect-form advanced options; the toolbar can't change them,
            // so preserve whatever the session was started with.
            ClipboardSync = _settings.ClipboardSync,
            BlankHostScreen = _settings.BlankHostScreen,
            LockHostInput = _settings.LockHostInput,
            PreferredCodec = SessionCodecBox.SelectedIndex == 2 ? VideoCodec.JpegTiles : VideoCodec.H264,
        };
    }

    private (ResolutionMode Mode, int W, int H) ResolveResolution(int resIndex, int displayIndex)
    {
        var (w, h) = ResChoices[Math.Clamp(resIndex, 0, ResChoices.Length - 1)];
        if (w == 0) return (ResolutionMode.Native, 0, 0);

        if (_displays != null && displayIndex >= 0 && displayIndex < _displays.Count)
        {
            var d = _displays[displayIndex];
            if (d.Width > 0 && d.Height > 0 && w >= d.Width && h >= d.Height)
                return (ResolutionMode.Native, 0, 0);
        }
        return (ResolutionMode.Scaled, w, h);
    }

    private async void PushSettings()
    {
        if (_connection is null || !_inSession) return;
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

        var vis = displays.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorLabel.Visibility = vis;
        DisplayBox.Visibility = vis;
    }

    private void ApplyPerformanceSelection()
    {
        if (_connection is null || !_inSession) return;
        BuildSettings();
        _connection.RequestKeyFrame();
        PushSettings();
    }

    // ---------------- toolbar handlers ----------------

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

    private void Cad_Click(object sender, RoutedEventArgs e)
    {
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
        Close();
    }

    private async Task DisconnectAsync()
    {
        if (_connection != null) { await _connection.DisposeAsync(); _connection = null; }
        if (_vpn != null) { await _vpn.DisposeAsync(); _vpn = null; }
    }

    private async void Cleanup()
    {
        if (_clipboard != null) _clipboard.ClipboardChanged -= OnLocalClipboardChanged;
        _input?.Detach();
        _input = null;
        _clipboard?.Dispose();
        await DisconnectAsync();
    }
}
