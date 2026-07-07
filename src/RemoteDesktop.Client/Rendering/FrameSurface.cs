using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Client.Rendering;

/// <summary>
/// Maintains the on-screen bitmap of the remote desktop. Tiles arrive JPEG-compressed; we decode
/// them on the network thread (cheap, parallel-friendly) and then blit all of a frame's tiles onto
/// the <see cref="WriteableBitmap"/> in a single UI-thread pass. This keeps the UI thread doing the
/// minimum — a handful of memcpys per frame — which is what lets it stay smooth at high fps.
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
            if (_bitmap != null && (int)_bitmap.Width == width && (int)_bitmap.Height == height)
                return;
            Width = width;
            Height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            SizeChanged?.Invoke();
        });
    }

    private readonly struct DecodedTile
    {
        public readonly int X, Y, W, H, Stride;
        public readonly byte[] Pixels;
        public DecodedTile(int x, int y, int w, int h, int stride, byte[] px)
        { X = x; Y = y; W = w; H = h; Stride = stride; Pixels = px; }
    }

    /// <summary>Decode + blit a frame's tiles. Safe to call from any thread.</summary>
    public void ApplyFrame(IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;

        var decoded = new List<DecodedTile>(tiles.Count);
        foreach (var t in tiles)
        {
            var px = DecodeTile(t, out int stride);
            if (px != null) decoded.Add(new DecodedTile(t.X, t.Y, t.Width, t.Height, stride, px));
        }
        if (decoded.Count == 0) return;

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var bmp = _bitmap;
            if (bmp is null) return;
            bmp.Lock();
            try
            {
                foreach (var d in decoded)
                {
                    if (d.X + d.W > bmp.PixelWidth || d.Y + d.H > bmp.PixelHeight) continue;
                    bmp.WritePixels(new Int32Rect(0, 0, d.W, d.H), d.Pixels, d.Stride, d.X, d.Y);
                }
            }
            finally
            {
                bmp.Unlock();
            }
        });
    }

    private static byte[]? DecodeTile(in Tile tile, out int stride)
    {
        stride = tile.Width * 4;
        try
        {
            using var ms = new MemoryStream(tile.Data.ToArray(), writable: false);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);

            var px = new byte[stride * tile.Height];
            frame.CopyPixels(px, stride, 0);
            return px;
        }
        catch
        {
            return null;
        }
    }
}
