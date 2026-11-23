namespace RemoteDesktop.Maui.Controls;

/// <summary>Raw touch state from the native view: action, fingers down, first two finger positions (view px).</summary>
public enum TouchAction { Down, Move, Up, Cancel }
public readonly record struct TouchFrame(TouchAction Action, int Count, float X, float Y, float X2, float Y2, long TimeMs);

/// <summary>
/// A cross-platform control whose platform handler provides a native render <c>Surface</c> for the
/// video decoder to draw into (Android: a <c>TextureView</c>). The shared code stays platform-neutral
/// by passing the surface as <see cref="object"/>; the decoder factory casts it per platform.
/// </summary>
public sealed class RemoteScreenView : View
{
    /// <summary>Raised (on the UI thread) when the native render surface becomes available.</summary>
    public event Action<object>? SurfaceReady;

    /// <summary>Raised when the native surface is torn down (e.g. app backgrounded / page closed).</summary>
    public event Action? SurfaceDestroyed;

    /// <summary>Raw multi-touch from the native view (Android). Coordinates are view pixels.</summary>
    public event Action<TouchFrame>? Touch;

    /// <summary>Asks the native view to draw the video into this rectangle (view pixels): letterbox/zoom/pan.</summary>
    public event Action<float, float, float, float>? ContentRectRequested;

    public void RaiseSurfaceReady(object surface) => SurfaceReady?.Invoke(surface);
    public void RaiseSurfaceDestroyed() => SurfaceDestroyed?.Invoke();
    public void RaiseTouch(TouchFrame f) => Touch?.Invoke(f);

    /// <summary>Draw the picture at (left, top) with this size, in native view pixels.</summary>
    public void SetContentRect(float left, float top, float width, float height)
        => ContentRectRequested?.Invoke(left, top, width, height);

    /// <summary>Native view size in pixels (set by the handler); 0 until laid out.</summary>
    public float PixelWidth { get; internal set; }
    public float PixelHeight { get; internal set; }
    public event Action? PixelSizeChanged;
    internal void SetPixelSize(float w, float h)
    {
        if (w == PixelWidth && h == PixelHeight) return;
        PixelWidth = w; PixelHeight = h;
        PixelSizeChanged?.Invoke();
    }
}
