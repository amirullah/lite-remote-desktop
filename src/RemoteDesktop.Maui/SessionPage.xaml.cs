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

    // Optional campus-VPN step run before every (re)connect, and cleanup when the page closes.
    private readonly Func<Action<string>, CancellationToken, Task>? _prepare;
    private readonly Action? _onClosed;

    public SessionPage(Func<CancellationToken, Task<ConnectOutcome>> connect, string title, SessionSettings? settings = null,
        Func<Action<string>, CancellationToken, Task>? prepare = null, Action? onClosed = null)
    {
        InitializeComponent();
        _connect = connect;
        _prepare = prepare;
        _onClosed = onClosed;
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
        KeyInput.TextChanged += OnKeyText;
#if ANDROID
        // "Visible password" input type: the keyboard app does no composing/autocorrect/suggestions, so
        // every key arrives as-is (no auto-inserted spaces, no rewrites of the hidden box).
        KeyInput.HandlerChanged += (_, _) =>
        {
            if (KeyInput.Handler?.PlatformView is Android.Widget.EditText et)
                et.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationVisiblePassword |
                               Android.Text.InputTypes.TextFlagNoSuggestions | Android.Text.InputTypes.TextFlagMultiLine;
        };
#endif   // Enter arrives as '\n' (Editor) → KeyMap → VK_RETURN

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
        if (_channel is not null || _connecting) return;
        await ConnectAsync();
    }

    // ---------------- connect / reconnect ----------------

    private const int MaxAutoRetries = 5;
    private bool _connecting;
    private bool _hadSession;    // a connection succeeded at least once on this page
    private int _retry;          // consecutive failed/dropped attempts since the last good session
    private int _generation;     // bumps on every (re)connect so a stale session's "ended" is ignored

    /// <summary>Open a fresh connection (the surface is reused) and start streaming on it.</summary>
    private async Task<bool> ConnectAsync()
    {
        if (_connecting || _cts.IsCancellationRequested) return false;
        _connecting = true;
        ReconnectBtn.IsVisible = false;
        ShowStatus(_retry == 0 ? "Menghubungkan…" : $"Menyambung ulang… ({_retry}/{MaxAutoRetries})");
        try
        {
            ConnectOutcome outcome;
            try
            {
                if (_prepare is not null)
                    await _prepare(msg => MainThread.BeginInvokeOnMainThread(() => { if (!_cts.IsCancellationRequested) ShowStatus(msg); }), _cts.Token);
                outcome = await _connect(_cts.Token);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { outcome = new ConnectOutcome(null, false, ex.Message, null); }
            if (_cts.IsCancellationRequested) { if (outcome.Channel is not null) _ = outcome.Channel.DisposeAsync(); return false; }

            if (!outcome.Ok || outcome.Channel is null)
            {
                if (outcome.Channel is not null) _ = outcome.Channel.DisposeAsync();
                // Never auto-retry the very first connect: a wrong password or a changed host key would
                // only be hammered. Auto-retry is for a session that was live and then dropped.
                if (!_hadSession) { ShowStatus(outcome.Message); ReconnectBtn.IsVisible = true; return false; }
                OnLinkLost(outcome.Message);
                return false;
            }
            _hadSession = true;
            _channel = outcome.Channel;
            ShowStatus("Terautentikasi ✓ — memulai video…");
            TryStart();
            return true;
        }
        finally { _connecting = false; }
    }

    /// <summary>Tear down the dead session and retry with back-off; after the last try, offer the button.</summary>
    private void OnLinkLost(string reason)
    {
        if (_cts.IsCancellationRequested) return;
        TearDownSession();
        _retry++;
        if (_retry > MaxAutoRetries)
        {
            ShowStatus($"Koneksi terputus: {reason}");
            ReconnectBtn.IsVisible = true;
            return;
        }
        int delayS = Math.Min(1 << (_retry - 1), 8);   // 1, 2, 4, 8, 8 s
        ShowStatus($"Koneksi terputus — menyambung ulang dalam {delayS} dtk… ({_retry}/{MaxAutoRetries})");
        ReconnectBtn.IsVisible = true;                  // user may retry right away
        int gen = _generation;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(delayS), () =>
        {
            if (gen == _generation && _channel is null && !_connecting) _ = ConnectAsync();
        });
    }

    private void TearDownSession()
    {
        _generation++;
        _session = null;
        _started = false;
        _gestures.Cancel();
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        if (_channel is not null) { _ = _channel.DisposeAsync(); _channel = null; }
        _lastMoveX = _lastMoveY = ushort.MaxValue;
    }

    private async void Reconnect_Clicked(object? sender, EventArgs e)
    {
        if (_connecting) return;
        TearDownSession();   // also invalidates any pending auto-retry
        _retry = 0;
        await ConnectAsync();
    }

    // ---------------- orientation ----------------

    private bool _orientationLocked;

    /// <summary>Lock the session to the other orientation (landscape ↔ portrait) without needing the
    /// phone's system auto-rotate; restored when leaving the session.</summary>
    private void Rotate_Clicked(object? sender, EventArgs e)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is null) return;
        bool nowLandscape = DeviceDisplay.MainDisplayInfo.Width > DeviceDisplay.MainDisplayInfo.Height;
        activity.RequestedOrientation = nowLandscape
            ? global::Android.Content.PM.ScreenOrientation.SensorPortrait
            : global::Android.Content.PM.ScreenOrientation.SensorLandscape;
        _orientationLocked = true;
#endif
    }

    private void RestoreOrientation()
    {
#if ANDROID
        if (!_orientationLocked) return;
        _orientationLocked = false;
        var activity = Platform.CurrentActivity;
        if (activity is not null) activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Unspecified;
#endif
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
        var session = new ViewerSession(_channel, _decoder);
        _session = session;
        int gen = _generation;
        session.VideoConfigured += (w, h) => Dispatcher.Dispatch(() =>
        {
            if (gen != _generation) return;
            _retry = 0;   // picture is flowing again: the next drop gets the full retry budget
            ReconnectBtn.IsVisible = false;
            _viewport.SetRemote(w, h);
            _viewport.SetView(Screen.PixelWidth, Screen.PixelHeight);
            ApplyViewport();
            ShowStatus($"{w}×{h}");
            // The status is only a hint once the picture is live — get it out of the way.
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () => { if (gen == _generation && _session is not null) StatusLabel.IsVisible = false; });
        });

        _ = Task.Run(async () =>
        {
            string reason;
            try
            {
                await session.RunAsync(_settings, _cts.Token);
                // RunAsync returns normally when the host closes or the link drops (e.g. the phone
                // switched Wi-Fi ↔ mobile data): reconnect instead of leaving a frozen frame.
                reason = "host terputus";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { reason = ex.Message; }
            if (_cts.IsCancellationRequested) return;
            Dispatcher.Dispatch(() => { if (gen == _generation) OnLinkLost(reason); });
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

    private long _echoUntil;   // IME echoes of our own reset arrive a few ms later — never treat them as keys

    private void OnKeyText(object? sender, TextChangedEventArgs e)
    {
        if (_resettingInput) return;
        string newT = e.NewTextValue ?? "";
        if (newT == Sentinel) return;   // already at rest (often the IME re-syncing after our reset)

        // Always diff against the sentinel we left behind, NOT OldTextValue: after we reset the box the
        // keyboard app may push its own late "correction" (e.g. " 0" -> ""), which a diff against
        // OldTextValue reads as Backspace — that erased every character right after it was typed.
        long now = Environment.TickCount64;
        if (newT.Length == 0)
        {
            // Sentinel deleted = a real Backspace, unless it is the echo right after a reset.
            if (now >= _echoUntil) SendVk(KeyMap.Back);
        }
        else
        {
            string added = newT.StartsWith(Sentinel, StringComparison.Ordinal) ? newT[Sentinel.Length..] : newT;
            foreach (char c in added) SendChar(c);
        }
        // Back to the sentinel so the box never fills up and Backspace keeps working.
        _resettingInput = true;
        KeyInput.Text = Sentinel;
        _resettingInput = false;
        _echoUntil = now + 120;
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
        _generation++;          // cancels any pending auto-reconnect
        RestoreOrientation();
        try { _decoder?.Dispose(); } catch { }
        if (_channel is not null) _ = _channel.DisposeAsync();
        try { _onClosed?.Invoke(); } catch { }
    }
}
