using RemoteDesktop.Maui.Controls;
using RemoteDesktop.Maui.Input;
using RemoteDesktop.Maui.Services;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;

namespace RemoteDesktop.Maui;

public partial class SessionPage : ContentPage, IRemoteInputSink
{
    private readonly Func<CancellationToken, Task<ConnectOutcome>> _connect;
    private readonly CancellationTokenSource _cts = new();

    private MessageChannel? _channel;
    private IVideoDecoder? _decoder;
    private ViewerSession? _session;
    private object? _surface;
    private bool _started;

    // Picture placement (aspect-correct letterbox + pinch zoom/pan) and touch → mouse gestures.
    private readonly Viewport _viewport = new();
    private readonly TouchGestures _gestures;

    // Keyboard: a hidden Entry holds one sentinel char so Backspace on an "empty" box is still seen.
    private const string Sentinel = " ";
    private bool _resettingInput;
    private bool _ctrl, _alt, _shift;   // sticky modifiers for the NEXT key

    private readonly SessionSettings _settings;
    private ushort _lastMoveX = ushort.MaxValue, _lastMoveY = ushort.MaxValue;

    public SessionPage(Func<CancellationToken, Task<ConnectOutcome>> connect, string title, SessionSettings? settings = null)
    {
        InitializeComponent();
        _connect = connect;
        _settings = settings ?? new SessionSettings { PreferredCodec = VideoCodec.H264, MaxFps = 30, TargetFps = 30, Quality = 65 };
        Title = title;
        _gestures = new TouchGestures(_viewport, this, (float)DeviceDisplay.MainDisplayInfo.Density);

        Screen.SurfaceReady += OnSurfaceReady;
        Screen.Touch += OnTouch;
        Screen.PixelSizeChanged += () => { _viewport.SetView(Screen.PixelWidth, Screen.PixelHeight); ApplyViewport(); };

#if MACCATALYST
        // Mac uses a real mouse: direct mapping via the pointer recognizer (no touch gestures).
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, e) => { if (MapPointer(e, out var x, out var y)) { Move(x, y); Button(false, true, x, y); } };
        pointer.PointerMoved += (_, e) => { if (MapPointer(e, out var x, out var y)) Move(x, y); };
        pointer.PointerReleased += (_, e) => { if (MapPointer(e, out var x, out var y)) Button(false, false, x, y); };
        Screen.GestureRecognizers.Add(pointer);
#endif

        KeyInput.Text = Sentinel;
        KeyInput.TextChanged += OnKeyText;   // Enter arrives as '\n' (Editor) → KeyMap → VK_RETURN

        // Long-press detection needs a clock even when the finger doesn't move.
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), () =>
        {
            if (_cts.IsCancellationRequested) return false;
            _gestures.Tick(NowMs());
            return true;
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_channel is not null) return;

        ShowStatus("Menghubungkan…");
        var outcome = await _connect(_cts.Token);
        if (!outcome.Ok || outcome.Channel is null)
        {
            ShowStatus(outcome.Message);
            return;
        }
        _channel = outcome.Channel;
        ShowStatus("Terautentikasi ✓ — memulai video…");
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
            ApplyViewport();
            return;
        }
        TryStart();
    }

    private void TryStart()
    {
        if (_started || _channel is null || _surface is null) return;
        _started = true;

        _decoder = CreateDecoder(_surface);
#if ANDROID
        // After a dropped/garbled frame the decoder resyncs on a fresh keyframe — let it ask the host.
        if (_decoder is AndroidVideoDecoder ad) ad.KeyFrameNeeded = () => _ = _session?.RequestKeyFrameAsync();
#endif
        _session = new ViewerSession(_channel, _decoder);
        _session.VideoConfigured += (w, h) => Dispatcher.Dispatch(() =>
        {
            _viewport.SetRemote(w, h);
            _viewport.SetView(Screen.PixelWidth, Screen.PixelHeight);
            ApplyViewport();
            ShowStatus($"{w}×{h}");
            // The status is only a hint once the picture is live — get it out of the way.
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () => { if (_session is not null) StatusLabel.IsVisible = false; });
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _session.RunAsync(_settings, _cts.Token);
                // RunAsync returns normally when the host closes or the link drops: say so instead of
                // leaving a frozen frame with no explanation.
                if (!_cts.IsCancellationRequested)
                    Dispatcher.Dispatch(() => { _session = null; ShowStatus("Sesi berakhir (host terputus)"); });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Dispatch(() => { _session = null; ShowStatus("Sesi berakhir: " + ex.Message); });
            }
        });
    }

    private void ShowStatus(string text) { StatusLabel.Text = text; StatusLabel.IsVisible = true; }

    // ---------------- picture placement ----------------

    private void ApplyViewport()
    {
        if (!_viewport.IsReady) return;
        Screen.SetContentRect(_viewport.Left, _viewport.Top, _viewport.ContentWidth, _viewport.ContentHeight);
        FitBtn.IsVisible = _viewport.Zoom > 1.01f;
    }

    private void Fit_Clicked(object? sender, EventArgs e) { _viewport.Reset(); ApplyViewport(); }

    // ---------------- touch → mouse ----------------

    private void OnTouch(TouchFrame f)
    {
        switch (f.Action)
        {
            case TouchAction.Down: _gestures.Down(f.Count, f.X, f.Y, f.X2, f.Y2, f.TimeMs); break;
            case TouchAction.Move: _gestures.Move(f.Count, f.X, f.Y, f.X2, f.Y2, f.TimeMs); break;
            case TouchAction.Up: _gestures.Up(f.Count, f.X, f.Y, f.TimeMs); break;
            case TouchAction.Cancel: _gestures.Cancel(); break;
        }
    }

    private static long NowMs()
    {
#if ANDROID
        return global::Android.OS.SystemClock.UptimeMillis();   // same clock as MotionEvent.EventTime
#else
        return Environment.TickCount64;
#endif
    }

    // IRemoteInputSink
    public void Move(float nx, float ny)
    {
        // Touch reports far more often than the pointer actually moves on the remote — skip repeats.
        ushort x = Viewport.ToWire(nx), y = Viewport.ToWire(ny);
        if (x == _lastMoveX && y == _lastMoveY) return;
        _lastMoveX = x; _lastMoveY = y;
        _ = _session?.SendPointerMoveAsync(x, y);
    }
    public void Button(bool right, bool down, float nx, float ny)
        => _ = _session?.SendPointerButtonAsync(right ? MouseButton.Right : MouseButton.Left, down, Viewport.ToWire(nx), Viewport.ToWire(ny));
    public void Wheel(int deltaY, float nx, float ny)
        => _ = _session?.SendWheelAsync(0, (short)deltaY, Viewport.ToWire(nx), Viewport.ToWire(ny));
    public void ViewportChanged() => ApplyViewport();

#if MACCATALYST
    private bool MapPointer(PointerEventArgs e, out float nx, out float ny)
    {
        nx = ny = 0;
        var p = e.GetPosition(Screen);
        if (p is null || Screen.Width <= 0 || Screen.Height <= 0) return false;
        // The Mac layer letterboxes (ResizeAspect): map into the fitted picture, not the whole view.
        _viewport.SetView((float)Screen.Width, (float)Screen.Height);
        if (!_viewport.IsReady)
        {
            nx = (float)Math.Clamp(p.Value.X / Screen.Width, 0, 1);
            ny = (float)Math.Clamp(p.Value.Y / Screen.Height, 0, 1);
            return true;
        }
        _viewport.TryViewToRemote((float)p.Value.X, (float)p.Value.Y, out nx, out ny);   // clamps to the edge outside
        return true;
    }
#endif

    // ---------------- keyboard ----------------

    // ⌨ always (re)opens the keyboard — even if the user dismissed it with Android Back while the keys
    // bar stayed up. The bar's own "⌄" closes both.
    private void Keyboard_Clicked(object? sender, EventArgs e)
    {
        KeysBar.IsVisible = true;
        // Unfocus first so Focus() is a real focus change and the IME is shown again.
        KeyInput.Unfocus();
        KeepKeyboardOpen();
    }

    private void HideKeys_Clicked(object? sender, EventArgs e)
    {
        KeysBar.IsVisible = false;
        KeyInput.Unfocus();
        ClearModifiers();
    }

    private void KeepKeyboardOpen()
    {
        if (!KeysBar.IsVisible) return;
        Dispatcher.Dispatch(() => KeyInput.Focus());
    }

    private void OnKeyText(object? sender, TextChangedEventArgs e)
    {
        if (_resettingInput) return;
        string oldT = e.OldTextValue ?? "", newT = e.NewTextValue ?? "";
        if (newT.Length < oldT.Length)
        {
            for (int i = 0; i < oldT.Length - newT.Length; i++) SendVk(KeyMap.Back);
        }
        else
        {
            int common = 0;
            while (common < oldT.Length && common < newT.Length && oldT[common] == newT[common]) common++;
            foreach (char c in newT[common..]) SendChar(c);
        }
        // Back to the sentinel so the box never fills up and Backspace keeps working.
        _resettingInput = true;
        KeyInput.Text = Sentinel;
        _resettingInput = false;
    }

    private void SendChar(char c)
    {
        if (!KeyMap.TryMap(c, out var vk, out var shift)) return;   // not typeable on a US layout (e.g. emoji)
        SendVk(vk, shift);
    }

    private void Key_Clicked(object? sender, EventArgs e)
    {
        if (sender is Button b && ushort.TryParse(b.CommandParameter?.ToString(), out var vk)) SendVk(vk);
        KeepKeyboardOpen();
    }

    private void Mod_Clicked(object? sender, EventArgs e)
    {
        switch ((sender as Button)?.CommandParameter?.ToString())
        {
            case "ctrl": _ctrl = !_ctrl; break;
            case "alt": _alt = !_alt; break;
            case "shift": _shift = !_shift; break;
        }
        UpdateModifierButtons();
        KeepKeyboardOpen();
    }

    /// <summary>Press <paramref name="vk"/> with any sticky modifiers (+ Shift if the char needs it).</summary>
    private void SendVk(ushort vk, bool needShift = false)
    {
        var s = _session;
        if (s is null) return;
        bool shift = needShift || _shift;
        if (_ctrl) _ = s.SendKeyAsync(KeyMap.Ctrl, true);
        if (_alt) _ = s.SendKeyAsync(KeyMap.Alt, true);
        if (shift) _ = s.SendKeyAsync(KeyMap.Shift, true);
        bool ext = KeyMap.IsExtended(vk);
        _ = s.SendKeyAsync(vk, true, ext);
        _ = s.SendKeyAsync(vk, false, ext);
        if (shift) _ = s.SendKeyAsync(KeyMap.Shift, false);
        if (_alt) _ = s.SendKeyAsync(KeyMap.Alt, false);
        if (_ctrl) _ = s.SendKeyAsync(KeyMap.Ctrl, false);
        ClearModifiers();
    }

    private void ClearModifiers()
    {
        if (!_ctrl && !_alt && !_shift) return;
        _ctrl = _alt = _shift = false;
        UpdateModifierButtons();
    }

    private void UpdateModifierButtons()
    {
        var on = Color.FromArgb("#2F7CF6"); var off = Color.FromArgb("#2B3445");
        CtrlBtn.BackgroundColor = _ctrl ? on : off;
        AltBtn.BackgroundColor = _alt ? on : off;
        ShiftBtn.BackgroundColor = _shift ? on : off;
    }

    // ---------------- lifecycle ----------------

    private async void Close_Clicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    private static IVideoDecoder CreateDecoder(object surface)
    {
#if ANDROID
        return new AndroidVideoDecoder((global::Android.Views.Surface)surface);
#elif MACCATALYST
        return new MacVideoDecoder((global::AVFoundation.AVSampleBufferDisplayLayer)surface);
#else
        throw new PlatformNotSupportedException("No video decoder for this platform yet.");
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _gestures.Cancel();
        _cts.Cancel();
        try { _decoder?.Dispose(); } catch { }
        if (_channel is not null) _ = _channel.DisposeAsync();
    }
}
