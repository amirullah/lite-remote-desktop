using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteDesktop.Client.Services;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// The connect hub: pick a connection type (LiteRemote by address/ID, or Windows RDP), optionally
/// through a VPN, and launch it into its OWN window (<see cref="SessionWindow"/> for the LiteRemote
/// protocol, <see cref="RdpWindow"/> for RDP). Several sessions run at once; this window stays a
/// launcher and keeps the Recent &amp; Saved column for one-click reconnect.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ClientConfig _config = ClientConfig.Shared;
    private readonly PinStore _pins = new(AppPaths.PinStore);

    public MainWindow()
    {
        InitializeComponent();
        RelayBox.Text = _config.RelayAddress;
        if (!string.IsNullOrEmpty(_config.GoogleClientId)) GoogleClientIdBox.Text = _config.GoogleClientId;
        // Pre-fill the address from the most-recently-used saved session so reconnecting is one click.
        var last = _config.Ordered.FirstOrDefault(x => x.Kind != SessionKind.LiteRemoteId && !string.IsNullOrWhiteSpace(x.Host));
        if (last != null) { HostBox.Text = last.Host; PortBox.Text = last.Port.ToString(); }
        RefreshRecent();
        Closing += (_, _) => Cleanup();
    }

    // ---------------- Recent / saved sessions (right column) ----------------

    /// <summary>Re-bind the right-column list from the shared store (pinned first, then most-recent).
    /// The store is process-wide, so sessions saved by session/RDP windows are already here.</summary>
    private void RefreshRecent()
    {
        try
        {
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

    /// <summary>Open the editor for a saved session (remote account + VPN user/password).</summary>
    private void RecentEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedSession s) return;
        var dlg = new SessionEditWindow(s, _config) { Owner = this };
        if (dlg.ShowDialog() == true) RefreshRecent();
    }

    /// <summary>
    /// One-click reconnect from the Recent list. RDP and any session with a remembered password connect
    /// straight away; a protocol session without a saved password prefills the form and asks for it.
    /// </summary>
    private void ConnectSaved(SavedSession s)
    {
        _config.TouchSession(s.Id);
        switch (s.Kind)
        {
            case SessionKind.Rdp:
                OpenRdp(s);
                break;

            case SessionKind.LiteRemoteId:
            {
                ModeIdRadio.IsChecked = true;
                IdBox.Text = s.RelayId;
                if (RelayBox.Text.Trim().Length == 0) RelayBox.Text = _config.RelayAddress; // ID needs a relay
                var cred = BuildSavedCredential(s);
                if (cred != null)
                {
                    LaunchSession(new SessionRequest
                    {
                        IdMode = true, Relay = RelayBox.Text.Trim(), Id = s.RelayId,
                        Credential = cred, Settings = BuildInitialSettings(),
                        Descriptor = s with { LastUsedUtc = DateTime.UtcNow },
                    });
                }
                else FallBackToForm(s);
                break;
            }

            default: // LiteRemoteIp
            {
                ModeAddrRadio.IsChecked = true;
                HostBox.Text = s.Host;
                PortBox.Text = s.Port.ToString();

                string? vpnPath = null;
                var vp = _config.GetVpn(s.VpnProfileId);
                if (s.UseVpn && vp != null && System.IO.File.Exists(vp.OvpnPath))
                {
                    NetworkModeBox.SelectedIndex = 1;
                    VpnProfileBox.Text = vp.OvpnPath;
                    vpnPath = vp.OvpnPath;
                }
                else NetworkModeBox.SelectedIndex = 0;

                var cred = BuildSavedCredential(s);
                if (cred != null)
                {
                    LaunchSession(new SessionRequest
                    {
                        IdMode = false, Host = s.Host, Port = s.Port,
                        Credential = cred, Settings = BuildInitialSettings(),
                        VpnProfilePath = vpnPath,
                        Descriptor = s with { LastUsedUtc = DateTime.UtcNow },
                    });
                }
                else FallBackToForm(s);
                break;
            }
        }
        // No RefreshRecent() here: each launched window refreshes the list on Closed, and refreshing now
        // would swap _config out from under an in-flight Google OAuth on the fall-back path.
    }

    /// <summary>No remembered password: prefill the form and let the user finish (Google reconnects via browser).</summary>
    private void FallBackToForm(SavedSession s)
    {
        if (s.Auth == ProtocolAuth.Google)
        {
            AuthGoogleRadio.IsChecked = true;
            Connect_Click(this, new RoutedEventArgs());   // opens the browser for OAuth, then launches
            return;
        }
        AuthPasswordRadio.IsChecked = true;
        SavePasswordCheck.IsChecked = s.SavePassword;     // keep the user's remember choice
        PasswordBox.Focus();
        SetStatus($"Masukkan password untuk {s.DisplayName}, lalu tekan Connect.");
    }

    /// <summary>Build a credential for a saved session WITHOUT the form, from the DPAPI-remembered password.</summary>
    private Credential? BuildSavedCredential(SavedSession s)
    {
        if (s.SavePassword && s.Auth != ProtocolAuth.Google)
        {
            var pwd = _config.GetSecret("session:" + s.Id);
            if (!string.IsNullOrEmpty(pwd)) return new PasswordCredential(pwd);
        }
        return null;
    }

    /// <summary>
    /// Open a real Windows RDP session embedded inside LiteRemote (the mstscax.dll ActiveX control),
    /// not by launching the external mstsc app — its own window, so it can run alongside other sessions.
    /// From the main form (s == null) the Windows user/password entered there connect directly, with no
    /// second login form. From Recent (s != null) it reconnects the saved target.
    /// </summary>
    private void OpenRdp(SavedSession? s = null)
    {
        try
        {
            string host;
            if (s != null)
            {
                host = s.Port is 0 or 3389 ? s.Host : $"{s.Host}:{s.Port}";
            }
            else
            {
                host = HostBox.Text.Trim();
                // Honour a non-default port from the box (RDP defaults to 3389 when none is given).
                if (host.Length > 0 && !host.Contains(':') &&
                    int.TryParse(PortBox.Text, out var p) && p is not (0 or 3389))
                    host = $"{host}:{p}";
            }
            if (host.Length == 0) { SetStatus("Masukkan alamat host untuk RDP."); return; }

            var win = new RdpWindow(host);
            if (s != null)
            {
                win.ApplySaved(s, _config);
            }
            else
            {
                // Fill once, connect directly: hand the Windows credentials AND the VPN profile+user+pass
                // (from Advanced) straight to the RDP window, which connects without showing its own form.
                string? vpnPath = null;
                if (NetworkModeBox.SelectedIndex == 1 &&
                    !string.IsNullOrWhiteSpace(VpnProfileBox.Text) && System.IO.File.Exists(VpnProfileBox.Text))
                    vpnPath = VpnProfileBox.Text;
                win.ApplyDirect(RdpUserBox.Text, RdpPassBox.Password, RdpSaveCheck.IsChecked == true,
                                vpnPath, VpnUserBox.Text, VpnPassBox.Password);
            }
            win.Closed += (_, _) => RefreshRecent();
            win.Show();
            SetStatus("Sesi RDP dibuka di jendela baru.");
        }
        catch (Exception ex)
        {
            Services.Diag.Log("OpenRdp FAILED: " + ex);
            SetStatus($"Gagal membuka RDP tertanam: {ex.Message}");
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IdModePanel is null || AddrModePanel is null || LoginSection is null || RdpCredPanel is null) return;
        bool idMode = ModeIdRadio.IsChecked == true;
        bool rdpMode = ModeRdpRadio.IsChecked == true;

        IdModePanel.Visibility = idMode ? Visibility.Visible : Visibility.Collapsed;
        AddrModePanel.Visibility = idMode ? Visibility.Collapsed : Visibility.Visible; // address for LiteRemote-IP + RDP
        LoginSection.Visibility = rdpMode ? Visibility.Collapsed : Visibility.Visible;  // protocol sign-in
        RdpCredPanel.Visibility = rdpMode ? Visibility.Visible : Visibility.Collapsed;  // Windows RDP sign-in
        ConnectButton.Content = rdpMode ? "Buka Windows RDP" : "Connect";

        // Nudge the port to the right service when the type changes (only if still on the other default).
        if (rdpMode && PortBox.Text.Trim() == "7443") PortBox.Text = "3389";
        else if (!rdpMode && PortBox.Text.Trim() == "3389") PortBox.Text = "7443";
    }

    // ---------------- connect (launches a session window) ----------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        // Windows RDP is a different beast — hand off to its self-contained window.
        if (ModeRdpRadio.IsChecked == true) { OpenRdp(null); return; }

        ConnectButton.IsEnabled = false;
        try
        {
            bool idMode = ModeIdRadio.IsChecked == true;
            bool savePwd = AuthPasswordRadio.IsChecked == true && SavePasswordCheck.IsChecked == true;
            var auth = AuthGoogleRadio.IsChecked == true ? ProtocolAuth.Google : ProtocolAuth.Password;

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
                req.Descriptor = StableId(new SavedSession
                {
                    Kind = SessionKind.LiteRemoteId, RelayId = id, Auth = auth, SavePassword = savePwd,
                });
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

                req.Descriptor = StableId(new SavedSession
                {
                    Kind = SessionKind.LiteRemoteIp, Host = host, Port = port,
                    UseVpn = vpnId != null, VpnProfileId = vpnId, Auth = auth, SavePassword = savePwd,
                });
            }

            LaunchSession(req);
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

    /// <summary>
    /// Reuse the Id of an existing saved session with the same identity, so the remembered-password
    /// secret (<c>session:&lt;id&gt;</c>) stays valid across reconnects and we never orphan a ciphertext.
    /// </summary>
    private SavedSession StableId(SavedSession s)
    {
        var existing = _config.Sessions.FirstOrDefault(x => x.IdentityKey == s.IdentityKey);
        return existing != null ? s with { Id = existing.Id } : s;
    }

    private void LaunchSession(SessionRequest req)
    {
        var win = new SessionWindow(req, _pins);
        win.Closed += (_, _) => RefreshRecent();
        win.Show();
        SetStatus("Sesi dibuka di jendela baru — Anda bisa membuka koneksi lain sekaligus.");
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

    private void SetStatus(string text) => StatusText.Text = text;

    private void Cleanup() => _config.Save();
}
