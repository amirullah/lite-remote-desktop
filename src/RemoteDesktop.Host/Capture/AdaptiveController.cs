using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Decides, frame by frame, the target frame interval and JPEG quality. In Auto mode it walks fps
/// up toward the display's refresh rate when there's headroom and backs off when the encoder or
/// the link can't keep up — the goal being a smooth, high frame rate that degrades gracefully
/// instead of stuttering. In Fixed mode it simply honours the user's chosen fps.
///
/// The only real congestion signals are the encoder's own cost and outbound-queue backpressure
/// (a dropped frame). Measured throughput is deliberately NOT used as a ceiling: on a quiet screen
/// almost nothing is sent, and treating that tiny number as "link capacity" would throttle the
/// stream into the ground exactly when the user starts moving things again.
/// </summary>
public sealed class AdaptiveController
{
    private SessionSettings _settings;
    private readonly int _displayRefreshHz;

    private int _currentFps;
    private int _currentQuality;
    private double _emaEncodeMs;
    private double _emaBytesPerFrame;

    public AdaptiveController(SessionSettings settings, int displayRefreshHz)
    {
        _settings = settings;
        _displayRefreshHz = Math.Clamp(displayRefreshHz, 30, 240);
        _currentFps = settings.FrameRateMode == FrameRateMode.Fixed
            ? Math.Max(1, settings.TargetFps)
            : Math.Min(settings.MaxFps, _displayRefreshHz);
        _currentQuality = settings.Quality;
    }

    public void UpdateSettings(SessionSettings settings)
    {
        _settings = settings;
        _currentQuality = settings.Quality;
        if (settings.FrameRateMode == FrameRateMode.Fixed)
            _currentFps = Math.Max(1, settings.TargetFps);
    }

    public int CurrentFps => _currentFps;
    public int CurrentQuality => _currentQuality;
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _currentFps));

    /// <summary>
    /// Feed back the cost of the frame we just sent so the next interval can adapt. Network
    /// backpressure no longer needs an explicit signal: the transport blocks the encode thread when
    /// the link is behind, so a slow link shows up directly as a higher wall-clock frame time.
    /// </summary>
    public void Observe(int encodeMs, int frameBytes, double linkMbps)
    {
        _emaEncodeMs = _emaEncodeMs == 0 ? encodeMs : _emaEncodeMs * 0.8 + encodeMs * 0.2;
        _emaBytesPerFrame = _emaBytesPerFrame == 0 ? frameBytes : _emaBytesPerFrame * 0.8 + frameBytes * 0.2;

        if (_settings.FrameRateMode == FrameRateMode.Fixed)
        {
            // User owns fps; nudge quality back up toward their target when there's room.
            if (_currentQuality < _settings.Quality)
                _currentQuality = Math.Min(_settings.Quality, _currentQuality + 1);
            return;
        }

        int ceiling = Math.Min(_settings.MaxFps, _displayRefreshHz);

        // Never schedule frames faster than we can encode them (15% headroom).
        int target = _emaEncodeMs > 0.1 ? (int)(1000.0 / (_emaEncodeMs * 1.15)) : ceiling;
        target = Math.Clamp(target, 10, ceiling);

        // Drop quickly when constrained, climb briskly when there's headroom.
        if (target < _currentFps)
            _currentFps = target;
        else
            _currentFps = Math.Min(target, _currentFps + 6);

        // Recover quality once the pipeline is comfortable again.
        if (_currentFps >= target && _currentQuality < _settings.Quality)
            _currentQuality = Math.Min(_settings.Quality, _currentQuality + 1);
    }
}
