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

        StatusLabel.Text = "";
        await Navigation.PushAsync(new SessionPage(host, port, PasswordEntry.Text ?? ""));
    }
}
