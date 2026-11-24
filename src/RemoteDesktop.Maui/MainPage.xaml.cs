using RemoteDesktop.Maui.Services;

namespace RemoteDesktop.Maui;

/// <summary>Home: list of saved sessions (tap = connect, ✎ = edit, 🗑 = delete) + "+ Sesi baru".</summary>
public partial class MainPage : ContentPage
{
    private bool _migrated;

    public MainPage()
    {
        InitializeComponent();
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
        if (!_migrated)
        {
            _migrated = true;
            await SessionStore.MigrateLegacyAsync();
        }
        Refresh();
    }

    private void Refresh()
    {
        var list = SessionStore.Load();
        BindableLayout.SetItemsSource(SessionList, list);
        SessionList.IsVisible = list.Count > 0;
        EmptyState.IsVisible = list.Count == 0;
        StatusLabel.IsVisible = false;
    }

    private async void NewSession_Clicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new SessionEditPage(null));

    private static SavedSession? SessionOf(object? sender) => (sender as BindableObject)?.BindingContext as SavedSession;

    private async void Edit_Clicked(object? sender, EventArgs e)
    {
        if (SessionOf(sender) is { } s) await Navigation.PushAsync(new SessionEditPage(s));
    }

    private async void Delete_Clicked(object? sender, EventArgs e)
    {
        if (SessionOf(sender) is not { } s) return;
        bool ok = await DisplayAlert("Hapus sesi?", $"Sesi \"{s.DisplayName}\" dan password tersimpannya akan dihapus dari HP ini.", "Hapus", "Batal");
        if (!ok) return;
        SessionStore.Delete(s.Id);
        Refresh();
    }

    private async void License_Tapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new LicensePage());

    private bool _connecting;

    private async void Session_Tapped(object? sender, TappedEventArgs e)
    {
        if (SessionOf(sender) is not { } s || _connecting) return;
        _connecting = true;
        try
        {
            var password = s.RememberPassword ? await SessionStore.GetPasswordAsync(s.Id) ?? "" : "";
            if (password.Length == 0)
            {
                await Navigation.PushAsync(new SessionEditPage(s, "Masukkan password, lalu ketuk \"Simpan & sambungkan\"."));
                return;
            }
            var err = SessionStore.TryBuildConnect(s, password, out var connect, out var title);
            if (err is not null || connect is null)
            {
                await Navigation.PushAsync(new SessionEditPage(s, err, hintIsError: true));
                return;
            }
            SessionStore.Touch(s.Id);
            await VpnNotice.WarnOnceAsync(this, s);
            await Navigation.PushAsync(new SessionPage(connect, title, QualityProfiles.BuildSettings(s.Quality),
                SessionStore.BuildPrepare(s), SessionStore.BuildOnClosed(s)));
        }
        finally { _connecting = false; }
    }
}
