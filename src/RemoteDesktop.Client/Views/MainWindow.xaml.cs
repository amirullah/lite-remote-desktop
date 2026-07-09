using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteDesktop.Client.Rendering;
using RemoteDesktop.Client.Services;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// The connect hub: pick a connection (LiteRemote by address/ID, or Windows RDP), optionally through a
/// VPN, and launch it into its OWN window (<see cref="SessionWindow"/> for the LiteRemote protocol,
/// <see cref="RdpWindow"/> for RDP). Several sessions can run at once; this window stays a launcher and
/// keeps the Recent &amp; Saved column for one-click reconnect.
/// </summary>
public partial class MainWindow : Window
{
    private ClientConfig _config = ClientConfig.Load();
    private readonly PinStore _pins = new(AppPaths.PinStore);

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
        Closing += (_, _) => Cleanup();
    }

    private bool _suppressSaved;

    /// <summary>The old saved-sessions combo is superseded by the Recent &amp; Saved column on the right.</summary>
    private void PopulateSaved()
    {
        _suppressSaved = true;
        SavedBox.Items.Clear();
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
            _config = ClientConfig.Load();   // reload so sessions saved by session/RDP windows show up too
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
                // Restore the saved VPN choice so a VPN-backed session reconnects the same way.
                var vp = _config.GetVpn(s.VpnProfileId);
                if (s.UseVpn && vp != null && System.IO.File.Exists(vp.OvpnPath))
                {
                    NetworkModeBox.SelectedIndex = 1;
                    VpnProfileBox.Text = vp.OvpnPath;
                }
                else NetworkModeBox.SelectedIndex = 0;
                Connect_Click(this, new RoutedEventArgs());
                break;
        }
        RefreshRecent();
    }

    /// <summary>Open the embedded RDP window (its own window), optionally from a saved session.</summary>
    private void OpenRdp(SavedSession? s = null)
    {
        try
        {
            string host = s != null
                ? (s.Port is 0 or 3389 ? s.Host : $"{s.Host}:{s.Port}")
                : HostBox.Text.Trim();
            var win = new RdpWindow(host);
            if (s != null) win.ApplySaved(s, _config);
            win.Closed += (_, _) => RefreshRecent();
            win.Show();
        }
        catch (Exception ex)
        {
            Services.Diag.Log("OpenRdp FAILED: " + ex);
            SetStatus($"Gagal membuka RDP tertanam: {ex.Message}");
        }
    }

    /// <summary>
    /// Open a real Windows RDP session embedded inside LiteRemote (the mstscax.dll ActiveX control),
    /// not by launching the external mstsc app. RDP authenticates with the Windows account and can
    /// drive the login/lock screen — the one thing LiteRemote's own path cannot.
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

    // ---------------- connect (launches a session window) ----------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            bool idMode = ModeIdRadio.IsChecked == true;

            Credential? credential = await BuildCredentialAsync();
            if (credential is null) { SetStatus("Pilih metode login yang valid (isi password atau client id Google)."); return; }

            var req = new SessionRequest
            {
                IdMode = idMode,
                Credential = credential,
                Settings = BuildInitialSettings(),
            };

            if (idMode)
            {
                string relay = RelayBox.Text.Trim();
                string id = Shared.Relay.RelayProtocol.NormalizeId(IdBox.Text);
                if (relay.Length == 0) { SetStatus("Set relay server di Advanced dulu."); return; }
                if (id.Length != 9) { SetStatus("Masukkan ID mitra 9 digit lengkap."); return; }
                _config.RelayAddress = relay; _config.Save();
                req.Relay = relay;
                req.Id = id;
                req.Descriptor = new SavedSession { Kind = SessionKind.LiteRemoteId, RelayId = id };
            }
            else
            {
                if (!int.TryParse(PortBox.Text, out int port)) port = 7443;
                string host = HostBox.Text.Trim();
                if (host.Length == 0) { SetStatus("Masukkan alamat host."); return; }
                req.Host = host;
                req.Port = port;

                string? vpnId = null;
                if (NetworkModeBox.SelectedIndex == 1)
                {
                    if (string.IsNullOrWhiteSpace(VpnProfileBox.Text) || !System.IO.File.Exists(VpnProfileBox.Text))
                    { SetStatus("Pilih profil .ovpn yang valid dulu."); return; }
                    req.VpnProfilePath = VpnProfileBox.Text;
                    // Remember the profile so it shows up for reuse and reconnect.
                    var vpn = _config.UpsertVpn(new VpnProfile { OvpnPath = VpnProfileBox.Text });
                    vpnId = vpn.Id;
                    _config.LastVpnProfile = VpnProfileBox.Text; _config.Save();
                }

                req.Descriptor = new SavedSession
                {
                    Kind = SessionKind.LiteRemoteIp, Host = host, Port = port,
                    UseVpn = vpnId != null, VpnProfileId = vpnId,
                };
            }

            var win = new SessionWindow(req, _pins);
            win.Closed += (_, _) => RefreshRecent();
            win.Show();
            SetStatus("Sesi dibuka di jendela baru — Anda bisa membuka koneksi lain sekaligus.");
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

    /// <summary>Initial settings for a new session; the session toolbar tunes fps/res/codec/quality live.</summary>
    private SessionSettings BuildInitialSettings() => new()
    {
        FrameRateMode = FrameRateMode.Auto,
        TargetFps = 60,
        MaxFps = 144,
        ResolutionMode = ResolutionMode.Native,
        DisplayIndex = 0,
        Quality = 80,
        ClipboardSync = ClipboardCheck.IsChecked == true,
        BlankHostScreen = BlankScreenCheck.IsChecked == true,
        LockHostInput = LockInputCheck.IsChecked == true,
        PreferredCodec = VideoCodec.H264,
    };

    private async Task<Credential?> BuildCredentialAsync()
    {
        if (AuthPasswordRadio.IsChecked == true)
        {
            var pwd = PasswordBox.Password;
            return string.IsNullOrEmpty(pwd) ? null : new PasswordCredential(pwd);
        }

        var clientId = GoogleClientIdBox.Text.Trim();
        if (clientId.Length == 0) { SetStatus("Masukkan Google OAuth client id Anda."); return null; }
        _config.GoogleClientId = clientId;
        _config.Save();

        SetStatus("Menunggu login Google di browser…");
        var oauth = new GoogleOAuthClient(clientId);
        var idToken = await oauth.SignInAsync();
        return idToken is null ? null : new GoogleCredential(idToken);
    }

    // ---------------- connect-form option handlers ----------------

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

    private void SetStatus(string text) => StatusText.Text = text;

    private void Cleanup() => _config.Save();
}
