using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Relay;

namespace RemoteDesktop.Maui;

public partial class MainPage : ContentPage
{
    // Non-secret fields live in Preferences; the password only in SecureStorage (Android Keystore).
    private const string KMode = "mode", KHost = "host", KPort = "port", KId = "id", KRelay = "relay", KRemember = "remember";
    private const string SecretKey = "host-password";

    private bool _idMode;

    public MainPage()
    {
        InitializeComponent();
        HostEntry.Text = Preferences.Get(KHost, "");
        PortEntry.Text = Preferences.Get(KPort, "7443");
        IdEntry.Text = Preferences.Get(KId, "");
        RelayEntry.Text = Preferences.Get(KRelay, "");
        RememberCheck.IsChecked = Preferences.Get(KRemember, false);
        SetMode(Preferences.Get(KMode, "addr") == "id");
        SetQuality(Preferences.Get(KQuality, "balanced"));
    }

    // ---- streaming profile (what the phone asks the host for) ----
    private const string KQuality = "quality";
    private string _quality = "balanced";

    private void Quality_Clicked(object? sender, EventArgs e) => SetQuality((sender as Button)?.CommandParameter?.ToString() ?? "balanced");

    private void SetQuality(string q)
    {
        _quality = q is "saver" or "sharp" ? q : "balanced";
        SegStyle(QSaverBtn, _quality == "saver");
        SegStyle(QBalancedBtn, _quality == "balanced");
        SegStyle(QSharpBtn, _quality == "sharp");
    }

    /// <summary>
    /// A phone doesn't need desktop defaults (up to 144 fps native): capping fps is the biggest win for
    /// bandwidth, decode load and battery, while native resolution keeps text crisp when zoomed.
    /// </summary>
    private SessionSettings BuildSettings() => _quality switch
    {
        "saver" => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 24, TargetFps = 24,
            ResolutionMode = ResolutionMode.Scaled, ScaledWidth = 1280, ScaledHeight = 720, Quality = 50,
        },
        "sharp" => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 60, TargetFps = 60, Quality = 80,
        },
        _ => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 30, TargetFps = 30, Quality = 65,
        },
    };

    private static void SegStyle(Button b, bool active)
    {
        b.BackgroundColor = active ? Color.FromArgb("#2F7CF6") : Colors.Transparent;
        b.TextColor = active ? Colors.White : Color.FromArgb("#8FA3C4");
    }

    // ---- in-app update ----
    private AppUpdate? _update;
    private bool _checkedUpdate, _updating;

    private async Task CheckUpdateAsync(bool manual)
    {
        try
        {
            if (manual) VersionLabel.Text = "Memeriksa pembaruan…";
            _update = await AppUpdateService.CheckAsync();
            if (_update is null)
            {
                VersionLabel.Text = $"LiteRemote v{AppUpdateService.CurrentVersion.ToString(3)}" + (manual ? " — sudah versi terbaru" : " · ketuk untuk cek pembaruan");
                return;
            }
            UpdateText.Text = $"Versi baru v{_update.Version.ToString(3)} tersedia (Anda: v{AppUpdateService.CurrentVersion.ToString(3)})";
            UpdateBanner.IsVisible = true;
        }
        catch (Exception ex)
        {
            // Offline / API hiccup: stay quiet on the automatic check.
            VersionLabel.Text = $"LiteRemote v{AppUpdateService.CurrentVersion.ToString(3)}" + (manual ? " — gagal cek: " + ex.Message : " · ketuk untuk cek pembaruan");
        }
    }

    private async void Version_Tapped(object? sender, TappedEventArgs e) => await CheckUpdateAsync(manual: true);

    private async void Update_Clicked(object? sender, EventArgs e)
    {
        if (_update is null || _updating) return;
        _updating = true; UpdateBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateText.Text = $"Mengunduh v{_update.Version.ToString(3)}… {(int)(p * 100)}%");
            var path = await AppUpdateService.DownloadAsync(_update, progress, CancellationToken.None);
            if (AppUpdateService.Install(path))
                UpdateText.Text = "Ikuti layar pemasangan Android untuk menyelesaikan pembaruan.";
            else
                UpdateText.Text = "Izinkan LiteRemote \"memasang aplikasi tidak dikenal\", lalu kembali dan ketuk Perbarui lagi.";
        }
        catch (Exception ex)
        {
            UpdateText.Text = "Pembaruan gagal: " + ex.Message;
        }
        finally { _updating = false; UpdateBtn.IsEnabled = true; }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_checkedUpdate)
        {
            _checkedUpdate = true;
            VersionLabel.Text = $"LiteRemote v{AppUpdateService.CurrentVersion.ToString(3)}";
            _ = CheckUpdateAsync(manual: false);
        }
        if (RememberCheck.IsChecked && string.IsNullOrEmpty(PasswordEntry.Text))
        {
            try { PasswordEntry.Text = await SecureStorage.Default.GetAsync(SecretKey) ?? ""; } catch { }
        }
    }

    private void ModeAddr_Clicked(object? sender, EventArgs e) => SetMode(false);
    private void ModeId_Clicked(object? sender, EventArgs e) => SetMode(true);

    private void SetMode(bool id)
    {
        _idMode = id;
        AddrPanel.IsVisible = !id;
        IdPanel.IsVisible = id;
        SegStyle(ModeAddrBtn, !id);
        SegStyle(ModeIdBtn, id);
        StatusLabel.Text = "";
    }

    private void Eye_Clicked(object? sender, EventArgs e) => PasswordEntry.IsPassword = !PasswordEntry.IsPassword;

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? "";
        if (password.Length == 0) { StatusLabel.Text = "Isi password akses host."; return; }

        Func<CancellationToken, Task<ConnectOutcome>> connect;
        string title;

        if (_idMode)
        {
            var id = RelayProtocol.NormalizeId(IdEntry.Text ?? "");
            if (id.Length != 9) { StatusLabel.Text = "ID harus 9 digit."; return; }
            var (relayHost, relayPort) = SplitHostPort(RelayEntry.Text, RelayProtocol.DefaultPort);
            if (relayHost.Length == 0) { StatusLabel.Text = "Isi alamat server relay."; return; }
            connect = ct => ViewerConnection.ConnectViaRelayAsync(relayHost, relayPort, id, password, ct);
            title = RelayProtocol.FormatId(id);
        }
        else
        {
            var host = (HostEntry.Text ?? "").Trim();
            if (host.Length == 0) { StatusLabel.Text = "Isi alamat host."; return; }
            if (!int.TryParse(PortEntry.Text, out var port) || port is < 1 or > 65535) port = 7443;
            PortEntry.Text = port.ToString();
            connect = ct => ViewerConnection.ConnectAsync(host, port, password, ct);
            title = $"{host}:{port}";
        }

        Remember(password);
        Preferences.Set(KQuality, _quality);
        StatusLabel.Text = "";
        await Navigation.PushAsync(new SessionPage(connect, title, BuildSettings()));
    }

    private void Remember(string password)
    {
        Preferences.Set(KMode, _idMode ? "id" : "addr");
        Preferences.Set(KHost, (HostEntry.Text ?? "").Trim());
        Preferences.Set(KPort, (PortEntry.Text ?? "7443").Trim());
        Preferences.Set(KId, (IdEntry.Text ?? "").Trim());
        Preferences.Set(KRelay, (RelayEntry.Text ?? "").Trim());
        Preferences.Set(KRemember, RememberCheck.IsChecked);
        try
        {
            if (RememberCheck.IsChecked) _ = SecureStorage.Default.SetAsync(SecretKey, password);
            else SecureStorage.Default.Remove(SecretKey);
        }
        catch { }   // SecureStorage can fail on devices with a broken keystore — just don't remember
    }

    private static (string host, int port) SplitHostPort(string? value, int defaultPort)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return ("", defaultPort);
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            return (text[..colon], p);
        return (text, defaultPort);
    }
}
