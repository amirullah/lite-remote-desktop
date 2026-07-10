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

        // Keep the top-bar sun/moon glyph in sync with the live theme (incl. OS switches while on System).
        ThemeManager.Changed += UpdateThemeIcon;
        UpdateThemeIcon();

        RelayBox.Text = _config.RelayAddress;
        if (!string.IsNullOrEmpty(_config.GoogleClientId)) GoogleClientIdBox.Text = _config.GoogleClientId;
        // Pre-fill the address from the most-recent LiteRemote-address session so reconnecting is one click.
        var last = _config.Ordered.FirstOrDefault(x => x.Kind == SessionKind.LiteRemoteIp && !string.IsNullOrWhiteSpace(x.Host));
        if (last != null) { HostBox.Text = last.Host; PortBox.Text = last.Port.ToString(); }
        RefreshRecent();
        Mode_Changed(this, new RoutedEventArgs());   // apply the initial (address-mode) field layout

        SessionRegistry.TabbedShell = true;   // the tab strip does session switching; suppress in-session buttons
        _fsHover.Tick += FsHover_Tick;        // reveal the fullscreen hover tab bar on the top edge

        // Keep the active session window pinned over the content area as the shell moves/resizes.
        SizeChanged += (_, _) => RepositionActive();
        LocationChanged += (_, _) => RepositionActive();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) RepositionActive(); };
        ContentArea.SizeChanged += (_, _) => RepositionActive();

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
        if ((sender as FrameworkElement)?.Tag is not SavedSession s) return;
        if (_config.ConfirmDelete &&
            MessageBox.Show(this, Loc.F("Connect.DeleteConfirm", s.DisplayName), Loc.T("Connect.WindowTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _config.DeleteSession(s.Id);
        RefreshRecent();
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
            AddSessionTab(win, s != null ? s.DisplayName
                                        : (NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host));
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
        AddSessionTab(win, req.Descriptor.DisplayName);
        SetStatus(Loc.T("Msg.SessionOpened"));
    }

    // ================= tabbed shell =================
    // Each session is its OWN borderless window — so mstscax / input / clipboard keep working untouched (no
    // re-parent) — owned by this shell and positioned EXACTLY over ContentArea, so it looks and behaves like
    // a tab. Switching a tab hides the other session windows and shows the chosen one; "+" hides them all,
    // which reveals the connect form sitting behind.

    private sealed class SessionTab
    {
        public Window Win = null!;
        public Border Container = null!;   // the whole tab element in the strip
        public Border Chip = null!;        // the clickable label chip (highlighted when active)
        public string Label = "";
    }

    private readonly List<SessionTab> _tabs = new();
    private SessionTab? _active;

    /// <summary>Embed a freshly-created (not-yet-shown) session window as a tab and activate it.</summary>
    private void AddSessionTab(Window win, string label)
    {
        win.WindowStyle = WindowStyle.None;
        win.ResizeMode = ResizeMode.NoResize;
        win.ShowInTaskbar = false;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Owner = this;

        var tab = new SessionTab { Win = win };
        BuildTabUi(tab, string.IsNullOrWhiteSpace(label) ? Loc.T("Shell.Session") : label.Trim());
        _tabs.Add(tab);
        TabList.Children.Add(tab.Container);
        // Keep the windowed tab strip HIDDEN while fullscreen — adding a session in fullscreen must not pop
        // the strip over the remote (the hover tab bar covers switching there). It reappears on exit.
        if (!_shellFullscreen) TabStrip.Visibility = Visibility.Visible;

        win.Closed += (_, _) => RemoveTab(tab);
        win.Show();
        PositionOver(win);
        ActivateTab(tab);
        HideTabBar();   // a new session just took over — the fullscreen bar should hide until next hover
    }

    private void BuildTabUi(SessionTab tab, string label)
    {
        tab.Label = label;
        var text = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170, Margin = new Thickness(12, 4, 6, 4),
        };
        var close = new Button
        {
            Content = "✕", Style = (Style)FindResource("Ghost"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0),
        };
        close.Click += (_, _) => { try { tab.Win.Close(); } catch { } };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text); row.Children.Add(close);

        tab.Chip = new Border { CornerRadius = new CornerRadius(8), Child = row, Cursor = System.Windows.Input.Cursors.Hand };
        tab.Chip.MouseLeftButtonUp += (_, _) => ActivateTab(tab);
        tab.Container = new Border { Margin = new Thickness(2, 0, 2, 0), Child = tab.Chip };
    }

    private void HighlightTab(SessionTab tab, bool active)
        => tab.Chip.Background = active ? (System.Windows.Media.Brush)FindResource("PanelHi") : System.Windows.Media.Brushes.Transparent;

    private void ActivateTab(SessionTab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            if (t == tab)
            {
                if (t.Win is ISessionWindow sw) sw.SetChrome(!_shellFullscreen);   // hide own bar in fullscreen
                t.Win.Show(); PositionOver(t.Win); try { t.Win.Activate(); } catch { }
            }
            else t.Win.Hide();
            HighlightTab(t, t == tab);
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e) => ShowConnectForm();

    /// <summary>Hide every session window so the connect form behind them shows (the "+" / home view).</summary>
    private void ShowConnectForm()
    {
        _active = null;
        foreach (var t in _tabs) { t.Win.Hide(); HighlightTab(t, false); }
    }

    private void RemoveTab(SessionTab tab)
    {
        _tabs.Remove(tab);
        TabList.Children.Remove(tab.Container);
        if (_tabs.Count == 0)
        {
            if (_shellFullscreen) SetShellFullscreen(false);   // last session closed — leave fullscreen
            TabStrip.Visibility = Visibility.Collapsed; _active = null;
        }
        else if (_active == tab) ActivateTab(_tabs[^1]);
        RefreshRecent();
    }

    /// <summary>Size/position a session window to cover ContentArea exactly, in PHYSICAL pixels (SetWindowPos)
    /// so it's correct even when the shell sits on a different-DPI monitor.</summary>
    private void PositionOver(Window win)
    {
        if (WindowState == WindowState.Minimized || ContentArea.ActualWidth < 1) return;
        try
        {
            var tl = ContentArea.PointToScreen(new Point(0, 0));
            var br = ContentArea.PointToScreen(new Point(ContentArea.ActualWidth, ContentArea.ActualHeight));
            int x = (int)System.Math.Round(tl.X), y = (int)System.Math.Round(tl.Y);
            int w = (int)System.Math.Round(br.X - tl.X), h = (int)System.Math.Round(br.Y - tl.Y);
            if (w > 0 && h > 0) SessionRegistry.SetPhysicalBounds(win, x, y, w, h);
        }
        catch { }
    }

    private void RepositionActive()
    {
        if (_active != null && WindowState != WindowState.Minimized) PositionOver(_active.Win);
    }

    // ---- shell fullscreen: cover the monitor, hide the active session's chrome, reveal a hover TAB bar ----
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private bool _shellFullscreen;
    public bool IsShellFullscreen => _shellFullscreen;
    private WindowState _fsPrevState = WindowState.Normal;
    private WindowStyle _fsPrevStyle = WindowStyle.SingleBorderWindow;
    private (int x, int y, int w, int h) _fsPrevPhys;
    private Window? _tabBar;
    private StackPanel? _tabBarList;
    private readonly System.Windows.Threading.DispatcherTimer _fsHover = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public void ToggleShellFullscreen() => SetShellFullscreen(!_shellFullscreen);

    public void SetShellFullscreen(bool on)
    {
        if (on == _shellFullscreen || _active == null) return;
        if (on)
        {
            _fsPrevState = WindowState; _fsPrevStyle = WindowStyle;
            _fsPrevPhys = SessionRegistry.GetPhysicalBounds(this);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            TabStrip.Visibility = Visibility.Collapsed;
            var scr = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle());
            SessionRegistry.FillScreen(this, scr);
            _shellFullscreen = true;
            if (_active.Win is ISessionWindow sw) sw.SetChrome(false);
            RepositionActive();
            _fsHover.Start();
        }
        else
        {
            _fsHover.Stop(); HideTabBar();
            WindowStyle = _fsPrevStyle;
            ResizeMode = ResizeMode.CanResize;
            TabStrip.Visibility = _tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SessionRegistry.SetPhysicalBounds(this, _fsPrevPhys.x, _fsPrevPhys.y, _fsPrevPhys.w, _fsPrevPhys.h);
            WindowState = _fsPrevState;
            _shellFullscreen = false;
            foreach (var t in _tabs) if (t.Win is ISessionWindow sw) sw.SetChrome(true);
            RepositionActive();
        }
    }

    private void FsHover_Tick(object? sender, EventArgs e)
    {
        if (!_shellFullscreen) { HideTabBar(); return; }
        if (!GetCursorPos(out var c)) return;
        var tl = PointToScreen(new Point(0, 0));
        double dy = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        bool nearTop = c.Y <= tl.Y + 2;
        bool overBar = _tabBar is { IsVisible: true } && c.Y <= tl.Y + (int)(46 * dy);
        if (nearTop || overBar) ShowTabBar(tl); else HideTabBar();
    }

    private void EnsureTabBar()
    {
        if (_tabBar != null) return;
        var bg = (System.Windows.Media.Brush)FindResource("Panel");
        _tabBarList = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dock = new DockPanel { LastChildFill = false, Background = bg };
        DockPanel.SetDock(_tabBarList, Dock.Left);
        var exit = MakeBarBtn(Loc.T("Rdp.Bar.ExitFullscreen"), (System.Windows.Media.Brush)FindResource("Accent"));
        exit.Click += (_, _) => { HideTabBar(); SetShellFullscreen(false); };
        DockPanel.SetDock(exit, Dock.Right);
        // Close the CURRENT session from fullscreen (there's no per-session ✕ up here).
        var close = MakeBarBtn(Loc.T("Shell.CloseSession"), (System.Windows.Media.Brush)FindResource("Bad"));
        close.Click += (_, _) => { HideTabBar(); try { _active?.Win.Close(); } catch { } };
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(_tabBarList);
        dock.Children.Add(exit);
        dock.Children.Add(close);
        _tabBar = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            Topmost = true, ShowInTaskbar = false, Height = 46, Owner = this, Content = dock, Background = bg,
        };
        // Without this, the first click on a non-activated top-most window is swallowed by activation, so the
        // tab/"+"/exit buttons need a second click. MA_NOACTIVATE delivers the click immediately.
        _tabBar.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(_tabBar).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(h)?.AddHook(NoActivateHook);
        };
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
        return IntPtr.Zero;
    }

    private void RebuildTabBar()
    {
        if (_tabBarList == null) return;
        _tabBarList.Children.Clear();
        foreach (var t in _tabs)
        {
            var tabRef = t;
            var b = MakeBarBtn(t.Label, t == _active ? (System.Windows.Media.Brush)FindResource("Accent")
                                                     : (System.Windows.Media.Brush)FindResource("GhostBg"));
            // Switch THEN hide the bar so you immediately see the chosen session (it reappears on hover).
            b.Click += (_, _) => { ActivateTab(tabRef); HideTabBar(); };
            _tabBarList.Children.Add(b);
        }
        var add = MakeBarBtn("+", (System.Windows.Media.Brush)FindResource("GhostBg"));
        // Stay in fullscreen — just reveal the connect form (hide session windows). Switch back via a tab.
        add.Click += (_, _) => { HideTabBar(); ShowConnectForm(); };
        _tabBarList.Children.Add(add);
    }

    private static Button MakeBarBtn(string text, System.Windows.Media.Brush background) => new()
    {
        Content = text, Margin = new Thickness(6, 6, 0, 6), Padding = new Thickness(14, 5, 14, 5),
        Foreground = System.Windows.Media.Brushes.White, Background = background, BorderThickness = new Thickness(0),
        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
    };

    private void ShowTabBar(Point tlPhysical)
    {
        EnsureTabBar();
        if (_tabBar!.IsVisible) return;   // already up — do NOT rebuild/reposition every hover tick
                                          // (that recreated the buttons ~8×/s → flicker + swallowed clicks)
        RebuildTabBar();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _tabBar.Left = tlPhysical.X / dpi.DpiScaleX;
        _tabBar.Top = tlPhysical.Y / dpi.DpiScaleY;
        _tabBar.Width = ActualWidth;
        _tabBar.Show();
    }

    private void HideTabBar() { try { if (_tabBar?.IsVisible == true) _tabBar.Hide(); } catch { } }

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

    /// <summary>Update the top-bar toggle glyph to reflect the theme actually in effect.</summary>
    private void UpdateThemeIcon()
    {
        // Segoe Fluent/MDL2 glyphs: E706 = brightness (sun), E708 = quiet-hours (moon).
        ThemeToggleBtn.Content = ThemeManager.IsDark ? "" : "";
    }

    /// <summary>One-tap light↔dark from the top bar; the "Follow system" choice stays in Settings.</summary>
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var t = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.SetPreference(t);
        _config.Theme = ThemeManager.ToKey(t);
        _config.Save();
        UpdateThemeIcon();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_config) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual };
        // Drop it down from the gear icon (flyout-style), not centered on the whole window.
        try
        {
            var br = SettingsBtn.PointToScreen(new Point(SettingsBtn.ActualWidth, SettingsBtn.ActualHeight));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double right = br.X / dpi.DpiScaleX, bottom = br.Y / dpi.DpiScaleY;
            w.Left = System.Math.Max(8, right - w.Width);   // right edge under the icon
            w.Top = bottom + 6;
        }
        catch { }
        w.ShowDialog();
        UpdateThemeIcon();   // theme may have changed inside Settings
        RefreshRecent();     // saved sessions may have been cleared inside Settings
    }

    /// <summary>Called by Settings after a language switch to refresh code-set (non-binding) text.</summary>
    public void ApplyLanguageChange() => Mode_Changed(this, new RoutedEventArgs());

    private void SetStatus(string text) => StatusText.Text = text;

    private void Cleanup() => _config.Save();
}
