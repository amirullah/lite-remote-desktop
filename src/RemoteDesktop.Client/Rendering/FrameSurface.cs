using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Client.Rendering;

/// <summary>
/// Maintains the on-screen bitmap of the remote desktop. Tiles arrive JPEG-compressed; decoding
/// runs parallel on the network side into pooled buffers, and the UI thread only performs raw
/// memcpys straight into the WriteableBitmap back buffer (one Lock per batch, one dirty rect per
/// tile). Decoded batches funnel through a queue drained by a single scheduled UI pass, so a burst
/// of frames coalesces into one render instead of piling latency onto the dispatcher.
/// </summary>
public sealed class FrameSurface
{
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    public ImageSource? Source => _bitmap;

    public event Action? SizeChanged;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public FrameSurface(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Configure(int width, int height)
    {
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

    /// <summary>Decode + blit a frame's tiles. Safe to call from any thread.</summary>
    public void ApplyFrame(IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;

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
