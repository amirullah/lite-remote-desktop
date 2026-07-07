namespace RemoteDesktop.Host.Capture;

/// <summary>
/// A single captured desktop frame in 32-bit BGRA, top-down. The pixel buffer is owned by the
/// capturer and reused between frames — consumers must finish reading before requesting the next.
/// </summary>
public sealed class CapturedFrame
{
    public byte[] Bgra = Array.Empty<byte>();
    public int Width;
    public int Height;
    public int Stride;      // bytes per row (>= Width*4, may include padding)
    public long TimestampTicks;

    /// <summary>
    /// Hardware-reported dirty rectangles for this frame, when the capture backend provides them
    /// (Desktop Duplication does; GDI does not). Null means "unknown — diff the whole frame".
    /// </summary>
    public IReadOnlyList<(int X, int Y, int W, int H)>? DirtyRects;

    public bool IsEmpty => Width == 0 || Height == 0;
}
