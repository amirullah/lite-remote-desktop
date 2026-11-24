using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Relay;

namespace RemoteDesktop.Maui;

/// <summary>Add or edit one saved session. Works on a copy keyed by Id, so other sessions are never touched.</summary>
public partial class SessionEditPage : ContentPage
{
    private readonly SavedSession _session;
    private readonly bool _isNew;
    private bool _idMode, _loadedPassword;
    private string _quality = "balanced";

    /// <param name="existing">null = new session.</param>
    /// <param name="hint">optional message shown at the bottom (e.g. ask for the password).</param>
    public SessionEditPage(SavedSession? existing, string? hint = null, bool hintIsError = false)
    {
        InitializeComponent();
        _isNew = existing is null;
        _session = existing is null ? new SavedSession() : Clone(existing);

        HeaderLabel.Text = _isNew ? "Sesi baru" : "Ubah sesi";
        NameEntry.Text = _session.Name;
        HostEntry.Text = _session.Host;
        PortEntry.Text = _session.Port.ToString();
        IdEntry.Text = _session.PartnerId.Length == 9 ? RelayProtocol.FormatId(_session.PartnerId) : _session.PartnerId;
        RelayEntry.Text = _session.Relay;
        RememberCheck.IsChecked = _isNew || _session.RememberPassword;
        SetMode(_session.IsIdMode);
        SetQuality(_session.Quality);
        if (!string.IsNullOrEmpty(hint)) { StatusLabel.TextColor = Color.FromArgb(hintIsError ? "#E5484D" : "#2F7CF6"); StatusLabel.Text = hint; }
    }

    private static SavedSession Clone(SavedSession s) => new()
    {
        Id = s.Id, Name = s.Name, Mode = s.Mode, Host = s.Host, Port = s.Port, PartnerId = s.PartnerId,
        Relay = s.Relay, Quality = s.Quality, RememberPassword = s.RememberPassword, LastUsed = s.LastUsed,
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loadedPassword || _isNew || !_session.RememberPassword) return;
        _loadedPassword = true;
        if (string.IsNullOrEmpty(PasswordEntry.Text))
            PasswordEntry.Text = await SessionStore.GetPasswordAsync(_session.Id) ?? "";
    }

    private void Back_Clicked(object? sender, EventArgs e) => _ = Navigation.PopAsync();

    private void ModeAddr_Clicked(object? sender, EventArgs e) => SetMode(false);
    private void ModeId_Clicked(object? sender, EventArgs e) => SetMode(true);

    private void SetMode(bool id)
    {
        _idMode = id;
        AddrPanel.IsVisible = !id;
        IdPanel.IsVisible = id;
        SegStyle(ModeAddrBtn, !id);
        SegStyle(ModeIdBtn, id);
    }

    private void Quality_Clicked(object? sender, EventArgs e) => SetQuality((sender as Button)?.CommandParameter?.ToString());

    private void SetQuality(string? q)
    {
        _quality = QualityProfiles.Normalize(q);
        SegStyle(QSaverBtn, _quality == "saver");
        SegStyle(QBalancedBtn, _quality == "balanced");
        SegStyle(QSharpBtn, _quality == "sharp");
    }

    internal static void SegStyle(Button b, bool active)
    {
        b.BackgroundColor = active ? Color.FromArgb("#2F7CF6") : Colors.Transparent;
        b.TextColor = active ? Colors.White : Color.FromArgb("#8FA3C4");
    }

    private void Eye_Clicked(object? sender, EventArgs e) => PasswordEntry.IsPassword = !PasswordEntry.IsPassword;

    private void ShowError(string msg) { StatusLabel.TextColor = Color.FromArgb("#E5484D"); StatusLabel.Text = msg; }

    /// <summary>Copies the form into the session and validates the target (password is optional for "Simpan saja").</summary>
    private string? ReadForm()
    {
        _session.Name = (NameEntry.Text ?? "").Trim();
        _session.Mode = _idMode ? "id" : "addr";
        _session.Host = (HostEntry.Text ?? "").Trim();
        _session.Port = int.TryParse(PortEntry.Text, out var p) && p is >= 1 and <= 65535 ? p : 7443;
        PortEntry.Text = _session.Port.ToString();
        _session.PartnerId = RelayProtocol.NormalizeId(IdEntry.Text ?? "");
        _session.Relay = (RelayEntry.Text ?? "").Trim();
        _session.Quality = _quality;

        if (_idMode)
        {
            if (_session.PartnerId.Length != 9) return "ID harus 9 digit.";
            if (SessionStore.SplitHostPort(_session.Relay, RelayProtocol.DefaultPort).host.Length == 0) return "Isi alamat server relay.";
        }
        else if (_session.Host.Length == 0) return "Isi alamat host.";
        return null;
    }

    private async Task PersistAsync(string password)
    {
        _session.RememberPassword = RememberCheck.IsChecked && password.Length > 0;
        await SessionStore.SetPasswordAsync(_session.Id, _session.RememberPassword ? password : null);
        SessionStore.Upsert(_session);
    }

    private async void Save_Clicked(object? sender, EventArgs e)
    {
        var err = ReadForm();
        if (err is not null) { ShowError(err); return; }
        if (_isNew) _session.LastUsed = DateTime.UtcNow;   // new session shows at the top
        await PersistAsync(PasswordEntry.Text ?? "");
        await Navigation.PopAsync();
    }

    private async void SaveConnect_Clicked(object? sender, EventArgs e)
    {
        var err = ReadForm();
        if (err is not null) { ShowError(err); return; }
        var password = PasswordEntry.Text ?? "";
        err = SessionStore.TryBuildConnect(_session, password, out var connect, out var title);
        if (err is not null || connect is null) { ShowError(err ?? "Data sesi tidak lengkap."); return; }

        _session.LastUsed = DateTime.UtcNow;
        await PersistAsync(password);
        StatusLabel.Text = "";
        // Back from the session goes straight home, not to this form.
        await Navigation.PushAsync(new SessionPage(connect, title, QualityProfiles.BuildSettings(_session.Quality)));
        Navigation.RemovePage(this);
    }
}
