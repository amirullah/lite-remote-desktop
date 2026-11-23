namespace RemoteDesktop.Maui.Input;

/// <summary>What the gesture layer asks the session to do (coordinates are 0..1 remote fractions).</summary>
public interface IRemoteInputSink
{
    void Move(float nx, float ny);
    void Button(bool right, bool down, float nx, float ny);
    void Wheel(int deltaY, float nx, float ny);
    /// <summary>The view transform changed (zoom/pan) — re-apply it to the screen.</summary>
    void ViewportChanged();
}

/// <summary>
/// Touch → mouse, "direct touch" style (the pointer goes where you touch):
/// tap = left click · hold = right click · one-finger drag = left drag ·
/// two-finger tap = right click · pinch = zoom · two-finger drag = scroll, or pan while zoomed in.
/// Platform-neutral: the Android view feeds raw pointer positions + timestamps; a periodic
/// <see cref="Tick"/> fires the long-press. Unit-testable without a device.
/// </summary>
public sealed class TouchGestures
{
    private readonly Viewport _vp;
    private readonly IRemoteInputSink _sink;
    private readonly float _slop;          // view px a finger may wander and still count as a tap
    private const long LongPressMs = 550;
    private const long TwoFingerTapMs = 300;
    private const float WheelStepPx = 36f; // two-finger travel per wheel notch
    private const int WheelNotch = 120;

    private enum Mode { Idle, Pending, Dragging, LongPressed, Multi, Finished }
    private Mode _mode = Mode.Idle;

    // single finger
    private float _downX, _downY, _lastX, _lastY;
    private long _downAt;
    // two fingers
    private float _lastDist, _lastCx, _lastCy, _wheelAcc;
    private long _multiAt;
    private bool _multiMoved;

    public TouchGestures(Viewport viewport, IRemoteInputSink sink, float density = 1f)
    {
        _vp = viewport;
        _sink = sink;
        _slop = 10f * Math.Max(1f, density);
    }

    /// <param name="count">Fingers currently on the screen (after this event).</param>
    public void Down(int count, float x, float y, float x2, float y2, long nowMs)
    {
        if (count == 1 && _mode is Mode.Idle or Mode.Finished)
        {
            _mode = Mode.Pending;
            _downX = _lastX = x; _downY = _lastY = y; _downAt = nowMs;
            return;
        }
        if (count >= 2) BeginMulti(x, y, x2, y2, nowMs);
    }

    public void Move(int count, float x, float y, float x2, float y2, long nowMs)
    {
        switch (_mode)
        {
            case Mode.Pending:
                if (count >= 2) { BeginMulti(x, y, x2, y2, nowMs); return; }
                if (Dist(x, y, _downX, _downY) <= _slop) return;
                // Finger travelled: it's a drag — press at the START point, then follow the finger.
                _mode = Mode.Dragging;
                var (sx, sy) = _vp.ViewToRemote(_downX, _downY);
                _sink.Move(sx, sy);
                _sink.Button(false, true, sx, sy);
                goto case Mode.Dragging;
            case Mode.Dragging:
                if (count >= 2) { BeginMulti(x, y, x2, y2, nowMs); return; }
                _lastX = x; _lastY = y;
                var (mx, my) = _vp.ViewToRemote(x, y);
                _sink.Move(mx, my);
                return;
            case Mode.Multi:
                if (count < 2) return;
                MultiMove(x, y, x2, y2);
                return;
        }
    }

    /// <param name="count">Fingers still on the screen after this one lifted.</param>
    public void Up(int count, float x, float y, long nowMs)
    {
        switch (_mode)
        {
            case Mode.Pending:
                if (count > 0) return;
                var (tx, ty) = _vp.ViewToRemote(_downX, _downY);
                _sink.Move(tx, ty);
                _sink.Button(false, true, tx, ty);
                _sink.Button(false, false, tx, ty);
                _mode = Mode.Idle;
                return;
            case Mode.Dragging:
                var (ux, uy) = _vp.ViewToRemote(_lastX, _lastY);
                _sink.Button(false, false, ux, uy);
                _mode = count > 0 ? Mode.Finished : Mode.Idle;
                return;
            case Mode.Multi:
                if (count > 0) return;   // wait for the last finger
                if (!_multiMoved && nowMs - _multiAt <= TwoFingerTapMs)
                {
                    var (rx, ry) = _vp.ViewToRemote(_lastCx, _lastCy);
                    _sink.Move(rx, ry);
                    _sink.Button(true, true, rx, ry);
                    _sink.Button(true, false, rx, ry);
                }
                _mode = Mode.Idle;
                return;
            default:
                if (count == 0) _mode = Mode.Idle;
                return;
        }
    }

    /// <summary>The OS cancelled the gesture (e.g. a system swipe): release anything held.</summary>
    public void Cancel()
    {
        if (_mode == Mode.Dragging)
        {
            var (ux, uy) = _vp.ViewToRemote(_lastX, _lastY);
            _sink.Button(false, false, ux, uy);
        }
        _mode = Mode.Idle;
    }

    /// <summary>Call periodically (~every 100 ms): turns a still, held finger into a right click.</summary>
    public void Tick(long nowMs)
    {
        if (_mode != Mode.Pending || nowMs - _downAt < LongPressMs) return;
        var (rx, ry) = _vp.ViewToRemote(_downX, _downY);
        _sink.Move(rx, ry);
        _sink.Button(true, true, rx, ry);
        _sink.Button(true, false, rx, ry);
        _mode = Mode.LongPressed;   // the rest of this touch does nothing
    }

    private void BeginMulti(float x, float y, float x2, float y2, long nowMs)
    {
        if (_mode == Mode.Dragging)
        {
            // A second finger joined a drag: finish the drag cleanly before switching to zoom/scroll.
            var (ux, uy) = _vp.ViewToRemote(_lastX, _lastY);
            _sink.Button(false, false, ux, uy);
        }
        if (_mode == Mode.Multi) return;
        _mode = Mode.Multi;
        _multiAt = nowMs;
        _multiMoved = false;
        _wheelAcc = 0;
        _lastDist = Math.Max(1f, Dist(x, y, x2, y2));
        _lastCx = (x + x2) / 2; _lastCy = (y + y2) / 2;
    }

    private void MultiMove(float x, float y, float x2, float y2)
    {
        float dist = Math.Max(1f, Dist(x, y, x2, y2));
        float cx = (x + x2) / 2, cy = (y + y2) / 2;
        float dx = cx - _lastCx, dy = cy - _lastCy;
        float ratio = dist / _lastDist;

        if (Math.Abs(ratio - 1f) > 0.01f)
        {
            _vp.ZoomAt(ratio, cx, cy);
            _sink.ViewportChanged();
            _multiMoved = true;
        }
        if (Math.Abs(dx) > 0.5f || Math.Abs(dy) > 0.5f)
        {
            _multiMoved |= Math.Abs(dx) + Math.Abs(dy) > 1f;
            if (_vp.Zoom > 1.01f)
            {
                _vp.PanBy(dx, dy);
                _sink.ViewportChanged();
            }
            else
            {
                // Natural scrolling: fingers up → content moves up → wheel "down" (negative delta).
                _wheelAcc += dy;
                var (rx, ry) = _vp.ViewToRemote(cx, cy);
                while (_wheelAcc >= WheelStepPx) { _sink.Wheel(WheelNotch, rx, ry); _wheelAcc -= WheelStepPx; }
                while (_wheelAcc <= -WheelStepPx) { _sink.Wheel(-WheelNotch, rx, ry); _wheelAcc += WheelStepPx; }
            }
        }
        _lastDist = dist; _lastCx = cx; _lastCy = cy;
    }

    private static float Dist(float ax, float ay, float bx, float by)
        => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}
