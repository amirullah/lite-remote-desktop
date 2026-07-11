namespace RemoteDesktop.Maui.Controls;

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

    public void RaiseSurfaceReady(object surface) => SurfaceReady?.Invoke(surface);
    public void RaiseSurfaceDestroyed() => SurfaceDestroyed?.Invoke();
}
