using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// The baseline codec: split the frame into a tile grid, compare each tile against the last sent
/// state, and JPEG-encode only the tiles that changed. On a typical desktop (a blinking cursor, a
/// bit of text) that's a few KB per frame instead of multiple MB — which is what makes the whole
/// thing feel instant even without a hardware video encoder.
///
/// The diff pass is a sequential memory sweep (fast), but JPEG compression is CPU-bound, so changed
/// tiles are encoded in parallel across cores with per-thread scratch bitmaps. That keeps keyframes
/// and heavy motion (video, window drags) at a fraction of the single-threaded cost.
/// </summary>
public sealed class JpegTileEncoder : IDisposable
{
    public int TileSize { get; }

    private readonly ImageCodecInfo _jpegCodec;
    private int _quality;

    private byte[] _previous = Array.Empty<byte>();
    private int _prevStride;
    private int _width, _height;

    // Per-thread scratch state so Parallel.For never contends: each worker owns a bitmap per tile
    // geometry plus a reusable stream. GDI+ is safe as long as threads touch distinct objects.
    private sealed class Scratch
    {
        public readonly Dictionary<int, Bitmap> Bitmaps = new();
        public readonly MemoryStream Stream = new(16 * 1024);
        public EncoderParameters? Params;
        public int ParamsQuality = -1;
    }

    private readonly ConcurrentBag<Scratch> _scratchPool = new();

    public JpegTileEncoder(int tileSize = 128)
    {
        TileSize = tileSize;
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _quality = 75;
    }

    public void SetQuality(int quality) => _quality = Math.Clamp(quality, 1, 100);

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

        // Pass 1 — sequential diff sweep to find dirty tiles (memory-bandwidth bound, ~1-2 ms/1080p).
        var dirty = new List<(int X, int Y, int W, int H)>();
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
                if (keyFrame || TileChanged(frame, x, y, tw, th))
                    dirty.Add((x, y, tw, th));
            }
        }
        if (dirty.Count == 0) return new List<Tile>();

        // Pass 2 — parallel JPEG encode of dirty tiles (CPU bound; scales with cores).
        var encoded = new Tile[dirty.Count];
        int quality = _quality;
        if (dirty.Count <= 2)
        {
            for (int i = 0; i < dirty.Count; i++) encoded[i] = EncodeOne(frame, dirty[i], quality);
        }
        else
        {
            Parallel.For(0, dirty.Count, i => encoded[i] = EncodeOne(frame, dirty[i], quality));
        }

        // Pass 3 — remember what we sent (sequential; plain memcpy).
        foreach (var (x, y, w, h) in dirty)
            CopyTileToPrevious(frame, x, y, w, h);

        return new List<Tile>(encoded);
    }

    private Tile EncodeOne(CapturedFrame frame, (int X, int Y, int W, int H) t, int quality)
    {
        if (!_scratchPool.TryTake(out var scratch)) scratch = new Scratch();
        try
        {
            var jpeg = EncodeTile(scratch, frame, t.X, t.Y, t.W, t.H, quality);
            return new Tile((ushort)t.X, (ushort)t.Y, (ushort)t.W, (ushort)t.H, jpeg);
        }
        finally
        {
            _scratchPool.Add(scratch);
        }
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

    private byte[] EncodeTile(Scratch scratch, CapturedFrame f, int x, int y, int w, int h, int quality)
    {
        int key = (w << 16) | h;
        if (!scratch.Bitmaps.TryGetValue(key, out var bmp))
        {
            bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            scratch.Bitmaps[key] = bmp;
        }
        if (scratch.Params is null || scratch.ParamsQuality != quality)
        {
            scratch.Params?.Param[0]?.Dispose();
            scratch.Params?.Dispose();
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            scratch.Params = ep;
            scratch.ParamsQuality = quality;
        }

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

        var ms = scratch.Stream;
        ms.SetLength(0);
        ms.Position = 0;
        bmp.Save(ms, _jpegCodec, scratch.Params);
        return ms.ToArray();
    }

    public void Dispose()
    {
        while (_scratchPool.TryTake(out var s))
        {
            foreach (var bmp in s.Bitmaps.Values) bmp.Dispose();
            s.Params?.Param[0]?.Dispose();
            s.Params?.Dispose();
            s.Stream.Dispose();
        }
    }
}
