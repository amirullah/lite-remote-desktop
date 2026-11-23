using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Android.Media;
using Android.OS;
using Android.Views;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Maui;

/// <summary>
/// H.264 video decoder for Android via <see cref="MediaCodec"/> in ASYNC (callback) mode, rendering
/// straight to a <see cref="Surface"/> (no YUV copy). Why async: in the old synchronous loop a decoded
/// frame was only drained when the NEXT frame arrived, adding a frame of latency — and on a still screen
/// the last update never showed. Here the codec calls us the moment a frame is decoded and we render it.
///
/// Robustness: a dropped delta frame would make every following frame decode from broken references
/// (grey smearing), so after any drop we discard input until the next keyframe and ask the host for one
/// (<see cref="KeyFrameNeeded"/>). A codec that dies (fatal CodecException, or reclaimed by the system)
/// is rebuilt automatically instead of freezing the picture for the rest of the session.
/// </summary>
public sealed class AndroidVideoDecoder : IVideoDecoder
{
    private Surface _surface; // tracks SetSurface so a later rebuild never targets a released surface
    private readonly object _gate = new();
    private MediaCodec? _codec;
    private Callback? _callback;
    private HandlerThread? _thread;          // codec callbacks run here, off the UI + network threads
    private int _width, _height;
    private bool _started;
    private bool _awaitKeyFrame = true;      // nothing decodable until the first IDR
    private long _lastKeyRequest;
    private long _lastBuildFail;             // back-off so a codec that can't be built isn't retried every frame

    /// <summary>Raised (rate-limited) when the decoder needs a fresh keyframe from the host.</summary>
    public Action? KeyFrameNeeded { get; set; }

    public AndroidVideoDecoder(Surface surface) => _surface = surface;

    public void Configure(int width, int height, VideoCodec codec)
    {
        lock (_gate)
        {
            bool dead = _callback?.Dead == true;
            if (_started && !dead && width == _width && height == _height) return;
            _width = width;
            _height = height;
            BuildCodec();
        }
    }

    /// <summary>(Re)create and start the codec for the current size. Caller holds <see cref="_gate"/>.</summary>
    private void BuildCodec()
    {
        ReleaseCodec();
        if (_width <= 0 || _height <= 0) return;

        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc!, _width, _height);
        // Latency/priority hints (ignored where unsupported): render ASAP, schedule as real-time.
        try { format.SetInteger("low-latency", 1); } catch { }     // API 30+
        try { format.SetInteger("priority", 0); } catch { }        // 0 = real-time
        try { format.SetInteger("operating-rate", 60); } catch { }

        MediaCodec? mc = null;
        try
        {
            _thread = new HandlerThread("LiteRemote-Decoder");
            _thread.Start();
            mc = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc!);
            _callback = new Callback();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                mc.SetCallback(_callback, new Handler(_thread.Looper!));
            else
                mc.SetCallback(_callback);
            mc.Configure(format, _surface, (MediaCrypto?)null, MediaCodecConfigFlags.None);
            mc.Start();
            _codec = mc;
            _started = true;
        }
        catch
        {
            // A decoder failure should drop frames, not end the session; the next frame/config retries.
            try { mc?.Release(); } catch { }
            StopThread();
            _codec = null;
            _callback = null;
            _started = false;
            _lastBuildFail = System.Environment.TickCount64;
        }
        _awaitKeyFrame = true;   // a fresh codec can only start from an IDR
    }

    public void SubmitFrame(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;
        var data = tiles[0].Data;
        bool isKey = flags.HasFlag(FrameFlags.KeyFrame);

        MediaCodec? codec; Callback? cb;
        lock (_gate)
        {
            // The codec died (fatal error / reclaimed): rebuild it rather than freezing forever.
            // Back off ~1 s after a failed build (e.g. resolution unsupported by this phone's decoder) instead
            // of churning a HandlerThread + MediaCodec on the network thread for every frame.
            if ((_callback?.Dead == true || (!_started && _width > 0)) &&
                System.Environment.TickCount64 - _lastBuildFail > 1000)
                BuildCodec();
            // After a drop/rebuild, delta frames would decode from broken references — wait for an IDR.
            if (_awaitKeyFrame && !isKey) { RequestKeyFrame(); return; }
            codec = _codec; cb = _callback;
        }
        if (codec is null || cb is null) { RequestKeyFrame(); return; }

        // Free input slot; wait only briefly so the network thread (input echo, control) never stalls.
        if (!cb.FreeInputs.TryTake(out int index, 5))
        {
            lock (_gate) { _awaitKeyFrame = true; RequestKeyFrame(); }
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(codec, _codec)) return;   // reconfigured meanwhile — slot belongs to the old codec
            try
            {
                var buffer = codec.GetInputBuffer(index);
                if (buffer is null) { _awaitKeyFrame = true; RequestKeyFrame(); return; }
                buffer.Clear();
                // Write straight from the receive buffer when possible (no extra managed copy). Put copies
                // synchronously, so the buffer may be reused as soon as we return.
                if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> seg) && seg.Array is not null)
                    buffer.Put(seg.Array, seg.Offset, seg.Count);
                else
                    buffer.Put(data.ToArray());
                long ptsUs = frameId * 1000L;   // monotonic; exact value irrelevant for "render ASAP"
                codec.QueueInputBuffer(index, 0, data.Length, ptsUs,
                    isKey ? MediaCodecBufferFlags.KeyFrame : MediaCodecBufferFlags.None);
                if (isKey) _awaitKeyFrame = false;
            }
            catch
            {
                // Transient MediaCodec hiccup — this frame is lost, so resync on the next keyframe.
                _awaitKeyFrame = true;
                RequestKeyFrame();
            }
        }
    }

    /// <summary>Ask the host for an IDR, at most every 500 ms.</summary>
    private void RequestKeyFrame()
    {
        long now = System.Environment.TickCount64;
        if (now - _lastKeyRequest < 500) return;
        _lastKeyRequest = now;
        try { KeyFrameNeeded?.Invoke(); } catch { }
    }

    /// <summary>
    /// Repoint the running decoder at a new render surface (e.g. after the page returns to the
    /// foreground and the TextureView recreates its SurfaceTexture) without rebuilding the codec.
    /// </summary>
    public void SetSurface(Surface surface)
    {
        lock (_gate)
        {
            _surface = surface;
            try { _codec?.SetOutputSurface(surface); }
            catch { BuildCodec(); }   // unsupported (API < 23) or failed: rebuild on the new surface
        }
    }

    private void ReleaseCodec()
    {
        try { _codec?.Stop(); } catch { }
        try { _codec?.Release(); } catch { }
        _codec = null;
        _callback = null;
        _started = false;
        StopThread();
    }

    private void StopThread()
    {
        try { _thread?.QuitSafely(); } catch { }
        _thread = null;
    }

    public void Dispose()
    {
        lock (_gate) ReleaseCodec();
    }

    /// <summary>MediaCodec async callbacks: collect free input slots; render each decoded frame at once.</summary>
    private sealed class Callback : MediaCodec.Callback
    {
        public readonly BlockingCollection<int> FreeInputs = new(new ConcurrentQueue<int>());
        public volatile bool Dead;

        public override void OnInputBufferAvailable(MediaCodec codec, int index) => FreeInputs.Add(index);

        public override void OnOutputBufferAvailable(MediaCodec codec, int index, MediaCodec.BufferInfo info)
        {
            try { codec.ReleaseOutputBuffer(index, render: true); } catch { }
        }

        public override void OnError(MediaCodec codec, MediaCodec.CodecException e)
        {
            // Transient = retry the same call later; anything else leaves the codec unusable → rebuild.
            if (!e.IsTransient) Dead = true;
        }

        public override void OnOutputFormatChanged(MediaCodec codec, MediaFormat format) { }
    }
}
