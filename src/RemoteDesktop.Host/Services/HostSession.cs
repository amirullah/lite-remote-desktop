using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Host.Capture;
using RemoteDesktop.Host.Input;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;
// WinForms projects inject a global `using System.Windows.Forms;`, whose Message type collides
// with our protocol Message — pin the alias to the protocol type.
using Message = RemoteDesktop.Shared.Protocol.Message;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Owns one authenticated client for its lifetime: runs the auth handshake, then pumps captured
/// frames out and input/clipboard/settings in. Capture+encode run on a dedicated thread so the
/// async message loop is never blocked by a slow frame.
/// </summary>
public sealed class HostSession
{
    private readonly MessageChannel _channel;
    private readonly HostConfig _config;
    private readonly ILogger _log;
    private readonly GoogleIdTokenVerifier? _google;

    private volatile SessionSettings _settings = new();
    private volatile bool _keyFrameRequested = true;
    private volatile bool _running = true;
    private volatile bool _inputActivity;
    private volatile bool _forceGdi; // set when DXGI proves slower than GDI (virtual GPUs)
    private Thread? _captureThread;
    private volatile IReadOnlyList<DisplayInfo> _displays = Array.Empty<DisplayInfo>();
    private int _gdiTargetW, _gdiTargetH; // last source-scale target pushed to the GDI capturer

    private IScreenCapture? _capture;
    private InputInjector? _injector;
    private AdaptiveController? _adaptive;
    private ClipboardService? _clipboard;
    private HostPrivacyService? _privacy;

    // send-rate accounting for the adaptive controller + status bar
    private long _bytesThisSecond;
    private int _framesThisSecond;
    private long _secondStartTicks;
    private double _measuredMbps;
    private int _measuredFps;

    public HostSession(MessageChannel channel, HostConfig config, ILogger log)
    {
        _channel = channel;
        _config = config;
        _log = log;
        if (config.AllowGoogle && !string.IsNullOrEmpty(config.GoogleClientId))
            _google = new GoogleIdTokenVerifier(config.GoogleClientId);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!await AuthenticateAsync(ct).ConfigureAwait(false))
            {
                _log.LogWarning("Authentication failed; dropping client.");
                return;
            }

            // Advertise monitors so the client can offer a display picker.
            var displays = EnumerateDisplays();
            await _channel.SendAsync(PayloadCodec.DisplayList(displays), ct).ConfigureAwait(false);

            _clipboard = new ClipboardService();
            _clipboard.ClipboardChanged += OnHostClipboardChanged;
            _privacy = new HostPrivacyService();

            await MessageLoopAsync(displays, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session terminated with error.");
        }
        finally
        {
            _running = false;
            // The capture loop owns every capture object: disposing one here while a capture call
            // is still in flight on the pipeline thread crashes the whole process inside the
            // driver (observed with DXGI duplication). Wait for the loop to wind down instead —
            // it drains its in-flight capture and disposes everything on its way out.
            if (_captureThread != null)
            {
                if (!_captureThread.Join(3000))
                    _log.LogWarning("Capture thread did not exit in time; leaving cleanup to process teardown.");
            }
            else
            {
                _capture?.Dispose();
            }
            if (_clipboard != null) { _clipboard.ClipboardChanged -= OnHostClipboardChanged; _clipboard.Dispose(); }
            _privacy?.Dispose();
        }
    }

    // ---------------- authentication ----------------

    private async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        var methods = AuthMethod.None;
        if (_config.AllowPassword && _config.HasPassword) methods |= AuthMethod.Password;
        if (_config.AllowGoogle && _google != null) methods |= AuthMethod.Google;
        if (methods == AuthMethod.None)
        {
            _log.LogError("No auth method configured — refusing all clients. Set a password or Google login.");
            await _channel.SendAsync(AuthProtocol.Result(new AuthResultData(false, "Host has no auth configured", "")), ct);
            return false;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await _channel.SendAsync(AuthProtocol.Request(new AuthRequestData(methods, nonce)), ct).ConfigureAwait(false);

        // Give the client a bounded window to respond.
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(30));

        await foreach (var msg in _channel.Inbound.ReadAllAsync(authCts.Token).ConfigureAwait(false))
        {
            if (msg.Type != MessageType.AuthResponse) continue;
            var resp = AuthProtocol.ReadResponse(msg.Span);
            bool ok = resp.Method switch
            {
                AuthMethod.Password => _config.HasPassword && PasswordHasher.Verify(resp.Secret, _config.PasswordHash!),
                AuthMethod.Google => await VerifyGoogleAsync(resp.Secret, ct).ConfigureAwait(false),
                _ => false,
            };

            var token = ok ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : "";
            await _channel.SendAsync(
                AuthProtocol.Result(new AuthResultData(ok, ok ? "ok" : "Invalid credentials", token)), ct)
                .ConfigureAwait(false);
            return ok;
        }
        return false;
    }

    private async Task<bool> VerifyGoogleAsync(string idToken, CancellationToken ct)
    {
        if (_google is null) return false;
        var id = await _google.VerifyAsync(idToken, ct).ConfigureAwait(false);
        if (id is null || !id.EmailVerified) return false;
        bool allowed = _config.AllowedGoogleEmails.Any(e => string.Equals(e, id.Email, StringComparison.OrdinalIgnoreCase));
        if (!allowed) _log.LogWarning("Google login for {Email} rejected: not in allow-list.", id.Email);
        return allowed;
    }

    // ---------------- main loop ----------------

    private async Task MessageLoopAsync(IReadOnlyList<DisplayInfo> displays, CancellationToken ct)
    {
        Thread? captureThread = null;

        await foreach (var msg in _channel.Inbound.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (msg.Type)
            {
                case MessageType.SettingsUpdate:
                    var newSettings = PayloadCodec.ReadSettings(msg.Span);
                    ApplySettings(newSettings, displays);
                    _adaptive?.UpdateSettings(newSettings);
                    // Start the capture thread on the first settings message. It creates and swaps
                    // capture objects itself — single-threaded ownership, no cross-thread disposal.
                    if (captureThread is null)
                    {
                        captureThread = new Thread(() => CaptureLoop(ct)) { IsBackground = true, Name = "CaptureLoop" };
                        _captureThread = captureThread;
                        captureThread.Start();
                    }
                    break;

                case MessageType.KeyFrameRequest:
                    _keyFrameRequested = true;
                    break;

                // Remote input is always injected; "lock host input" blocks the *local* user instead
                // (handled by HostPrivacyService), so these must not be gated on LockHostInput.
                case MessageType.MouseMove:
                    _inputActivity = true;
                    _injector?.MouseMove(PayloadCodec.ReadMouseMove(msg.Span));
                    break;
                case MessageType.MouseButton:
                    _inputActivity = true;
                    _injector?.MouseButton(PayloadCodec.ReadMouseButton(msg.Span));
                    break;
                case MessageType.MouseWheel:
                    _inputActivity = true;
                    _injector?.MouseWheel(PayloadCodec.ReadMouseWheel(msg.Span));
                    break;
                case MessageType.KeyEvent:
                    _inputActivity = true;
                    _injector?.Key(PayloadCodec.ReadKey(msg.Span));
                    break;

                case MessageType.ClipboardUpdate:
                    if (_settings.ClipboardSync) _clipboard?.SetClipboard(ClipboardCodec.Decode(msg.Span));
                    break;

                case MessageType.Ping:
                    await _channel.SendAsync(Message.Empty(MessageType.Pong), ct).ConfigureAwait(false);
                    break;

                case MessageType.Bye:
                    _running = false;
                    return;
            }
        }
    }

    private void ApplySettings(SessionSettings s, IReadOnlyList<DisplayInfo> displays)
    {
        var display = displays.FirstOrDefault(d => d.Index == s.DisplayIndex) ?? displays[0];

        _displays = displays;
        _settings = s;
        _injector = new InputInjector(display);
        _adaptive ??= new AdaptiveController(s, display.RefreshHz);
        _privacy?.Apply(s.LockHostInput, s.BlankHostScreen);
        // Capture creation and display switching happen inside CaptureLoop, which owns the objects.
    }

    // ---------------- capture + encode thread ----------------

    private void CaptureLoop(CancellationToken ct)
    {
        var encoder = new JpegTileEncoder();
        var scaler = new FrameScaler();
        // H.264 path (physical PCs with a hardware encoder). Created lazily when the client asks for
        // H.264 and an encoder initialises for the current geometry; otherwise we stay on JPEG tiles.
        H264Encoder? h264 = null;
        var activeCodec = VideoCodec.JpegTiles;
        var h264Tiles = new List<Tile>(1);
        uint frameId = 0;
        int idleFrames = 0;
        var sw = new Stopwatch();
        VideoConfig? announced = null;
        _secondStartTicks = Stopwatch.GetTimestamp();

        // Capture/encode pipeline: while frame N is being encoded and sent, frame N+1 is already
        // being captured on a worker thread (the capture backends double-buffer their output).
        // Throughput becomes max(capture, encode) instead of capture + encode — on GDI hosts
        // (VMs, RDP) that alone nearly doubles the frame rate.
        IScreenCapture? pendingOwner = null;
        Task<(CapturedFrame? Frame, long Ms)>? pendingCapture = null;
        // Best-case capture cost seen while the screen is busy. We key the DXGI→GDI demotion off the
        // MINIMUM (not an average) because that isolates the true readback floor from wait-for-change
        // and one-off spikes: a fast GPU whose DXGI occasionally stalls (e.g. 0.1 ms typical, rare
        // 117 ms) must stay on DXGI, while a virtual GPU whose *fastest* grab is still ~150 ms is
        // genuinely slow and should fall back to GDI.
        double captureMinMs = double.MaxValue;
        int captureSamples = 0;

        // Wait for an in-flight pipelined capture to finish. Must run before disposing the capture
        // object it uses — tearing a capture down mid-call crashes inside the driver.
        void DrainPending()
        {
            try { pendingCapture?.GetAwaiter().GetResult(); } catch { }
            pendingCapture = null;
            pendingOwner = null;
        }

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                // Create or swap the capture for the requested display — only this thread ever
                // creates or disposes capture objects.
                var settings = _settings;
                var displays = _displays;
                if (displays.Count == 0) { Thread.Sleep(10); continue; }
                var wantDisplay = displays.FirstOrDefault(d => d.Index == settings.DisplayIndex) ?? displays[0];
                // In --agent mode this attaches the capture thread to the current input desktop (user
                // desktop, or the Winlogon/UAC secure desktop) and reports when it switched, so we
                // recreate the capturer on the new desktop and keep showing the login screen.
                bool desktopSwitched = DesktopFollow.ReattachIfChanged();
                if (desktopSwitched)
                    _log.LogInformation("Input desktop -> {Desktop}", DesktopFollow.CurrentDesktop);
                if (_capture is null || _capture.Display.Index != wantDisplay.Index || desktopSwitched)
                {
                    DrainPending();
                    _capture?.Dispose();
                    _capture = CreateCapture(wantDisplay);
                    _adaptive = new AdaptiveController(settings, wantDisplay.RefreshHz);
                    captureMinMs = double.MaxValue;
                    captureSamples = 0;
                    _gdiTargetW = _gdiTargetH = -1; // force the source-scale target to re-apply
                    _keyFrameRequested = true;
                    announced = null;
                    _log.LogInformation("Capturing {Display} via {Backend}", wantDisplay.DeviceName, _capture.BackendName);
                }

                var capture = _capture;
                var adaptive = _adaptive;
                if (capture is null || adaptive is null) { Thread.Sleep(10); continue; }

                // Tell a GDI capturer the desired source-scale target before it grabs (must happen
                // before the pipelined next-capture is kicked off). No pending capture may be in
                // flight when we resize its buffers, so drain first if the target actually changes.
                if (capture is GdiScreenCapture gdi)
                {
                    var st = _settings;
                    bool scale = st.ResolutionMode == ResolutionMode.Scaled && st.ScaledWidth > 0 && st.ScaledHeight > 0;
                    int tw = scale ? st.ScaledWidth : 0, th = scale ? st.ScaledHeight : 0;
                    if (tw != _gdiTargetW || th != _gdiTargetH)
                    {
                        DrainPending();
                        gdi.SetTargetSize(tw, th);
                        _gdiTargetW = tw;
                        _gdiTargetH = th;
                        _keyFrameRequested = true;
                    }
                }

                // Remote input means the user is interacting — cancel any idle backoff immediately
                // so the first visual response isn't delayed by a sleeping capture loop.
                if (_inputActivity) { _inputActivity = false; idleFrames = 0; }

                CapturedFrame? frame;
                long captureMs;
                // --agent mode must grab on THIS thread (the one attached to the input desktop) — a
                // pooled thread would be on the wrong desktop and miss the login screen. So pipelining
                // is disabled there; everywhere else it overlaps capture N+1 with encode N.
                bool pipeline = !DesktopFollow.Enabled;
                if (pipeline && pendingCapture != null && ReferenceEquals(pendingOwner, capture))
                {
                    (frame, captureMs) = pendingCapture.GetAwaiter().GetResult();
                }
                else
                {
                    var t = Stopwatch.StartNew();
                    frame = capture.Capture(timeoutMs: 100);
                    captureMs = t.ElapsedMilliseconds;
                }

                // Kick off the next capture right away; it overlaps with the encode below.
                if (pipeline)
                {
                    pendingOwner = capture;
                    pendingCapture = Task.Run(() =>
                    {
                        var t = Stopwatch.StartNew();
                        var f = capture.Capture(timeoutMs: 100);
                        return (f, t.ElapsedMilliseconds);
                    });
                }

                // Timing starts at the frame, not the wait-for-change — otherwise idle time would
                // masquerade as encode cost in the stats and the adaptive controller.
                sw.Restart();

                if (frame is null || frame.IsEmpty)
                    continue; // nothing changed; loop straight back for the next event

                // Reduced stream resolution. GDI scales at the source (SetTargetSize below already
                // told it to), so its frame arrives pre-shrunk and we skip the CPU pass. Other
                // backends hand back a native frame that we downscale here.
                var s = _settings;
                bool wantScale = s.ResolutionMode == ResolutionMode.Scaled && s.ScaledWidth > 0 && s.ScaledHeight > 0;
                if (wantScale && capture is not GdiScreenCapture)
                {
                    double k = Math.Min((double)s.ScaledWidth / frame.Width, (double)s.ScaledHeight / frame.Height);
                    if (k < 0.999)
                        frame = scaler.Scale(frame, (int)(frame.Width * k) & ~1, (int)(frame.Height * k) & ~1);
                }

                // Negotiate the codec for this geometry. The client's PreferredCodec asks for H.264;
                // we honour it only if an encoder actually initialises here (hardware present) — in a
                // VM that fails and we transparently stay on JPEG tiles. Re-decide on any geometry
                // change or when the client toggles the codec.
                var desiredCodec = _settings.PreferredCodec == VideoCodec.H264 ? VideoCodec.H264 : VideoCodec.JpegTiles;
                bool geoChanged = announced is null || announced.Width != frame.Width || announced.Height != frame.Height;
                if (geoChanged || desiredCodec != activeCodec)
                {
                    if (h264 != null) { h264.Dispose(); h264 = null; }
                    activeCodec = VideoCodec.JpegTiles;
                    if (desiredCodec == VideoCodec.H264)
                    {
                        int fps = Math.Max(adaptive.CurrentFps, 30);
                        int bitrate = EstimateH264Bitrate(frame.Width, frame.Height, fps);
                        // Streaming requires a hardware encoder; without one (e.g. a VM) we fall back to
                        // JPEG so the viewer keeps showing video instead of stalling on a slow SW encoder.
                        h264 = H264Encoder.TryCreate(frame.Width, frame.Height, fps, bitrate,
                            preferHardware: true, out string why, hardwareOnly: true);
                        if (h264 != null) activeCodec = VideoCodec.H264;
                        else _log.LogInformation("H.264 requested but no hardware encoder ({Why}); using JPEG tiles.", why);
                    }
                    announced = new VideoConfig(frame.Width, frame.Height, activeCodec, encoder.TileSize);
                    _channel.TrySend(PayloadCodec.VideoConfigMsg(announced));
                    _keyFrameRequested = true;
                    _log.LogInformation("Streaming {W}x{H} as {Codec}", frame.Width, frame.Height, activeCodec);
                }

                bool wantKey = _keyFrameRequested;
                _keyFrameRequested = false;

                IReadOnlyList<Tile> tiles;
                bool wasKey;
                int encodeMs;
                if (activeCodec == VideoCodec.H264 && h264 != null)
                {
                    byte[] annexB = h264.Encode(frame.Bgra, frame.Stride, out wasKey);
                    encodeMs = (int)sw.ElapsedMilliseconds;
                    if (annexB.Length == 0) { PaceFrame(sw, adaptive); continue; } // encoder still warming up
                    h264Tiles.Clear();
                    h264Tiles.Add(new Tile(0, 0, (ushort)frame.Width, (ushort)frame.Height, annexB));
                    tiles = h264Tiles;
                    idleFrames = 0;
                }
                else
                {
                    encoder.SetQuality(adaptive.CurrentQuality);
                    tiles = encoder.Encode(frame, wantKey, out wasKey);
                    // Pure encode cost, measured before the (possibly blocking) send so the stat and the
                    // adaptive controller see real compression time, not network wait.
                    encodeMs = (int)sw.ElapsedMilliseconds;
                    if (tiles.Count == 0)
                    {
                        PaceFrame(sw, adaptive);
                        // Back off polling on a static screen so an idle desktop costs almost nothing.
                        // A single input/keyframe request resets this immediately.
                        if (++idleFrames > 8) Thread.Sleep(Math.Min(60, (idleFrames - 8) * 4));
                        continue;
                    }
                    idleFrames = 0;
                }

                var flags = wasKey ? FrameFlags.KeyFrame : FrameFlags.None;
                var frameMsg = VideoFrameCodec.Encode(frameId++, flags, tiles);
                int frameBytes = frameMsg.Length;
                // The channel's shallow video lane blocks here when the link is behind (backpressure),
                // so the encoder can't outrun the wire and latency stays bounded. It only returns
                // false while shutting down, in which case we recycle the buffer ourselves.
                if (!_channel.TrySend(frameMsg))
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(frameMsg.Payload);
                    _keyFrameRequested = true;
                }

                AccountBandwidth(frameBytes);
                adaptive.Observe(encodeMs, frameBytes, _measuredMbps);

                // Judge the capture backend only under motion: many dirty tiles means the acquire
                // returned immediately, so the measured time is real capture/readback cost. Demote
                // DXGI→GDI only when even the FASTEST recent grab is slow — that means the readback
                // itself is expensive (a virtual GPU at ~150 ms), not just an occasional stall on a
                // fast GPU. Keying off the minimum (over a longer window) stops us from wrongly
                // dropping a healthy DXGI (0.1 ms typical) to a slower GDI path just because of a spike.
                if (tiles.Count >= 8)
                {
                    if (captureMs < captureMinMs) captureMinMs = captureMs;
                    captureSamples++;
                    if (!_forceGdi && captureSamples >= 30 && captureMinMs > 60 &&
                        capture.BackendName.StartsWith("DXGI", StringComparison.Ordinal))
                    {
                        _log.LogWarning(
                            "DXGI best-case capture {Ms:F0} ms — genuinely slow readback; switching to GDI.",
                            captureMinMs);
                        _forceGdi = true;
                        DrainPending();
                        var display = capture.Display;
                        capture.Dispose();
                        _capture = CreateCapture(display);
                        captureMinMs = double.MaxValue;
                        captureSamples = 0;
                        _gdiTargetW = _gdiTargetH = -1; // re-apply source-scale to the new GDI capturer
                        _keyFrameRequested = true;
                        continue;
                    }
                }

                string encName = activeCodec == VideoCodec.H264
                    ? "H.264 hardware" : $"JPEG · {capture.BackendName}";
                MaybeSendStat(adaptive, (int)captureMs, encodeMs, encName);
                PaceFrame(sw, adaptive);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A capture object disposed under us (display switch) or a transient GPU loss lands
                // here. Recover instead of tearing the session down: brief pause, then resume with a
                // full refresh against whatever capture is current now.
                _log.LogWarning(ex, "Capture iteration error; recovering.");
                announced = null;
                _keyFrameRequested = true;
                DrainPending();
                Thread.Sleep(50);
            }
        }

        // Session over — drain the in-flight capture, then dispose everything this thread owns.
        DrainPending();
        _capture?.Dispose();
        _capture = null;
        h264?.Dispose();
        encoder.Dispose();
        scaler.Dispose();
    }

    /// <summary>
    /// Target H.264 bitrate for a geometry/fps, nudged by the quality slider. Clamped to a sane band so
    /// it never starves (blocky) or floods the link. Inter-frame compression means the real average
    /// sits well below this on a mostly-static desktop.
    /// </summary>
    private int EstimateH264Bitrate(int width, int height, int fps)
    {
        double q = 0.5 + _settings.Quality / 100.0;           // 0.51 .. 1.5
        double bps = (double)width * height * fps * 0.08 * q; // ~12 Mbps at 1080p60, quality 75
        return (int)Math.Clamp(bps, 2_000_000, 25_000_000);
    }

    private static void PaceFrame(Stopwatch sw, AdaptiveController adaptive)
    {
        var target = adaptive.FrameInterval;
        var spent = sw.Elapsed;
        if (spent < target)
        {
            var remaining = target - spent;
            // Sleep the bulk, spin the last sliver for accurate high-fps pacing.
            if (remaining > TimeSpan.FromMilliseconds(2))
                Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
        }
    }

    private void AccountBandwidth(int bytes)
    {
        _bytesThisSecond += bytes;
        _framesThisSecond++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _secondStartTicks) / (double)Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            _measuredMbps = _bytesThisSecond * 8 / elapsed / 1_000_000;
            _measuredFps = (int)Math.Round(_framesThisSecond / elapsed);
            _bytesThisSecond = 0;
            _framesThisSecond = 0;
            _secondStartTicks = now;
        }
    }

    private long _lastStatTicks;
    private void MaybeSendStat(AdaptiveController adaptive, int captureMs, int encodeMs, string backend)
    {
        long now = Stopwatch.GetTimestamp();
        if ((now - _lastStatTicks) / (double)Stopwatch.Frequency < 0.5) return;
        _lastStatTicks = now;
        // Report the fps actually achieved (frames sent per second), not the pacing target —
        // that's what the user perceives, and it makes fps-picker changes honestly visible.
        // The RoundTripMs slot carries the capture cost so the client can show cap/enc separately.
        int shownFps = _measuredFps > 0 ? _measuredFps : adaptive.CurrentFps;
        _channel.TrySend(PayloadCodec.Stat(new SessionStat(
            shownFps, _measuredMbps, captureMs, encodeMs, backend)));
    }

    private void OnHostClipboardChanged(ClipboardData data)
    {
        if (_settings.ClipboardSync)
            _channel.TrySend(ClipboardCodec.Encode(data));
    }

    // ---------------- capture backend selection ----------------

    private IScreenCapture CreateCapture(DisplayInfo display)
    {
#if ENABLE_DXGI
        // In --agent mode use GDI: it captures whatever desktop this (input-desktop-attached) thread is
        // on, including the Winlogon/UAC secure desktop, whereas DXGI Desktop Duplication is blocked
        // there. GDI on a small login screen is plenty fast.
        if (_config.PreferHardwareCapture && !_forceGdi && !DesktopFollow.Enabled)
        {
            try { return new DesktopDuplicationCapture(display); }
            catch (Exception ex) { _log.LogWarning(ex, "DXGI duplication unavailable; falling back to GDI."); }
        }
#endif
        return new GdiScreenCapture(display);
    }

    private IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
#if ENABLE_DXGI
        if (_config.PreferHardwareCapture)
        {
            try
            {
                var d = DesktopDuplicationCapture.EnumerateDisplays();
                if (d.Count > 0) return d;
            }
            catch { }
        }
#endif
        return GdiScreenCapture.EnumerateDisplays();
    }
}
