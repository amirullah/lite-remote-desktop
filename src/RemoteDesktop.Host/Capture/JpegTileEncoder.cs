using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// The baseline codec: split the frame into a tile grid, compare each tile against the last sent
/// state, and JPEG-encode only the tiles that changed. On a typical desktop (a blinking cursor, a
/// bit of text) that's a few KB per frame instead of multiple MB — which is what makes the whole
/// thing feel instant even without a hardware video encoder.
///
/// This class is single-threaded per session and reuses buffers aggressively to stay allocation-light.
/// </summary>
public sealed class JpegTileEncoder : IDisposable
{
    public int TileSize { get; }

    private readonly ImageCodecInfo _jpegCodec;
    private readonly EncoderParameters _params;
    private long _qualityHandle;

    private byte[] _previous = Array.Empty<byte>();
    private int _prevStride;
    private int _width, _height;

    public JpegTileEncoder(int tileSize = 128)
    {
        TileSize = tileSize;
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _params = new EncoderParameters(1);
        SetQuality(75);
    }

    public void SetQuality(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        _params.Param[0]?.Dispose();
        _params.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        _qualityHandle = quality;
    }

    /// <summary>
    /// Diff <paramref name="frame"/> against the previously encoded state and return the changed tiles.
    /// When <paramref name="forceKeyFrame"/> is set every tile is emitted (used on connect, resize,
    /// or in response to a <see cref="MessageType.KeyFrameRequest"/>).
    /// </summary>
    public List<Tile> Encode(CapturedFrame frame, bool forceKeyFrame, out bool wasKeyFrame)
    {
        bool geometryChanged = frame.Width != _width || frame.Height != _height;
        bool keyFrame = forceKeyFrame || geometryChanged || _previous.Length == 0;
        wasKeyFrame = keyFrame;

        if (geometryChanged)
        {
            _width = frame.Width;
            _height = frame.Height;
            _prevStride = frame.Stride;
            _previous = new byte[frame.Stride * frame.Height];
        }

        var tiles = new List<Tile>();
        int cols = (frame.Width + TileSize - 1) / TileSize;
        int rows = (frame.Height + TileSize - 1) / TileSize;

        for (int ty = 0; ty < rows; ty++)
        {
            int y = ty * TileSize;
            int th = Math.Min(TileSize, frame.Height - y);
            for (int tx = 0; tx < cols; tx++)
            {
                int x = tx * TileSize;
                int tw = Math.Min(TileSize, frame.Width - x);

                if (!keyFrame && !TileChanged(frame, x, y, tw, th))
                    continue;

                var jpeg = EncodeTile(frame, x, y, tw, th);
                tiles.Add(new Tile((ushort)x, (ushort)y, (ushort)tw, (ushort)th, jpeg));
                CopyTileToPrevious(frame, x, y, tw, th);
            }
        }

        return tiles;
    }

    private unsafe bool TileChanged(CapturedFrame f, int x, int y, int w, int h)
    {
        int bytesPerRow = w * 4;
        fixed (byte* cur = f.Bgra)
        fixed (byte* prev = _previous)
        {
            for (int row = 0; row < h; row++)
            {
                byte* c = cur + (long)(y + row) * f.Stride + (long)x * 4;
                byte* p = prev + (long)(y + row) * _prevStride + (long)x * 4;

                // Compare 8 bytes at a time for speed; tail handled byte-wise.
                int i = 0;
                for (; i + 8 <= bytesPerRow; i += 8)
                    if (*(long*)(c + i) != *(long*)(p + i)) return true;
                for (; i < bytesPerRow; i++)
                    if (c[i] != p[i]) return true;
            }
        }
        return false;
    }

    private unsafe void CopyTileToPrevious(CapturedFrame f, int x, int y, int w, int h)
    {
        int bytesPerRow = w * 4;
        fixed (byte* cur = f.Bgra)
        fixed (byte* prev = _previous)
        {
            for (int row = 0; row < h; row++)
            {
                byte* c = cur + (long)(y + row) * f.Stride + (long)x * 4;
                byte* p = prev + (long)(y + row) * _prevStride + (long)x * 4;
                Buffer.MemoryCopy(c, p, bytesPerRow, bytesPerRow);
            }
        }
    }

    private byte[] EncodeTile(CapturedFrame f, int x, int y, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                fixed (byte* src = f.Bgra)
                {
                    byte* dstBase = (byte*)data.Scan0;
                    int rowBytes = w * 4;
                    for (int row = 0; row < h; row++)
                    {
                        byte* s = src + (long)(y + row) * f.Stride + (long)x * 4;
                        byte* d = dstBase + (long)row * data.Stride;
                        Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream(4096);
        bmp.Save(ms, _jpegCodec, _params);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _params.Param[0]?.Dispose();
        _params.Dispose();
    }
}
