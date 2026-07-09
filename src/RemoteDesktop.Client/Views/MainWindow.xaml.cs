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

    private bool _suppressPrefs;

    public MainWindow()
    {
        InitializeComponent();

        // Theme / language pickers reflect the saved preference (without re-triggering a save).
        _suppressPrefs = true;
        ThemeBox.SelectedIndex = ThemeManager.Preference switch { AppTheme.Light => 1, AppTheme.Dark => 2, _ => 0 };
        LangBox.SelectedIndex = Loc.Lang == "en" ? 1 : 0;
        _suppressPrefs = false;

        RelayBox.Text = _config.RelayAddress;
        if (!string.IsNullOrEmpty(_config.GoogleClientId)) GoogleClientIdBox.Text = _config.GoogleClientId;
        // Pre-fill the address from the most-recent LiteRemote-address session so reconnecting is one click.
        var last = _config.Ordered.FirstOrDefault(x => x.Kind == SessionKind.LiteRemoteIp && !string.IsNullOrWhiteSpace(x.Host));
        if (last != null) { HostBox.Text = last.Host; PortBox.Text = last.Port.ToString(); }
        RefreshRecent();
        Mode_Changed(this, new RoutedEventArgs());   // apply the initial (address-mode) field layout
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
                NameBox.Text = s.Label;
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
                NameBox.Text = s.Label;
                HostBox.Text = s.Host;
                PortBox.Text = s.Port.ToString();

                string? vpnPath = RestoreVpn(s);   // ticks Use-VPN + prefills profile/user/pass if saved

                // The saved session wanted VPN but its .ovpn is gone — don't silently connect OUTSIDE the
                // tunnel; prefill the form and let the user re-pick it.
                if (s.UseVpn && vpnPath == null) { SetStatus(Loc.T("Msg.VpnProfileMissing")); FallBackToForm(s); break; }

                var cred = BuildSavedCredential(s);
                if (cred != null)
                {
                    LaunchSession(new SessionRequest
                    {
                        IdMode = false, Host = s.Host, Port = s.Port,
                        Credential = cred, Settings = BuildInitialSettings(),
                        VpnProfilePath = vpnPath, VpnUser = VpnUserBox.Text, VpnPass = VpnPassBox.Password,
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

    /// <summary>Restore a saved session's VPN choice into the form; returns the profile path if usable.</summary>
    private string? RestoreVpn(SavedSession s)
    {
        var vp = _config.GetVpn(s.VpnProfileId);
        if (s.UseVpn && vp != null && System.IO.File.Exists(vp.OvpnPath))
        {
            UseVpnCheck.IsChecked = true;
            VpnArea.Visibility = Visibility.Visible;
            VpnProfileBox.Text = vp.OvpnPath;
            VpnUserBox.Text = vp.Username;
            var vpass = _config.GetSecret("vpn:" + vp.OvpnPath);
            if (!string.IsNullOrEmpty(vpass)) VpnPassBox.Password = vpass;
            return vp.OvpnPath;
        }
        UseVpnCheck.IsChecked = false;
        VpnArea.Visibility = Visibility.Collapsed;
        return null;
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
        SetStatus(Loc.F("Msg.EnterPasswordFor", s.DisplayName));
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

                // ---- validate the main-form RDP inputs (password may be left blank on purpose) ----
                if (host.Length == 0) { Invalid(Loc.T("Msg.RdpNeedHost"), HostBox); return; }
                if (UseVpnCheck.IsChecked == true)
                {
                    if (string.IsNullOrWhiteSpace(VpnProfileBox.Text) || !System.IO.File.Exists(VpnProfileBox.Text))
                    { Invalid(Loc.T("Msg.VpnNeedProfile"), VpnProfileBox); return; }
                    if (VpnUserBox.Text.Trim().Length == 0) { Invalid(Loc.T("Msg.VpnNeedUser"), VpnUserBox); return; }
                    if (VpnPassBox.Password.Length == 0) { Invalid(Loc.T("Msg.VpnNeedPass"), VpnPassBox); return; }
                }
            }
            if (host.Length == 0) { SetStatus(Loc.T("Msg.RdpNeedHost")); return; }

            var win = new RdpWindow(host);
            if (s != null)
            {
                win.ApplySaved(s, _config);
            }
            else
            {
                string? vpnPath = UseVpnCheck.IsChecked == true &&
                    !string.IsNullOrWhiteSpace(VpnProfileBox.Text) && System.IO.File.Exists(VpnProfileBox.Text)
                    ? VpnProfileBox.Text : null;
                win.ApplyDirect(RdpUserBox.Text, RdpPassBox.Password, RdpSaveCheck.IsChecked == true,
                                vpnPath, VpnUserBox.Text, VpnPassBox.Password, NameBox.Text.Trim());
            }
            win.Closed += (_, _) => RefreshRecent();
            win.Show();
            SetStatus(Loc.T("Msg.RdpOpened"));
        }
        catch (Exception ex)
        {
            Services.Diag.Log("OpenRdp FAILED: " + ex);
            SetStatus(Loc.F("Msg.RdpFailed", ex.Message));
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IdModePanel is null || AddrModePanel is null || LoginSection is null || RdpCredPanel is null ||
            VpnToggleSection is null || AdvRelay is null || AdvProtocolOpts is null || AdvancedExpander is null) return;
        bool idMode = ModeIdRadio.IsChecked == true;
        bool rdpMode = ModeRdpRadio.IsChecked == true;

        IdModePanel.Visibility = idMode ? Visibility.Visible : Visibility.Collapsed;
        AddrModePanel.Visibility = idMode ? Visibility.Collapsed : Visibility.Visible;  // address for LiteRemote-IP + RDP
        LoginSection.Visibility = rdpMode ? Visibility.Collapsed : Visibility.Visible;   // protocol sign-in (IP + ID)
        RdpCredPanel.Visibility = rdpMode ? Visibility.Visible : Visibility.Collapsed;   // Windows RDP sign-in

        // VPN only where it applies: direct address (IP) and RDP. ID connects through the relay.
        VpnToggleSection.Visibility = idMode ? Visibility.Collapsed : Visibility.Visible;

        // Advanced adapts: relay only for ID; session options for the protocol (IP + ID); nothing for RDP.
        AdvRelay.Visibility = idMode ? Visibility.Visible : Visibility.Collapsed;
        AdvProtocolOpts.Visibility = rdpMode ? Visibility.Collapsed : Visibility.Visible;
        AdvancedExpander.Visibility = rdpMode ? Visibility.Collapsed : Visibility.Visible;

        ConnectButton.Content = Loc.T(rdpMode ? "Connect.OpenRdpButton" : "Connect.ConnectButton");

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
            bool useVpn = !idMode && UseVpnCheck.IsChecked == true;

            // ---- validate required fields (optional ones excepted) ----
            string relay = RelayBox.Text.Trim();
            string id = Shared.Relay.RelayProtocol.NormalizeId(IdBox.Text);
            string host = HostBox.Text.Trim();
            int port = 7443;

            if (idMode)
            {
                if (relay.Length == 0) { Invalid(Loc.T("Msg.NeedRelay")); AdvancedExpander.IsExpanded = true; RelayBox.Focus(); return; }
                if (id.Length != 9) { Invalid(Loc.T("Msg.NeedId9"), IdBox); return; }
            }
            else
            {
                if (host.Length == 0) { Invalid(Loc.T("Msg.NeedHost"), HostBox); return; }
                if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535) { Invalid(Loc.T("Msg.BadPort"), PortBox); return; }
            }
            if (auth == ProtocolAuth.Password && PasswordBox.Password.Length == 0) { Invalid(Loc.T("Msg.NeedHostPwd"), PasswordBox); return; }
            if (auth == ProtocolAuth.Google && GoogleClientIdBox.Text.Trim().Length == 0) { Invalid(Loc.T("Msg.NeedGoogleId"), GoogleClientIdBox); return; }

            string? vpnPath = null;
            if (useVpn)
            {
                if (string.IsNullOrWhiteSpace(VpnProfileBox.Text) || !System.IO.File.Exists(VpnProfileBox.Text))
                { Invalid(Loc.T("Msg.VpnNeedProfile"), VpnProfileBox); return; }
                vpnPath = VpnProfileBox.Text.Trim();
            }

            // ---- build the credential (Google may open a browser) ----
            Credential? credential = await BuildCredentialAsync();
            if (credential is null) { SetStatus(Loc.T("Msg.LoginFailed")); return; }

            var req = new SessionRequest { IdMode = idMode, Credential = credential, Settings = BuildInitialSettings() };
            string label = NameBox.Text.Trim();

            if (idMode)
            {
                _config.RelayAddress = relay; _config.Save();
                req.Relay = relay;
                req.Id = id;
                req.Descriptor = StableId(new SavedSession
                {
                    Kind = SessionKind.LiteRemoteId, RelayId = id, Auth = auth, SavePassword = savePwd, Label = label,
                });
            }
            else
            {
                req.Host = host;
                req.Port = port;

                string? vpnId = null;
                if (vpnPath != null)
                {
                    req.VpnProfilePath = vpnPath;
                    req.VpnUser = VpnUserBox.Text;
                    req.VpnPass = VpnPassBox.Password;
                    // Remember the profile + VPN username; remember the VPN password when 'remember' is
                    // ticked (independent of the auth radio, so it also works for Google-auth sessions).
                    bool rememberVpn = SavePasswordCheck.IsChecked == true;
                    var vpn = _config.UpsertVpn(new VpnProfile { OvpnPath = vpnPath, Username = VpnUserBox.Text.Trim(), SavePassword = rememberVpn });
                    vpnId = vpn.Id;
                    _config.SetSecret("vpn:" + vpnPath, rememberVpn && VpnPassBox.Password.Length > 0 ? VpnPassBox.Password : null);
                    _config.LastVpnProfile = vpnPath; _config.Save();
                }

                req.Descriptor = StableId(new SavedSession
                {
                    Kind = SessionKind.LiteRemoteIp, Host = host, Port = port,
                    UseVpn = vpnId != null, VpnProfileId = vpnId, Auth = auth, SavePassword = savePwd, Label = label,
                });
            }

            LaunchSession(req);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Msg.Error", ex.Message));
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
        SetStatus(Loc.T("Msg.SessionOpened"));
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
        if (clientId.Length == 0) { SetStatus(Loc.T("Msg.NeedGoogleId")); return null; }
        _config.GoogleClientId = clientId;
        _config.Save();

        SetStatus(Loc.T("Msg.WaitingGoogle"));
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

    private void UseVpn_Changed(object sender, RoutedEventArgs e)
    {
        if (VpnArea is null) return;
        VpnArea.Visibility = UseVpnCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseVpn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "OpenVPN profile (*.ovpn)|*.ovpn|All files|*.*" };
        if (dlg.ShowDialog() == true) VpnProfileBox.Text = dlg.FileName;
    }

    /// <summary>Show a validation message and focus the offending field.</summary>
    private void Invalid(string msg, Control? focus = null)
    {
        SetStatus(msg);
        focus?.Focus();
    }

    // ---------------- theme + language ----------------

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPrefs) return;
        var t = ThemeBox.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System };
        ThemeManager.SetPreference(t);
        _config.Theme = ThemeManager.ToKey(t);
        _config.Save();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPrefs) return;
        var lang = LangBox.SelectedIndex == 1 ? "en" : "id";
        Loc.SetLanguage(lang);
        Mode_Changed(this, new RoutedEventArgs());   // refresh the code-set Connect button label
        _config.Language = lang;
        _config.Save();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void Cleanup() => _config.Save();
}
