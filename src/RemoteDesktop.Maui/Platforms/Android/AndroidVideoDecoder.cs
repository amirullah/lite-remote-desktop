using Android.Media;
using Android.Views;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Maui;

/// <summary>
/// H.264 video decoder for Android via <see cref="MediaCodec"/>, rendering decoded frames straight to a
/// <see cref="Surface"/> (no YUV copy — the fast path). Feeds the complete Annex-B access unit from each
/// VideoFrame (see docs/PROTOCOL-SPEC.md §9); SPS/PPS are inline in the keyframe so no out-of-band
/// codec config is needed.
///
/// NOTE: on-device path — verified to COMPILE; runtime decode needs a real device + host to confirm.
/// </summary>
public sealed class AndroidVideoDecoder : IVideoDecoder
{
    private readonly Surface _surface;
    private readonly object _gate = new();
    private MediaCodec? _codec;
    private int _width, _height;
    private bool _started;

    public AndroidVideoDecoder(Surface surface) => _surface = surface;

    public void Configure(int width, int height, VideoCodec codec)
    {
        lock (_gate)
        {
            if (_started && width == _width && height == _height) return;
            _width = width;
            _height = height;
            ReleaseCodec();

            var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, width, height);
            try { format.SetInteger("low-latency", 1); } catch { /* API < 30 */ }

            var mc = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc!);
            mc.Configure(format, _surface, (MediaCrypto?)null, MediaCodecConfigFlags.None);
            mc.Start();
            _codec = mc;
            _started = true;
        }
    }

    public void SubmitFrame(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;
        var accessUnit = tiles[0].Data.ToArray();

        lock (_gate)
        {
            var codec = _codec;
            if (codec is null) return;
            try
            {
                int inIndex = codec.DequeueInputBuffer(10_000);
                if (inIndex >= 0)
                {
                    var buffer = codec.GetInputBuffer(inIndex);
                    if (buffer is not null)
                    {
                        buffer.Clear();
                        buffer.Put(accessUnit);
                        // Presentation timestamp is monotonic; the exact value doesn't matter for our
                        // low-latency "render as soon as decoded" playback.
                        codec.QueueInputBuffer(inIndex, 0, accessUnit.Length, frameId * 1000L,
                            MediaCodecBufferFlags.None);
                    }
                }

                var info = new MediaCodec.BufferInfo();
                int outIndex;
                while ((outIndex = codec.DequeueOutputBuffer(info, 0)) >= 0)
                    codec.ReleaseOutputBuffer(outIndex, render: true); // render to the Surface
            }
            catch
            {
                // Transient MediaCodec hiccup — drop this frame; the next keyframe recovers.
            }
        }
    }

    private void ReleaseCodec()
    {
        try { _codec?.Stop(); } catch { }
        try { _codec?.Release(); } catch { }
        _codec = null;
        _started = false;
    }

    public void Dispose()
    {
        lock (_gate) ReleaseCodec();
    }
}
