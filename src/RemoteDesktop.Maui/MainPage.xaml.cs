using RemoteDesktop.Maui.Services;

namespace RemoteDesktop.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        var host = (HostEntry.Text ?? "").Trim();
        if (host.Length == 0) { StatusLabel.Text = "Enter a host."; return; }
        if (!int.TryParse(PortEntry.Text, out var port) || port is < 1 or > 65535) port = 7443;

        SetBusy(true);
        StatusLabel.Text = "Connecting…";
        FingerprintLabel.Text = "";
        try
        {
            var result = await HostConnection.HelloConnectAsync(host, port, PasswordEntry.Text ?? "");
            StatusLabel.Text = result.Message;
            if (result.Fingerprint is not null)
                FingerprintLabel.Text = "Host key: " + result.Fingerprint;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.IsRunning = busy;
        Busy.IsVisible = busy;
        ConnectButton.IsEnabled = !busy;
    }
}
