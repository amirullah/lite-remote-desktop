using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Decides, frame by frame, the target frame interval and JPEG quality. In Auto mode it walks fps
/// up toward the display's refresh rate when there's headroom and backs off when the encoder or
/// the link can't keep up — the goal being a smooth, high frame rate that degrades gracefully
/// instead of stuttering. In Fixed mode it simply honours the user's chosen fps.
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
            ? settings.TargetFps
            : Math.Min(settings.MaxFps, _displayRefreshHz);
        _currentQuality = settings.Quality;
    }

    public void UpdateSettings(SessionSettings settings)
    {
        _settings = settings;
        _currentQuality = settings.Quality;
        if (settings.FrameRateMode == FrameRateMode.Fixed)
            _currentFps = settings.TargetFps;
    }

    public int CurrentFps => _currentFps;
    public int CurrentQuality => _currentQuality;
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _currentFps));

    /// <summary>
    /// Feed back the cost of the frame we just sent so the next interval can adapt.
    /// <paramref name="linkMbps"/> is the measured send throughput; 0 means "unknown".
    /// </summary>
    public void Observe(int encodeMs, int frameBytes, double linkMbps)
    {
        _emaEncodeMs = _emaEncodeMs == 0 ? encodeMs : _emaEncodeMs * 0.8 + encodeMs * 0.2;
        _emaBytesPerFrame = _emaBytesPerFrame == 0 ? frameBytes : _emaBytesPerFrame * 0.8 + frameBytes * 0.2;

        if (_settings.FrameRateMode == FrameRateMode.Fixed)
            return; // user is in control of fps

        int ceiling = Math.Min(_settings.MaxFps, _displayRefreshHz);

        // 1) Never schedule frames faster than we can encode them (leave 20% headroom).
        int encodeBoundFps = _emaEncodeMs > 0.1 ? (int)(1000.0 / (_emaEncodeMs * 1.2)) : ceiling;

        // 2) If the link is known, don't push more than it can carry.
        int linkBoundFps = ceiling;
        if (linkMbps > 0 && _emaBytesPerFrame > 0)
        {
            double frameBits = _emaBytesPerFrame * 8;
            linkBoundFps = (int)(linkMbps * 1_000_000 * 0.85 / frameBits);
        }

        int target = Math.Clamp(Math.Min(Math.Min(encodeBoundFps, linkBoundFps), ceiling), 5, ceiling);

        // Ease toward the target so fps doesn't oscillate visibly.
        _currentFps += Math.Sign(target - _currentFps) * Math.Min(5, Math.Abs(target - _currentFps));

        // Under sustained pressure, shed quality before frame rate — motion smoothness reads better.
        if (encodeBoundFps < _currentFps || (linkMbps > 0 && linkBoundFps < _currentFps))
            _currentQuality = Math.Max(35, _currentQuality - 2);
        else if (_currentQuality < _settings.Quality)
            _currentQuality = Math.Min(_settings.Quality, _currentQuality + 1);
    }
}
