using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;

namespace RemoteDesktop.Maui;

public partial class SessionPage : ContentPage
{
    private readonly Func<CancellationToken, Task<ConnectOutcome>> _connect;
    private readonly CancellationTokenSource _cts = new();

    private MessageChannel? _channel;
    private IVideoDecoder? _decoder;
    private ViewerSession? _session;
    private object? _surface;
    private bool _started;

    public SessionPage(Func<CancellationToken, Task<ConnectOutcome>> connect, string title)
    {
        InitializeComponent();
        _connect = connect;
        Title = title;
        Screen.SurfaceReady += OnSurfaceReady;

        // Touch -> mouse: a tap is press+release (a click); a drag streams moves. Coordinates are
        // normalized 0..65535 so they never desync from the remote resolution.
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, e) =>
        {
            if (!Map(e, out var nx, out var ny)) return;
            _ = _session?.SendPointerMoveAsync(nx, ny);
            _ = _session?.SendPointerButtonAsync(MouseButton.Left, true, nx, ny);
        };
        pointer.PointerMoved += (_, e) => { if (Map(e, out var nx, out var ny)) _ = _session?.SendPointerMoveAsync(nx, ny); };
        pointer.PointerReleased += (_, e) =>
        {
            if (!Map(e, out var nx, out var ny)) return;
            _ = _session?.SendPointerButtonAsync(MouseButton.Left, false, nx, ny);
        };
        Screen.GestureRecognizers.Add(pointer);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_channel is not null) return;

        StatusLabel.Text = "Connecting…";
        var outcome = await _connect(_cts.Token);
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
        if (_started)
        {
            // Returned to the foreground with a fresh surface: repoint the running decoder and ask the
            // host for a keyframe so the (blank) new surface fills right away instead of staying black.
#if ANDROID
            (_decoder as AndroidVideoDecoder)?.SetSurface((global::Android.Views.Surface)surface);
#endif
            _ = _session?.RequestKeyFrameAsync();
            return;
        }
        TryStart();
    }

    private void TryStart()
    {
        if (_started || _channel is null || _surface is null) return;
        _started = true;

        _decoder = CreateDecoder(_surface);
        _session = new ViewerSession(_channel, _decoder);
        _session.VideoConfigured += (w, h) => Dispatcher.Dispatch(() => StatusLabel.Text = $"{w}×{h}");

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

    private bool Map(PointerEventArgs e, out ushort nx, out ushort ny)
    {
        nx = ny = 0;
        var p = e.GetPosition(Screen);
        if (p is null || Screen.Width <= 0 || Screen.Height <= 0) return false;
        nx = (ushort)Math.Clamp(p.Value.X / Screen.Width * ushort.MaxValue, 0, ushort.MaxValue);
        ny = (ushort)Math.Clamp(p.Value.Y / Screen.Height * ushort.MaxValue, 0, ushort.MaxValue);
        return true;
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
