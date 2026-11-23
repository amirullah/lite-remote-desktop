namespace RemoteDesktop.Maui.Input;

/// <summary>
/// Where the remote picture sits inside the on-screen view: letterboxed to keep its aspect ratio, then
/// zoomed (1x..MaxZoom) and panned. Pure math (no platform types) so it can be unit-tested on a PC.
/// All values are in view pixels; the remote size is in remote pixels.
/// </summary>
public sealed class Viewport
{
    public const float MaxZoom = 5f;

    public float ViewWidth { get; private set; }
    public float ViewHeight { get; private set; }
    public float RemoteWidth { get; private set; }
    public float RemoteHeight { get; private set; }
    public float Zoom { get; private set; } = 1f;

    // Top-left of the picture in view coordinates.
    public float Left { get; private set; }
    public float Top { get; private set; }

    public bool IsReady => ViewWidth > 0 && ViewHeight > 0 && RemoteWidth > 0 && RemoteHeight > 0;

    /// <summary>Picture size on screen at the current zoom.</summary>
    public float ContentWidth => RemoteWidth * Scale;
    public float ContentHeight => RemoteHeight * Scale;

    /// <summary>View pixels per remote pixel.</summary>
    public float Scale => IsReady ? FitScale * Zoom : 0f;

    private float FitScale => Math.Min(ViewWidth / RemoteWidth, ViewHeight / RemoteHeight);

    public void SetView(float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        // Keep the same remote point under the view centre across rotation / resize.
        var (cx, cy) = IsReady ? ViewToRemote(ViewWidth / 2, ViewHeight / 2) : (0.5f, 0.5f);
        ViewWidth = width; ViewHeight = height;
        CenterOn(cx, cy);
    }

    public void SetRemote(float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        bool changed = width != RemoteWidth || height != RemoteHeight;
        RemoteWidth = width; RemoteHeight = height;
        if (changed) { Zoom = 1f; CenterOn(0.5f, 0.5f); }
    }

    public void Reset() { Zoom = 1f; CenterOn(0.5f, 0.5f); }

    /// <summary>Zoom by <paramref name="factor"/> keeping the picture point under (fx, fy) fixed.</summary>
    public void ZoomAt(float factor, float fx, float fy)
    {
        if (!IsReady || factor <= 0) return;
        var (rx, ry) = ViewToRemote(fx, fy, clamp: false);
        Zoom = Math.Clamp(Zoom * factor, 1f, MaxZoom);
        Left = fx - rx * ContentWidth;
        Top = fy - ry * ContentHeight;
        Clamp();
    }

    /// <summary>Move the picture by (dx, dy) view pixels (only meaningful when it is larger than the view).</summary>
    public void PanBy(float dx, float dy)
    {
        if (!IsReady) return;
        Left += dx; Top += dy;
        Clamp();
    }

    /// <summary>View point → remote position as 0..1 fractions. Returns false if outside the picture.</summary>
    public bool TryViewToRemote(float x, float y, out float nx, out float ny)
    {
        nx = ny = 0;
        if (!IsReady) return false;
        nx = (x - Left) / ContentWidth;
        ny = (y - Top) / ContentHeight;
        bool inside = nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
        nx = Math.Clamp(nx, 0f, 1f); ny = Math.Clamp(ny, 0f, 1f);
        return inside;
    }

    public (float nx, float ny) ViewToRemote(float x, float y, bool clamp = true)
    {
        if (!IsReady) return (0.5f, 0.5f);
        float nx = (x - Left) / ContentWidth, ny = (y - Top) / ContentHeight;
        return clamp ? (Math.Clamp(nx, 0f, 1f), Math.Clamp(ny, 0f, 1f)) : (nx, ny);
    }

    /// <summary>0..1 fraction → protocol coordinate 0..65535.</summary>
    public static ushort ToWire(float f) => (ushort)Math.Clamp(MathF.Round(f * ushort.MaxValue), 0, ushort.MaxValue);

    private void CenterOn(float rx, float ry)
    {
        if (!IsReady) return;
        Left = ViewWidth / 2 - rx * ContentWidth;
        Top = ViewHeight / 2 - ry * ContentHeight;
        Clamp();
    }

    // Smaller than the view on an axis → centred (letterbox bars); larger → no empty gap at either edge.
    private void Clamp()
    {
        if (ContentWidth <= ViewWidth) Left = (ViewWidth - ContentWidth) / 2;
        else Left = Math.Clamp(Left, ViewWidth - ContentWidth, 0);
        if (ContentHeight <= ViewHeight) Top = (ViewHeight - ContentHeight) / 2;
        else Top = Math.Clamp(Top, ViewHeight - ContentHeight, 0);
    }
}
