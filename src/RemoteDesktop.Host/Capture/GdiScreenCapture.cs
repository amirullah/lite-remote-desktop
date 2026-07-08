using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// GDI BitBlt capture. Universally available — works inside VMs, over RDP, and anywhere the DXGI
/// duplication path is unavailable.
///
/// When the client requests a reduced stream resolution we scale <b>at the source</b> with
/// <c>StretchBlt</c> instead of grabbing the full screen and shrinking on the CPU afterwards. This
/// matters enormously inside a VM: the slow part there is moving pixels out of the virtual GPU
/// framebuffer, and that cost is proportional to how many pixels we read back. Capturing straight
/// into a 720p buffer instead of a 1080p one cuts that readback ~2× — the single biggest lever for
/// a VM host, where readback (not encode or network) dominates the frame time.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly Rectangle _bounds;
    private Bitmap _bitmap;
    private Graphics _graphics;
    private int _dstW, _dstH;         // current destination (capture) size
    private int _targetW, _targetH;   // requested source-scale target; 0 = native
    // Two frames, used alternately, so the session can encode one while the next capture is
    // already being written (capture/encode pipelining).
    private readonly CapturedFrame[] _frames = { new(), new() };
    private int _frameIndex;

    public DisplayInfo Display { get; }
    public string BackendName => "GDI-BitBlt";

    public GdiScreenCapture(DisplayInfo display)
    {
        Display = display;
        _bounds = new Rectangle(display.X, display.Y, display.Width, display.Height);
        AllocateDestination(display.Width, display.Height);
    }

    /// <summary>
    /// Ask the capturer to read the screen back already scaled to fit within (w,h), preserving
    /// aspect ratio. Pass (0,0) for native. Cheap to call every frame — it only reallocates when the
    /// resulting size actually changes.
    /// </summary>
    public void SetTargetSize(int w, int h)
    {
        _targetW = w;
        _targetH = h;
        var (cw, ch) = ResolveCaptureSize();
        if (cw != _dstW || ch != _dstH) AllocateDestination(cw, ch);
    }

    private (int W, int H) ResolveCaptureSize()
    {
        if (_targetW <= 0 || _targetH <= 0) return (_bounds.Width, _bounds.Height);
        double k = Math.Min((double)_targetW / _bounds.Width, (double)_targetH / _bounds.Height);
        if (k >= 0.999) return (_bounds.Width, _bounds.Height);
        return (Math.Max(2, (int)(_bounds.Width * k) & ~1), Math.Max(2, (int)(_bounds.Height * k) & ~1));
    }

    private void AllocateDestination(int w, int h)
    {
        _graphics?.Dispose();
        _bitmap?.Dispose();
        _dstW = w;
        _dstH = h;
        _bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public CapturedFrame? Capture(int timeoutMs)
    {
        bool scaled = _dstW != _bounds.Width || _dstH != _bounds.Height;
        // Always blit through the raw GDI path with CAPTUREBLT. CAPTUREBLT is what pulls *layered*
        // windows into the copy — modern (Win11) context menus, dropdowns, tooltips, drop-shadows,
        // and the login/lock UI are all layered. Without it, on hosts where DWM does not composite
        // those into the framebuffer a plain BitBlt reads (VMs, basic/virtual display drivers, inside
        // an RDP session), pop-up menus simply never appear in the captured frame — they "disappear"
        // for the viewer. On a modern desktop GPU the flag is a no-op on output but costs a hair more;
        // that trade is worth it because a remote session where right-click menus vanish is unusable.
        // Managed Graphics.CopyFromScreen can't express SRCCOPY|CAPTUREBLT (it validates the ROP
        // against the CopyPixelOperation enum and the combined value isn't a member), so we P/Invoke.
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr dstDc = _graphics.GetHdc();
        try
        {
            if (!scaled)
            {
                BitBlt(dstDc, 0, 0, _dstW, _dstH, screenDc, _bounds.X, _bounds.Y, SRCCOPY | CAPTUREBLT);
            }
            else
            {
                // Scale during the blit so the CPU-visible buffer (and its readback) is only dst-sized.
                // COLORONCOLOR (drops whole lines — cheap) rather than HALFTONE (resamples the whole
                // source — measured 2-3× slower on weak/virtual GPUs). On capture-bound hosts, speed of
                // the shrink matters far more than smoothness, and the viewer upscales HighQuality which
                // already softens the result. This is deliberately the fast path.
                SetStretchBltMode(dstDc, STRETCH_COLORONCOLOR);
                StretchBlt(dstDc, 0, 0, _dstW, _dstH,
                           screenDc, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SRCCOPY | CAPTUREBLT);
            }
        }
        finally
        {
            _graphics.ReleaseHdc(dstDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var data = _bitmap.LockBits(new Rectangle(0, 0, _dstW, _dstH),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var frame = _frames[_frameIndex];
            _frameIndex ^= 1;

            int required = data.Stride * _dstH;
            if (frame.Bgra.Length < required) frame.Bgra = new byte[required];
            Marshal.Copy(data.Scan0, frame.Bgra, 0, required);

            frame.Width = _dstW;
            frame.Height = _dstH;
            frame.Stride = data.Stride;
            frame.TimestampTicks = DateTime.UtcNow.Ticks;
            frame.DirtyRects = null; // unknown; encoder diffs the whole frame
            return frame;
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }

    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var list = new List<DisplayInfo>();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new DisplayInfo(
                Index: i,
                DeviceName: s.DeviceName,
                X: s.Bounds.X, Y: s.Bounds.Y,
                Width: s.Bounds.Width, Height: s.Bounds.Height,
                IsPrimary: s.Primary,
                RefreshHz: QueryRefreshHz(s.DeviceName)));
        }
        return list;
    }

    internal static int QueryRefreshHz(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 60;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int STRETCH_COLORONCOLOR = 3;
    private const int STRETCH_HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;
    // Include layered windows (pop-up menus, tooltips, drop-shadows, the login/lock UI) in the copy.
    private const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }
}
