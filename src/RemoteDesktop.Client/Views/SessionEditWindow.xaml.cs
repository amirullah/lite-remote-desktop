using System;
using System.IO;
using System.Windows;
using RemoteDesktop.Client.Services;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// Edit a saved session's editable parts: display name, the remote account (RDP Windows user/password,
/// or the LiteRemote host password) and the VPN profile + VPN user/password. Address/kind stay fixed
/// (they are the session's identity). Secrets are stored the same way the connect paths read them:
/// <c>rdp:&lt;host&gt;</c> ("user\npassword"), <c>session:&lt;id&gt;</c> (protocol password), and
/// <c>vpn:&lt;ovpn-path&gt;</c> (VPN password); VPN user lives on the <see cref="VpnProfile"/>.
/// </summary>
public partial class SessionEditWindow : Window
{
    private readonly SavedSession _s;
    private readonly ClientConfig _cfg;

    public SessionEditWindow(SavedSession s, ClientConfig cfg)
    {
        InitializeComponent();
        _s = s;
        _cfg = cfg;

        KindText.Text = s.KindLabel;
        AddrText.Text = s.Kind == SessionKind.LiteRemoteId
            ? $"ID {s.RelayId}"
            : (s.Port is 0 or 3389 or 7443 ? s.Host : $"{s.Host}:{s.Port}");
        LabelBox.Text = s.Label;
        SavePassCheck.IsChecked = s.SavePassword;

        // ----- account credentials -----
        if (s.Kind == SessionKind.Rdp)
        {
            // UserLabel/PassLabel keep their XAML {loc:Loc Edit.Windows*Label} bindings (Windows user/password).
            var combo = cfg.GetSecret("rdp:" + s.Host);
            if (!string.IsNullOrEmpty(combo))
            {
                var p = combo.Split('\n');
                UserBox.Text = p.Length > 0 ? p[0] : s.Username;
                if (p.Length > 1) PassBox.Password = p[1];
            }
            else UserBox.Text = s.Username;
        }
        else if (s.Auth == ProtocolAuth.Google)
        {
            // Google has no stored account password, but the session may still carry a VPN password —
            // so keep the "save password" toggle (it then governs only the VPN secret).
            UserRow.Visibility = Visibility.Collapsed;
            PassRow.Visibility = Visibility.Collapsed;
            GoogleNote.Visibility = Visibility.Visible;
        }
        else // LiteRemote protocol, password auth
        {
            UserRow.Visibility = Visibility.Collapsed;
            PassLabel.Text = Loc.T("Edit.PasswordLabel");
            var pwd = cfg.GetSecret("session:" + s.Id);
            if (!string.IsNullOrEmpty(pwd)) PassBox.Password = pwd;
        }

        // ----- VPN -----
        var vp = cfg.GetVpn(s.VpnProfileId);
        if (s.UseVpn && vp != null)
        {
            UseVpnCheck.IsChecked = true;
            VpnPanel.Visibility = Visibility.Visible;
            VpnProfileBox.Text = vp.OvpnPath;
            VpnUserBox.Text = vp.Username;
            var vpass = cfg.GetSecret("vpn:" + vp.OvpnPath);
            if (!string.IsNullOrEmpty(vpass)) { VpnPassBox.Password = vpass; SavePassCheck.IsChecked = true; }
        }
    }

    private void UseVpn_Click(object sender, RoutedEventArgs e) =>
        VpnPanel.Visibility = UseVpnCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseVpn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Common.OvpnFileFilter") };
        if (dlg.ShowDialog(this) == true) VpnProfileBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        bool isGoogle = _s.Auth == ProtocolAuth.Google && _s.Kind != SessionKind.Rdp;
        bool remember = SavePassCheck.IsChecked == true;
        bool saveAccountPwd = remember && !isGoogle;                 // Google has no account password
        bool useVpn = UseVpnCheck.IsChecked == true && !string.IsNullOrWhiteSpace(VpnProfileBox.Text);
        bool saveVpnPwd = useVpn && remember;                        // VPN saving is independent of Google

        // Capture the currently-referenced VPN profile so we can clear its secret if VPN is removed/changed.
        string? oldVpnPath = _cfg.GetVpn(_s.VpnProfileId)?.OvpnPath;

        string? vpnId = null;
        if (useVpn)
        {
            var path = VpnProfileBox.Text.Trim();
            if (!File.Exists(path)) { StatusText.Text = Loc.T("Edit.Status.OvpnNotFound"); return; }
            var v = _cfg.UpsertVpn(new VpnProfile { OvpnPath = path, Username = VpnUserBox.Text.Trim(), SavePassword = saveVpnPwd });
            vpnId = v.Id;
            _cfg.SetSecret("vpn:" + path, saveVpnPwd ? VpnPassBox.Password : null);
            // Profile path changed → drop the old profile's orphaned secret.
            if (oldVpnPath != null && !string.Equals(oldVpnPath, path, StringComparison.OrdinalIgnoreCase))
                _cfg.SetSecret("vpn:" + oldVpnPath, null);
        }
        else if (oldVpnPath != null)
        {
            _cfg.SetSecret("vpn:" + oldVpnPath, null);   // VPN removed → clear its password too
        }

        // account secret
        if (_s.Kind == SessionKind.Rdp)
            _cfg.SetSecret("rdp:" + _s.Host, saveAccountPwd ? (UserBox.Text.Trim() + "\n" + PassBox.Password) : null);
        else if (!isGoogle)
            _cfg.SetSecret("session:" + _s.Id, saveAccountPwd ? PassBox.Password : null);

        var updated = _s with
        {
            Label = LabelBox.Text.Trim(),
            Username = _s.Kind == SessionKind.Rdp ? UserBox.Text.Trim() : _s.Username,
            SavePassword = saveAccountPwd,
            UseVpn = useVpn,
            VpnProfileId = useVpn ? vpnId : null,
        };
        _cfg.UpsertSession(updated, touch: false);   // edit shouldn't bump the recent order
        _cfg.Save();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
