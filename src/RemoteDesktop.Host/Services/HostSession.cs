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
        // Rolling capture-cost estimate, sampled only while the screen is busy (so DXGI's blocking
        // wait-for-change on an idle screen doesn't count as "slow").
        double captureEmaMs = 0;
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
                if (_capture is null || _capture.Display.Index != wantDisplay.Index)
                {
                    DrainPending();
                    _capture?.Dispose();
                    _capture = CreateCapture(wantDisplay);
                    _adaptive = new AdaptiveController(settings, wantDisplay.RefreshHz);
                    captureEmaMs = 0;
                    captureSamples = 0;
                    _keyFrameRequested = true;
                    announced = null;
                    _log.LogInformation("Capturing {Display} via {Backend}", wantDisplay.DeviceName, _capture.BackendName);
                }

                var capture = _capture;
                var adaptive = _adaptive;
                if (capture is null || adaptive is null) { Thread.Sleep(10); continue; }

                // Remote input means the user is interacting — cancel any idle backoff immediately
                // so the first visual response isn't delayed by a sleeping capture loop.
                if (_inputActivity) { _inputActivity = false; idleFrames = 0; }

                CapturedFrame? frame;
                long captureMs;
                if (pendingCapture != null && ReferenceEquals(pendingOwner, capture))
                {
                    (frame, captureMs) = pendingCapture.GetAwaiter().GetResult();
                }
                else
                {
                    // First iteration or the display just changed — capture synchronously.
                    var t = Stopwatch.StartNew();
                    frame = capture.Capture(timeoutMs: 100);
                    captureMs = t.ElapsedMilliseconds;
                }

                // Kick off the next capture right away; it overlaps with the encode below.
                pendingOwner = capture;
                pendingCapture = Task.Run(() =>
                {
                    var t = Stopwatch.StartNew();
                    var f = capture.Capture(timeoutMs: 100);
                    return (f, t.ElapsedMilliseconds);
                });

                // Timing starts at the frame, not the wait-for-change — otherwise idle time would
                // masquerade as encode cost in the stats and the adaptive controller.
                sw.Restart();

                if (frame is null || frame.IsEmpty)
                    continue; // nothing changed; loop straight back for the next event

                // Client asked for a reduced stream resolution — fit the frame inside the requested
                // box preserving aspect ratio (never upscale), then encode the smaller image.
                var s = _settings;
                if (s.ResolutionMode == ResolutionMode.Scaled && s.ScaledWidth > 0 && s.ScaledHeight > 0)
                {
                    double k = Math.Min((double)s.ScaledWidth / frame.Width, (double)s.ScaledHeight / frame.Height);
                    if (k < 0.999)
                        frame = scaler.Scale(frame, (int)(frame.Width * k) & ~1, (int)(frame.Height * k) & ~1);
                }

                // Announce geometry the first time / whenever it changes.
                if (announced is null || announced.Width != frame.Width || announced.Height != frame.Height)
                {
                    announced = new VideoConfig(frame.Width, frame.Height, VideoCodec.JpegTiles, encoder.TileSize);
                    _channel.TrySend(PayloadCodec.VideoConfigMsg(announced));
                    _keyFrameRequested = true;
                }

                encoder.SetQuality(adaptive.CurrentQuality);
                bool wantKey = _keyFrameRequested;
                _keyFrameRequested = false;

                var tiles = encoder.Encode(frame, wantKey, out bool wasKey);
                if (tiles.Count == 0)
                {
                    PaceFrame(sw, adaptive);
                    // Back off polling on a static screen so an idle desktop costs almost nothing.
                    // A single input/keyframe request resets this immediately.
                    if (++idleFrames > 8) Thread.Sleep(Math.Min(60, (idleFrames - 8) * 4));
                    continue;
                }
                idleFrames = 0;

                var flags = wasKey ? FrameFlags.KeyFrame : FrameFlags.None;
                var frameMsg = VideoFrameCodec.Encode(frameId++, flags, tiles);
                int frameBytes = frameMsg.Length;
                // If the outbound queue is saturated we drop this frame — but must return its pooled
                // buffer ourselves, since the write pump only recycles frames it actually sent. The
                // encoder already recorded these tiles as "sent", so force a full refresh next frame
                // to avoid leaving stale regions on the client.
                if (!_channel.TrySend(frameMsg))
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(frameMsg.Payload);
                    _keyFrameRequested = true;
                    adaptive.NotifyBackpressure();
                }

                int encodeMs = (int)sw.ElapsedMilliseconds;
                AccountBandwidth(frameBytes);
                adaptive.Observe(encodeMs, frameBytes, _measuredMbps);

                // Judge the capture backend only under motion: many dirty tiles means the acquire
                // returned immediately, so the measured time is real capture/readback cost. On
                // virtual GPUs (VMware/VirtualBox) DXGI duplication readback can take hundreds of
                // milliseconds — far worse than plain GDI — so demote it when it proves slow.
                if (tiles.Count >= 8)
                {
                    captureEmaMs = captureEmaMs == 0 ? captureMs : captureEmaMs * 0.8 + captureMs * 0.2;
                    captureSamples++;
                    if (!_forceGdi && captureSamples >= 10 && captureEmaMs > 60 &&
                        capture.BackendName.StartsWith("DXGI", StringComparison.Ordinal))
                    {
                        _log.LogWarning(
                            "DXGI capture averages {Ms:F0} ms — slow virtual GPU readback; switching to GDI.",
                            captureEmaMs);
                        _forceGdi = true;
                        DrainPending();
                        var display = capture.Display;
                        capture.Dispose();
                        _capture = CreateCapture(display);
                        captureEmaMs = 0;
                        captureSamples = 0;
                        _keyFrameRequested = true;
                        continue;
                    }
                }

                MaybeSendStat(adaptive, (int)captureMs, encodeMs, capture.BackendName);
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
        encoder.Dispose();
        scaler.Dispose();
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
        if (_config.PreferHardwareCapture && !_forceGdi)
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
