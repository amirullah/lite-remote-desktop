using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Host.Services;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Host;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppPaths.EnsureRoot();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
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
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "LiteRemote Host",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        EnsureAuthConfigured();
        StartServer();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show status / ID / fingerprint", null, (_, _) => ShowStatus());
        menu.Items.Add("Set access password…", null, (_, _) => SetPassword());
        menu.Items.Add("Set up ID access (relay)…", null, (_, _) => ConfigureRelay());
        menu.Items.Add("Configure Google login…", null, (_, _) => ConfigureGoogle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
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
        var fp = _server?.Fingerprint ?? "(server not running)";
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
            $"LiteRemote Host\n\n" +
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
    }

    private void ConfigureGoogle()
    {
        var clientId = InputDialog.Show("Google login", "Google OAuth client id:", masked: false, _config.GoogleClientId);
        if (string.IsNullOrEmpty(clientId)) return;
        var emails = InputDialog.Show("Google login",
            "Allowed emails (comma-separated):", masked: false,
            string.Join(", ", _config.AllowedGoogleEmails));

        _config.AllowGoogle = true;
        _config.GoogleClientId = clientId.Trim();
        _config.AllowedGoogleEmails = (emails ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _config.Save();
        RestartServer();
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
