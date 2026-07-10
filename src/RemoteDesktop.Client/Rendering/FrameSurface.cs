using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesktop.Media;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Client.Rendering;

/// <summary>
/// Maintains the on-screen bitmap of the remote desktop. Tiles arrive JPEG-compressed; decoding
/// runs parallel on the network side into pooled buffers, and the UI thread only performs raw
/// memcpys straight into the WriteableBitmap back buffer (one Lock per batch, one dirty rect per
/// tile). Decoded batches funnel through a queue drained by a single scheduled UI pass, so a burst
/// of frames coalesces into one render instead of piling latency onto the dispatcher.
/// </summary>
public sealed class FrameSurface : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    public ImageSource? Source => _bitmap;

    /// <summary>Release the native H.264 decoder + frame buffers. Call only after the network loop has
    /// stopped (connection disposed) so no ApplyFrame is in flight.</summary>
    public void Dispose()
    {
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        _bitmap = null;
    }

    public event Action? SizeChanged;
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Codec of the current stream. JPEG tiles decode in parallel per-tile; H.264 decodes whole
    // frames through one Media Foundation decoder owned by the network thread (see ApplyFrame).
    private volatile VideoCodec _codec = VideoCodec.JpegTiles;
    private H264Decoder? _decoder;   // touched only on the network (ApplyFrame) thread
    private int _decW, _decH;

    public FrameSurface(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Configure(int width, int height, VideoCodec codec)
    {
        _codec = codec;
        _dispatcher.Invoke(() =>
        {
            if (_bitmap != null && _bitmap.PixelWidth == width && _bitmap.PixelHeight == height)
                return;
            Width = width;
            Height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            // Drop batches decoded for the old geometry.
            while (_pending.TryDequeue(out var stale)) ReturnBatch(stale);
            SizeChanged?.Invoke();
        });
    }

    private readonly struct DecodedTile
    {
        public readonly int X, Y, W, H, Stride;
        public readonly byte[] Pixels; // rented from ArrayPool — returned after blit
        public DecodedTile(int x, int y, int w, int h, int stride, byte[] px)
        { X = x; Y = y; W = w; H = h; Stride = stride; Pixels = px; }
    }

    private readonly ConcurrentQueue<List<DecodedTile>> _pending = new();
    private int _drainScheduled; // 0/1 — only one UI drain pass in flight

    /// <summary>Decode + blit a frame. Called sequentially from the network message loop.</summary>
    public void ApplyFrame(IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;

        if (_codec == VideoCodec.H264)
        {
            ApplyH264Frame(tiles[0]);
            return;
        }

        // JPEG decode is CPU-bound; spread big frames (keyframes, motion) across cores.
        var slots = new DecodedTile?[tiles.Count];
        if (tiles.Count <= 2)
        {
            for (int i = 0; i < tiles.Count; i++) slots[i] = DecodeSlot(tiles[i]);
        }
        else
        {
            var snapshot = tiles;
            Parallel.For(0, snapshot.Count, i => slots[i] = DecodeSlot(snapshot[i]));
        }

        var decoded = new List<DecodedTile>(tiles.Count);
        foreach (var slot in slots)
            if (slot is { } d) decoded.Add(d);
        if (decoded.Count == 0) return;

        _pending.Enqueue(decoded);

        // Schedule a single drain; if one is already queued it will pick this batch up too.
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Render, DrainPending);
    }

    /// <summary>
    /// Decode one H.264 frame (the payload is a single full-frame NAL slice) to BGRA and enqueue it as
    /// one whole-surface tile. Runs on the network thread, which also owns the decoder — Media
    /// Foundation transforms want single-threaded feeding.
    /// </summary>
    private void ApplyH264Frame(in Tile tile)
    {
        var dec = EnsureDecoder();
        if (dec is null) return;

        byte[]? bgra;
        try { bgra = dec.Decode(tile.Data.ToArray(), out _); }
        catch { bgra = null; }
        if (bgra is null) return; // decoder still buffering (before the first IDR)

        // H.264 decoders pad the frame up to a macroblock multiple (e.g. 1080 -> 1088). The visible
        // image is the top-left Width×Height region; the surface bitmap is exactly Width×Height, so we
        // crop to it. Without this the padded tile is taller than the bitmap and the blit's bounds
        // guard drops the whole frame — a black screen even though decoding succeeded.
        int bw = Width, bh = Height;
        if (bw <= 0 || bh <= 0) return;
        int dw = dec.Width, dh = dec.Height;
        int copyW = Math.Min(bw, dw), copyH = Math.Min(bh, dh);
        int dstStride = bw * 4, srcStride = dw * 4;
        var px = ArrayPool<byte>.Shared.Rent(dstStride * bh);
        for (int y = 0; y < copyH; y++)
            Array.Copy(bgra, y * srcStride, px, y * dstStride, copyW * 4); // copy before the next frame overwrites the decoder buffer

        _pending.Enqueue(new List<DecodedTile>(1) { new DecodedTile(0, 0, bw, bh, dstStride, px) });
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Render, DrainPending);
    }

    private H264Decoder? EnsureDecoder()
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return null;
        if (_decoder != null && _decW == w && _decH == h) return _decoder;

        _decoder?.Dispose();
        _decoder = H264Decoder.TryCreate(w, h, 60, out _);
        _decW = w; _decH = h;
        return _decoder;
    }

    private void DrainPending()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        var bmp = _bitmap;
        if (bmp is null)
        {
            while (_pending.TryDequeue(out var stale)) ReturnBatch(stale);
            return;
        }

        bool locked = false;
        try
        {
            while (_pending.TryDequeue(out var batch))
            {
                if (!locked) { bmp.Lock(); locked = true; }
                Blit(bmp, batch);
                ReturnBatch(batch);
            }
        }
        finally
        {
            if (locked) bmp.Unlock();
        }
    }

    private static unsafe void Blit(WriteableBitmap bmp, List<DecodedTile> batch)
    {
        byte* back = (byte*)bmp.BackBuffer;
        int backStride = bmp.BackBufferStride;
        int bw = bmp.PixelWidth, bh = bmp.PixelHeight;

        foreach (var d in batch)
        {
            if (d.X + d.W > bw || d.Y + d.H > bh) continue; // stale tile from a previous geometry
            fixed (byte* src = d.Pixels)
            {
                for (int row = 0; row < d.H; row++)
                {
                    Buffer.MemoryCopy(
                        src + (long)row * d.Stride,
                        back + (long)(d.Y + row) * backStride + (long)d.X * 4,
                        d.Stride, d.Stride);
                }
            }
            bmp.AddDirtyRect(new Int32Rect(d.X, d.Y, d.W, d.H));
        }
    }

    private static void ReturnBatch(List<DecodedTile> batch)
    {
        foreach (var d in batch) ArrayPool<byte>.Shared.Return(d.Pixels);
    }

    private static DecodedTile? DecodeSlot(in Tile t)
    {
        try
        {
            // Decode straight from the frame payload slice — no intermediate copy.
            Stream ms = MemoryMarshal.TryGetArray(t.Data, out var seg)
                ? new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false)
                : new MemoryStream(t.Data.ToArray(), writable: false);
            using (ms)
            {
                var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                if (frame.Format != PixelFormats.Pbgra32)
                    frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);

                int stride = t.Width * 4;
                var px = ArrayPool<byte>.Shared.Rent(stride * t.Height);
                frame.CopyPixels(px, stride, 0);
                return new DecodedTile(t.X, t.Y, t.Width, t.Height, stride, px);
            }
        }
        catch
        {
            return null;
        }
    }
}
