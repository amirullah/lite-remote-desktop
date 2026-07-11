using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Relay;

namespace RemoteDesktop.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? "";
        var id = RelayProtocol.NormalizeId(IdEntry.Text ?? "");

        Func<CancellationToken, Task<ConnectOutcome>> connect;
        string title;

        if (id.Length > 0)
        {
            // ID mode via relay.
            if (id.Length != 9) { StatusLabel.Text = "ID must be 9 digits."; return; }
            var (relayHost, relayPort) = SplitHostPort(RelayEntry.Text, RelayProtocol.DefaultPort);
            if (relayHost.Length == 0) { StatusLabel.Text = "Enter the relay address."; return; }
            connect = ct => ViewerConnection.ConnectViaRelayAsync(relayHost, relayPort, id, password, ct);
            title = RelayProtocol.FormatId(id);
        }
        else
        {
            // Direct host:port mode.
            var host = (HostEntry.Text ?? "").Trim();
            if (host.Length == 0) { StatusLabel.Text = "Enter a host or an ID."; return; }
            if (!int.TryParse(PortEntry.Text, out var port) || port is < 1 or > 65535) port = 7443;
            connect = ct => ViewerConnection.ConnectAsync(host, port, password, ct);
            title = $"{host}:{port}";
        }

        StatusLabel.Text = "";
        await Navigation.PushAsync(new SessionPage(connect, title));
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
