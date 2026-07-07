using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Downscales a captured frame before encoding when the client asked for a reduced stream
/// resolution. Fewer pixels means proportionally less JPEG work and less bandwidth — the single
/// biggest lever for slow links or high-DPI hosts. Buffers are reused across frames.
/// </summary>
public sealed class FrameScaler : IDisposable
{
    private Bitmap? _dst;
    private Graphics? _g;
    private readonly CapturedFrame _out = new();

    public CapturedFrame Scale(CapturedFrame src, int dstW, int dstH)
    {
        dstW = Math.Clamp(dstW & ~1, 160, src.Width);
        dstH = Math.Clamp(dstH & ~1, 90, src.Height);
        if (dstW >= src.Width || dstH >= src.Height) return src;

        if (_dst is null || _dst.Width != dstW || _dst.Height != dstH)
        {
            _g?.Dispose();
            _dst?.Dispose();
            _dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            _g = Graphics.FromImage(_dst);
            _g.CompositingMode = CompositingMode.SourceCopy;
            _g.InterpolationMode = InterpolationMode.Bilinear; // good quality, much cheaper than bicubic
            _g.PixelOffsetMode = PixelOffsetMode.Half;
            _g.SmoothingMode = SmoothingMode.None;
        }

        unsafe
        {
            fixed (byte* p = src.Bgra)
            {
                using var srcBmp = new Bitmap(src.Width, src.Height, src.Stride, PixelFormat.Format32bppArgb, (IntPtr)p);
                _g!.DrawImage(srcBmp,
                    new Rectangle(0, 0, dstW, dstH),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
        }

        var data = _dst!.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int required = data.Stride * dstH;
            if (_out.Bgra.Length < required) _out.Bgra = new byte[required];
            Marshal.Copy(data.Scan0, _out.Bgra, 0, required);
            _out.Width = dstW;
            _out.Height = dstH;
            _out.Stride = data.Stride;
            _out.TimestampTicks = src.TimestampTicks;
            _out.DirtyRects = null; // geometry changed; let the encoder diff everything
            return _out;
        }
        finally
        {
            _dst.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _g?.Dispose();
        _dst?.Dispose();
    }
}
