using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;

namespace RemoteDesktop.Maui;

public partial class SessionPage : ContentPage
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();

    private MessageChannel? _channel;
    private IVideoDecoder? _decoder;
    private ViewerSession? _session;
    private object? _surface;
    private bool _started;

    public SessionPage(string host, int port, string password)
    {
        InitializeComponent();
        _host = host;
        _port = port;
        _password = password;
        Screen.SurfaceReady += OnSurfaceReady;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_channel is not null) return; // already connected (page re-appear)

        StatusLabel.Text = "Connecting…";
        var outcome = await ViewerConnection.ConnectAsync(_host, _port, _password, _cts.Token);
        if (!outcome.Ok || outcome.Channel is null)
        {
            StatusLabel.Text = outcome.Message;
            return;
        }
        _channel = outcome.Channel;
        StatusLabel.Text = "Authenticated ✓ — starting video…";
        TryStart();
    }

    private void OnSurfaceReady(object surface)
    {
        _surface = surface;
        TryStart();
    }

    // Both the authenticated channel and the render surface must be ready before we start decoding.
    private void TryStart()
    {
        if (_started || _channel is null || _surface is null) return;
        _started = true;

        _decoder = CreateDecoder(_surface);
        _session = new ViewerSession(_channel, _decoder);
        _session.VideoConfigured += (w, h) =>
            Dispatcher.Dispatch(() => StatusLabel.Text = $"{w}×{h}");

        _ = Task.Run(async () =>
        {
            try
            {
                await _session.RunAsync(new SessionSettings { PreferredCodec = VideoCodec.H264 }, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Dispatch(() => StatusLabel.Text = "Session ended: " + ex.Message);
            }
        });
    }

    private static IVideoDecoder CreateDecoder(object surface)
    {
#if ANDROID
        return new AndroidVideoDecoder((global::Android.Views.Surface)surface);
#else
        throw new PlatformNotSupportedException("No video decoder for this platform yet.");
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cts.Cancel();
        try { _decoder?.Dispose(); } catch { }
        if (_channel is not null) _ = _channel.DisposeAsync();
    }
}
