using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// GDI BitBlt capture. Universally available — works inside VMs, over RDP, and anywhere the DXGI
/// duplication path is unavailable. Slower than duplication (CPU copy every frame) but the encoder's
/// dirty-tile diffing keeps bandwidth low regardless.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly Rectangle _bounds;
    private Bitmap _bitmap;
    private Graphics _graphics;
    private readonly CapturedFrame _frame = new();

    public DisplayInfo Display { get; }
    public string BackendName => "GDI-BitBlt";

    public GdiScreenCapture(DisplayInfo display)
    {
        Display = display;
        _bounds = new Rectangle(display.X, display.Y, display.Width, display.Height);
        _bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public CapturedFrame? Capture(int timeoutMs)
    {
        // GDI has no change signal, so we always grab; the encoder decides what actually changed.
        _graphics.CopyFromScreen(_bounds.Location, Point.Empty, _bounds.Size, CopyPixelOperation.SourceCopy);

        var data = _bitmap.LockBits(new Rectangle(0, 0, _bounds.Width, _bounds.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int required = data.Stride * _bounds.Height;
            if (_frame.Bgra.Length < required) _frame.Bgra = new byte[required];
            Marshal.Copy(data.Scan0, _frame.Bgra, 0, required);

            _frame.Width = _bounds.Width;
            _frame.Height = _bounds.Height;
            _frame.Stride = data.Stride;
            _frame.TimestampTicks = DateTime.UtcNow.Ticks;
            _frame.DirtyRects = null; // unknown; encoder diffs the whole frame
            return _frame;
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

    private static int QueryRefreshHz(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 60;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

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
