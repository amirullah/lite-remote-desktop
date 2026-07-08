using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Host;

/// <summary>
/// H.264 encoder (Media Foundation Transform). Milestone M1 of docs/H264-PLAN.md.
///
/// Prefers a hardware MFT (NVENC / AMD VCE / Intel QuickSync — async, event-driven) and falls back to
/// the Microsoft software MFT (synchronous) so a valid H.264 elementary stream is always produced when
/// any encoder exists. Input is BGRA (from the capture path), converted to NV12 on the CPU; output is
/// Annex-B NAL units per frame. Configured for low latency (no B-frames, CBR, LOW_LATENCY attribute).
///
/// This class is self-contained and used only behind <c>--bench-h264</c> for now; it does NOT touch the
/// default JPEG-tiles streaming path. Integration (M3/M4) wires it into HostSession later, always with a
/// JPEG fallback so the app can never break.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    private readonly int _width, _height, _fps;
    private readonly bool _async;               // true = hardware event-driven MFT
    private IMFTransform _transform;
    private IMFMediaEventGenerator? _events;    // async path only
    private readonly bool _providesSamples;     // MFT allocates its own output samples
    private readonly int _outBufSize;
    private long _frameIndex;
    private byte[] _nv12;                        // reusable NV12 scratch

    /// <summary>Human-readable description of the encoder that was selected (name + hw/sw).</summary>
    public string Info { get; }

    private H264Encoder(int width, int height, int fps, bool async, IMFTransform transform,
        bool providesSamples, int outBufSize, string info)
    {
        _width = width; _height = height; _fps = fps; _async = async;
        _transform = transform; _providesSamples = providesSamples;
        _outBufSize = Math.Max(outBufSize, width * height); // generous ceiling for compressed output
        _nv12 = new byte[width * height * 3 / 2];
        Info = info;
        if (async)
            _events = transform.QueryInterface<IMFMediaEventGenerator>();
    }

    /// <summary>
    /// Create an encoder for the given geometry/bitrate, or return null (with a reason) if no usable
    /// H.264 MFT exists — the caller then stays on JPEG tiles.
    /// </summary>
    /// <param name="hardwareOnly">
    /// When true, only a hardware (GPU) encoder is accepted; if none exists we return null so the
    /// caller stays on JPEG. Live streaming passes this — the Microsoft software encoder is too slow
    /// for a real session (and often produces nothing usable in a VM), and H.264 only pays off with a
    /// GPU encoder anyway. The bench/self-test pass false so they can still exercise software.
    /// </param>
    public static H264Encoder? TryCreate(int width, int height, int fps, int bitrateBps,
        bool preferHardware, out string reason, bool hardwareOnly = false)
    {
        reason = "";
        width &= ~1; height &= ~1;              // H.264 requires even dimensions
        if (width < 16 || height < 16) { reason = "resolusi terlalu kecil"; return null; }

        MediaFactory.MFStartup(true);

        // Try hardware (async) first when requested, then software (sync) unless hardware is required.
        var order = hardwareOnly
            ? new[] { (true, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER) }
            : preferHardware
            ? new[] { (true, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER),
                      (false, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER) }
            : new[] { (false, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER) };

        foreach (var (isAsync, flags) in order)
        {
            var enc = TryBuild(width, height, fps, bitrateBps, isAsync, flags, out string err);
            if (enc != null) return enc;
            reason = err;
        }
        return null;
    }

    private static H264Encoder? TryBuild(int width, int height, int fps, int bitrateBps,
        bool isAsync, int enumFlags, out string reason)
    {
        reason = "";
        IntPtr activateArr = IntPtr.Zero;
        int count = 0;
        try
        {
            var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };
            IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(outInfo, outPtr, false);
            int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, enumFlags, IntPtr.Zero, outPtr, out activateArr, out count);
            Marshal.FreeHGlobal(outPtr);
            if (hr != 0 || count == 0) { reason = isAsync ? "tidak ada encoder hardware" : "tidak ada encoder software"; return null; }

            // Take the first (SORTANDFILTER orders best-first); release the rest.
            IntPtr chosen = Marshal.ReadIntPtr(activateArr, 0);
            for (int i = 1; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(activateArr, i * IntPtr.Size));

            using var activate = new IMFActivate(chosen);
            string name = SafeName(activate);
            var transform = activate.ActivateObject<IMFTransform>();

            // Unlock async hardware MFTs and request low latency BEFORE setting media types.
            var attrs = transform.Attributes;
            if (attrs != null)
            {
                if (isAsync) TrySet(() => attrs.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u));
                TrySet(() => attrs.Set(MF_LOW_LATENCY, true));
            }

            // Output = H.264.
            var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outType.Set(MF_MT_AVG_BITRATE, (uint)bitrateBps);
            outType.Set(MF_MT_INTERLACE_MODE, 2u);                 // MFVideoInterlace_Progressive
            outType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            outType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
            outType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            outType.Set(MF_MT_MPEG2_PROFILE, 77u);                 // eAVEncH264VProfile_Main
            transform.SetOutputType(0, outType, 0);

            // Input = NV12 (must be set after output for encoders).
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inType.Set(MF_MT_INTERLACE_MODE, 2u);
            inType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            inType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
            inType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            transform.SetInputType(0, inType, 0);

            var streamInfo = transform.GetOutputStreamInfo(0);
            bool provides = (streamInfo.Flags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            string info = $"{name} [{(isAsync ? "hardware/async" : "software/sync")}]";
            return new H264Encoder(width, height, fps, isAsync, transform, provides, streamInfo.Size, info);
        }
        catch (Exception ex)
        {
            reason = $"init MFT gagal: {ex.Message}";
            return null;
        }
        finally
        {
            if (activateArr != IntPtr.Zero) CoTaskMemFree(activateArr);
        }
    }

    /// <summary>
    /// Encode one BGRA frame. Returns the Annex-B bytes for this frame (may be empty if the encoder is
    /// still buffering), and whether it was a keyframe (IDR). Not thread-safe; call from one thread.
    /// </summary>
    public byte[] Encode(byte[] bgra, int stride, out bool keyframe)
    {
        keyframe = false;
        BgraToNv12(bgra, stride);

        long pts = _frameIndex * 10_000_000L / Math.Max(_fps, 1);
        long dur = 10_000_000L / Math.Max(_fps, 1);
        _frameIndex++;

        var inputSample = MakeNv12Sample(pts, dur);

        return _async
            ? EncodeAsync(inputSample, out keyframe)
            : EncodeSync(inputSample, out keyframe);
    }

    private byte[] EncodeSync(IMFSample inputSample, out bool keyframe)
    {
        keyframe = false;
        _transform.ProcessInput(0, inputSample, 0);
        inputSample.Dispose();

        using var ms = new System.IO.MemoryStream();
        while (DrainOne(ms, ref keyframe)) { }
        return ms.ToArray();
    }

    private byte[] EncodeAsync(IMFSample inputSample, out bool keyframe)
    {
        keyframe = false;
        using var ms = new System.IO.MemoryStream();
        bool fed = false;
        // Pump events until the encoder has consumed our input and emitted its output.
        while (!fed || ms.Length == 0)
        {
            using var ev = _events!.GetEvent(0);
            var type = ev.EventType;
            if (type == MediaEventTypes.TransformNeedInput && !fed)
            {
                _transform.ProcessInput(0, inputSample, 0);
                inputSample.Dispose();
                fed = true;
            }
            else if (type == MediaEventTypes.TransformHaveOutput)
            {
                DrainOne(ms, ref keyframe);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Pull one output sample. Returns true if a sample was produced (sync: keep draining).</summary>
    private bool DrainOne(System.IO.MemoryStream ms, ref bool keyframe)
    {
        var db = new OutputDataBuffer { StreamID = 0 };
        IMFSample? owned = null;
        if (!_providesSamples)
        {
            owned = MediaFactory.MFCreateSample();
            var buf = MediaFactory.MFCreateMemoryBuffer(_outBufSize);
            owned.AddBuffer(buf);
            db.Sample = owned;
        }

        Result r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);
        if (r.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            owned?.Dispose();
            return false;
        }
        if (r.Failure || db.Sample == null)
        {
            owned?.Dispose();
            return false;
        }

        var sample = db.Sample;
        try
        {
            // CleanPoint attribute (1) marks an IDR keyframe.
            if (SafeGetU32(sample, MFSampleExtension_CleanPoint) == 1) keyframe = true;

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr ptr, out _, out int cur);
            if (cur > 0)
            {
                var chunk = new byte[cur];
                Marshal.Copy(ptr, chunk, 0, cur);
                ms.Write(chunk, 0, cur);
            }
            buffer.Unlock();
        }
        finally
        {
            // If the MFT provided the sample, release it; if we owned it, dispose ours.
            if (_providesSamples) sample.Dispose();
            else owned?.Dispose();
        }
        return !_providesSamples; // sync: try to drain more; async: one per HaveOutput event
    }

    private IMFSample MakeNv12Sample(long pts, long dur)
    {
        int len = _nv12.Length;
        var buffer = MediaFactory.MFCreateMemoryBuffer(len);
        buffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(_nv12, 0, ptr, len);
        buffer.Unlock();
        buffer.CurrentLength = len;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts;
        sample.SampleDuration = dur;
        buffer.Dispose();
        return sample;
    }

    /// <summary>
    /// BGRA (little-endian B,G,R,A) → NV12 (BT.601), nearest-neighbour chroma subsample. Rows are
    /// independent, so we split the work across cores — 1080p conversion drops from ~9 ms to ~2 ms and
    /// stops dominating the per-frame budget.
    /// </summary>
    private unsafe void BgraToNv12(byte[] bgra, int stride)
    {
        int w = _width, h = _height;
        int ySize = w * h;
        fixed (byte* srcFixed = bgra)
        fixed (byte* dstFixed = _nv12)
        {
            byte* src = srcFixed, dst = dstFixed;
            byte* uvPlane = dst + ySize;
            // Process pairs of rows per iteration so each chunk owns whole 2×2 chroma blocks.
            System.Threading.Tasks.Parallel.For(0, (h + 1) / 2, yp =>
            {
                int y0 = yp * 2;
                for (int dy = 0; dy < 2 && y0 + dy < h; dy++)
                {
                    int y = y0 + dy;
                    byte* row = src + (long)y * stride;
                    byte* yr = dst + (long)y * w;
                    byte* uv = uvPlane + (long)yp * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 4;
                        int b = p[0], g = p[1], rr = p[2];
                        yr[x] = (byte)(((66 * rr + 129 * g + 25 * b + 128) >> 8) + 16);
                        if (dy == 0 && (x & 1) == 0)
                        {
                            uv[x] = (byte)(((-38 * rr - 74 * g + 112 * b + 128) >> 8) + 128);     // U
                            uv[x + 1] = (byte)(((112 * rr - 94 * g - 18 * b + 128) >> 8) + 128);  // V
                        }
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _transform?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
        _events?.Dispose();
        _transform?.Dispose();
        try { MediaFactory.MFShutdown(); } catch { }
    }

    // ---- helpers ----
    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
    private static void TrySet(Action a) { try { a(); } catch { } }

    private static uint SafeGetU32(IMFAttributes a, Guid key)
    {
        try { return a.GetUInt32(key); } catch { return 0; }
    }

    private static string SafeName(IMFActivate activate)
    {
        try { return activate.GetAllocatedString(MFT_FRIENDLY_NAME_Attribute); }
        catch { return "H264 Encoder MFT"; }
    }

    // ---- Media Foundation constants / interop ----
    private const int MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2,
        MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid guidCategory, int flags, IntPtr inputType, IntPtr outputType,
        out IntPtr pppMFTActivate, out int numMFTActivate);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);
}
