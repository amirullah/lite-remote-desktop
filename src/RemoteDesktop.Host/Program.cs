using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Host.Services;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppPaths.EnsureRoot();

        // Headless CLI helpers so scripts/automation can configure the host without the tray UI.
        //   LiteRemoteHost --set-password <pwd>   set access password and exit
        //   LiteRemoteHost --show-id              print the relay ID (if configured) and exit
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set-password" && i + 1 < args.Length)
            {
                var cfg = HostConfig.Load();
                cfg.AllowPassword = true;
                cfg.SetPassword(args[i + 1]);
                cfg.Save();
                Console.WriteLine("Access password set.");
                return;
            }
            if (args[i] == "--show-id")
            {
                var cfg = HostConfig.Load();
                Console.WriteLine(string.IsNullOrEmpty(cfg.HostId) ? "(no ID — set up relay access)" : cfg.HostId);
                return;
            }
            if (args[i] == "--bench-capture")
            {
                CaptureBench.Run();
                return;
            }
            if (args[i] == "--h264-probe")
            {
                H264Probe.Run();
                return;
            }
            if (args[i] == "--bench-h264")
            {
                H264Bench.Run();
                return;
            }
            // Login-screen support (runs the host as a SYSTEM agent that can reach the secure desktop).
            if (args[i] == "--agent") { Service.AgentHost.RunAgent(); return; }
            if (args[i] == "--daemon") { Service.AgentHost.RunDaemon(); return; }
            // Optional next arg: the interactive user's config folder (passed by the tray / installer).
            if (args[i] == "--install-login") { AttachConsoleForCli(); Service.LoginSupport.Install(i + 1 < args.Length ? args[i + 1] : null); return; }
            if (args[i] == "--sync-login-config") { AttachConsoleForCli(); Service.LoginSupport.SyncConfig(i + 1 < args.Length ? args[i + 1] : null); return; }
            if (args[i] == "--uninstall-login") { AttachConsoleForCli(); Service.LoginSupport.Uninstall(); return; }
        }

        // One host per machine: a second instance would fight over port 7443 and confuse the tray.
        using var single = new Mutex(initiallyOwned: true, @"Global\LiteRemoteHost-single", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LiteRemote Host is already running (check the tray icons).",
                "LiteRemote Host", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }

    // WinExe has no console; attach to the caller's so CLI helpers can print.
    private static void AttachConsoleForCli() { try { AttachConsole(-1); } catch { } }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}

/// <summary>
/// The host runs headless in the tray on the controlled PC. The menu exposes the one thing a user
/// must do out-of-band — read the certificate fingerprint to the person connecting — plus password
/// and Google-login configuration.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private HostConfig _config;
    private HostServer? _server;

    public TrayContext()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        _log = _loggerFactory.CreateLogger("Host");
        _config = HostConfig.Load();

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "LiteRemote Host",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        // When login-screen support is installed, a SYSTEM daemon already serves on the port (and can
        // reach the login/secure desktop). The interactive tray must NOT also bind it — that is the
        // "only one usage of each socket address" error. Step aside and run as a settings tray only.
        if (Service.LoginSupport.IsInstalled())
        {
            _tray.Text = "LiteRemote Host — login-support mode";
            _tray.ShowBalloonTip(5000, "LiteRemote Host",
                "Login-support is active: a background service handles connections and can show the " +
                "Windows login screen. This tray is for settings only.", ToolTipIcon.Info);
            EnsureAuthConfigured();   // a password set here is pushed to the service (SetPassword syncs)
        }
        else
        {
            EnsureAuthConfigured();
            StartServer();
        }

        // The tray is the part of LiteRemote that is always running, so it is the one that notices
        // new releases (the viewer only checks while it is open). Clicking the notice opens the viewer,
        // whose banner downloads, verifies and installs the update.
        _tray.BalloonTipClicked += (_, _) => { if (_updateNoticeShown) OpenViewerForUpdate(); };
        _updateTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;   // first check 30 s after start, then every 6 h
            await CheckForUpdateAsync();
        };
        _updateTimer.Start();
    }

    // ---------------- update notice ----------------

    private readonly System.Windows.Forms.Timer _updateTimer;
    private bool _updateNoticeShown;
    private string? _notifiedVersion;

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteRemote-Host");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync("https://api.github.com/repos/amirullah/lite-remote-desktop/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True) return;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True) return;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var latest)) return;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            var a = typeof(TrayContext).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var current = new Version(a.Major, a.Minor, Math.Max(0, a.Build));
            if (latest <= current || latest.ToString(3) == _notifiedVersion) return;
            _notifiedVersion = latest.ToString(3);
            _updateNoticeShown = true;
            _tray.Text = $"LiteRemote Host — pembaruan v{_notifiedVersion} tersedia";
            _tray.ShowBalloonTip(10000, "Pembaruan LiteRemote tersedia",
                $"Versi v{_notifiedVersion} sudah rilis (terpasang: v{current.ToString(3)}). Klik untuk memperbarui.", ToolTipIcon.Info);
        }
        catch (Exception ex) { _log.LogDebug("Update check failed: {Msg}", ex.Message); }   // offline: stay quiet
    }

    private void OpenViewerForUpdate()
    {
        try
        {
            var viewer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "client", "LiteRemote.exe"));
            if (File.Exists(viewer))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(viewer) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/amirullah/lite-remote-desktop/releases/latest") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.LogWarning("Could not open the viewer for the update: {Msg}", ex.Message); }
    }

    private static Icon LoadAppIcon()
    {
        // The exe carries the LiteRemote icon (ApplicationIcon in the csproj); extract it for the
        // tray so both stay in sync. Fall back to the stock icon if extraction ever fails.
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show status / ID / fingerprint", null, (_, _) => ShowStatus());
        menu.Items.Add("Set access password…", null, (_, _) => SetPassword());
        menu.Items.Add("Set up ID access (relay)…", null, (_, _) => ConfigureRelay());
        menu.Items.Add("Configure Google login…", null, (_, _) => ConfigureGoogle());
        menu.Items.Add(new ToolStripSeparator());
        _loginItem = new ToolStripMenuItem("", null, (_, _) => ToggleLoginSupport());
        menu.Items.Add(_loginItem);
        menu.Opening += (_, _) => _loginItem.Text = Service.LoginSupport.IsInstalled()
            ? "✓ Akses layar login Windows aktif — nonaktifkan…"
            : "Aktifkan akses layar login Windows (buka kunci dari jauh)…";
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    // ---------------- login-screen support (no terminal needed) ----------------

    private ToolStripMenuItem _loginItem = null!;
    private static string UserConfigDir => Path.GetDirectoryName(AppPaths.HostCertificate)!;

    /// <summary>Run this exe elevated (one UAC prompt) with the given args; true if it ran and exited 0.</summary>
    private static async Task<bool> RunElevatedAsync(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC declined
    }

    private async void ToggleLoginSupport()
    {
        bool installed = Service.LoginSupport.IsInstalled();
        if (!installed)
        {
            var ok = MessageBox.Show(
                "Aktifkan akses layar login Windows?\n\n" +
                "Dengan ini Anda bisa membuka kunci PC (mengetik PIN/password Windows) dan masuk dari jauh, " +
                "juga setelah PC restart. LiteRemote akan berjalan sebagai layanan sistem.\n\n" +
                "Windows akan meminta izin administrator satu kali.",
                "LiteRemote Host", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;
            EnsureAuthConfigured();
            // Free port 7443 for the service before it starts.
            if (_server != null) { await _server.DisposeAsync(); _server = null; }
            if (await RunElevatedAsync($"--install-login \"{UserConfigDir}\"") && Service.LoginSupport.IsInstalled())
            {
                _tray.Text = "LiteRemote Host — login-support mode";
                _tray.ShowBalloonTip(5000, "LiteRemote Host",
                    "Akses layar login aktif. PC bisa dibuka kuncinya dari jauh.", ToolTipIcon.Info);
            }
            else
            {
                StartServer();   // declined/failed: keep serving as before
                MessageBox.Show("Akses layar login tidak diaktifkan (izin administrator ditolak atau gagal).",
                    "LiteRemote Host", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            var ok = MessageBox.Show(
                "Nonaktifkan akses layar login Windows?\n\nSetelah ini PC yang terkunci tidak bisa dibuka dari jauh.",
                "LiteRemote Host", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;
            if (await RunElevatedAsync("--uninstall-login") && !Service.LoginSupport.IsInstalled())
            {
                await Task.Delay(1000);   // let the SYSTEM copies release the port
                _tray.Text = "LiteRemote Host";
                StartServer();
            }
        }
    }

    /// <summary>The SYSTEM service keeps its own copy of the settings: after a change, push it (one UAC).</summary>
    private async Task SyncLoginConfigAsync()
    {
        if (!Service.LoginSupport.IsInstalled()) return;
        if (!await RunElevatedAsync($"--sync-login-config \"{UserConfigDir}\""))
            MessageBox.Show(
                "Pengaturan tersimpan, tetapi layanan layar login belum diperbarui (izin administrator ditolak).\n" +
                "Koneksi masih memakai pengaturan lama sampai Anda mengulanginya.",
                "LiteRemote Host", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void EnsureAuthConfigured()
    {
        if ((_config.AllowPassword && _config.HasPassword) ||
            (_config.AllowGoogle && !string.IsNullOrEmpty(_config.GoogleClientId)))
            return;

        MessageBox.Show(
            "No access method is configured yet. Set an access password so clients can connect.",
            "LiteRemote Host", MessageBoxButtons.OK, MessageBoxIcon.Information);
        SetPassword();
    }

    private void StartServer()
    {
        // Never bind the port while the login-support service owns it (avoids the socket-in-use error).
        if (Service.LoginSupport.IsInstalled()) return;
        try
        {
            _server = new HostServer(_config, _log);
            _ = _server.StartAsync();
            _tray.ShowBalloonTip(3000, "LiteRemote Host",
                $"Listening on port {_config.Port}. Double-click the tray icon for the fingerprint.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start server.");
            MessageBox.Show($"Failed to start: {ex.Message}", "LiteRemote Host",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void RestartServer()
    {
        if (_server != null) await _server.DisposeAsync();
        StartServer();
    }

    private void ShowStatus()
    {
        bool loginMode = Service.LoginSupport.IsInstalled();
        var fp = _server?.Fingerprint ?? (loginMode
            ? "(dilayani layanan layar login — sama dengan sidik jari yang sudah tersimpan di viewer)"
            : "(server not running)");
        var av = typeof(TrayContext).Assembly.GetName().Version;
        string version = av is null ? "?" : $"{av.Major}.{av.Minor}.{Math.Max(0, av.Build)}";
        var methods = new List<string>();
        if (_config.AllowPassword && _config.HasPassword) methods.Add("Password");
        if (_config.AllowGoogle && !string.IsNullOrEmpty(_config.GoogleClientId)) methods.Add("Google");

        string idLine;
        if (!string.IsNullOrWhiteSpace(_config.RelayAddress) && !string.IsNullOrEmpty(_config.HostId))
        {
            var online = _server?.RelayOnline == true ? "online" : "connecting…";
            idLine = $"Your ID: {Shared.Relay.RelayProtocol.FormatId(_config.HostId)}  ({online} via {_config.RelayAddress})\n";
        }
        else
        {
            idLine = "Your ID: (not set up — use \"Set up ID access\")\n";
        }

        MessageBox.Show(
            $"LiteRemote Host v{version}\n\n" +
            $"Akses layar login Windows: {(loginMode ? "aktif" : "tidak aktif (aktifkan lewat menu tray)")}\n" +
            idLine +
            $"Port: {_config.Port}\n" +
            $"Bind: {_config.BindAddress}\n" +
            $"Auth: {(methods.Count == 0 ? "NONE" : string.Join(", ", methods))}\n\n" +
            $"Certificate fingerprint (share this with the client so they can verify the host):\n\n{fp}",
            "LiteRemote Host — Status", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ConfigureRelay()
    {
        var relay = InputDialog.Show("ID access (relay)",
            "Relay server address (host:port). Leave empty to disable ID access:",
            masked: false, _config.RelayAddress);
        if (relay is null) return; // cancelled

        _config.RelayAddress = relay.Trim();
        if (_config.RelayAddress.Length > 0) _config.EnsureIdentity();
        _config.Save();
        RestartServer();
        _ = SyncLoginConfigAsync();

        var msg = _config.RelayAddress.Length == 0
            ? "ID access disabled."
            : $"ID access enabled.\n\nYour ID: {Shared.Relay.RelayProtocol.FormatId(_config.HostId)}\n\n" +
              "Share this ID (and the access password) with whoever connects.";
        MessageBox.Show(msg, "LiteRemote Host", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetPassword()
    {
        var pwd = InputDialog.Show("Set access password", "Enter a strong access password:", masked: true);
        if (string.IsNullOrEmpty(pwd)) return;
        _config.AllowPassword = true;
        _config.SetPassword(pwd);
        _config.Save();
        _tray.ShowBalloonTip(2000, "LiteRemote Host", "Access password updated.", ToolTipIcon.Info);
        _ = SyncLoginConfigAsync();
    }

    private void ConfigureGoogle()
    {
        var clientId = InputDialog.Show("Google login", "Google OAuth client id:", masked: false, _config.GoogleClientId);
        if (string.IsNullOrEmpty(clientId)) return;
        var emails = InputDialog.Show("Google login",
            "Allowed emails (comma-separated):", masked: false,
            string.Join(", ", _config.AllowedGoogleEmails));
        // Cancel returns null; treat it as abort instead of wiping the allow-list.
        if (emails is null) return;

        _config.AllowGoogle = true;
        _config.GoogleClientId = clientId.Trim();
        _config.AllowedGoogleEmails = (emails ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _config.Save();
        RestartServer();
        _ = SyncLoginConfigAsync();
        _tray.ShowBalloonTip(2000, "LiteRemote Host", "Google login configured.", ToolTipIcon.Info);
    }

    private async void ExitApp()
    {
        _tray.Visible = false;
        if (_server != null) await _server.DisposeAsync();
        _loggerFactory.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
