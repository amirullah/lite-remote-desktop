using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Media;

/// <summary>
/// H.264 decoder (Media Foundation Transform). Milestone M2 of docs/H264-PLAN.md — the receiving half
/// of the codec. Takes Annex-B NAL units (as produced by <see cref="H264Encoder"/>), decodes them with
/// the Microsoft/hardware H.264 decoder MFT to NV12, and converts to top-down BGRA for the viewer's
/// <c>WriteableBitmap</c>.
///
/// It uses the synchronous software decoder (universally present, simple ProcessInput/ProcessOutput),
/// which is plenty for decode — the expensive side is encode. Lives in the Host project for now so the
/// <c>--bench-h264</c> round-trip can prove both halves on one machine; M4 moves it into the client.
/// </summary>
internal sealed class H264Decoder : IDisposable
{
    private int _width, _height;
    private readonly int _fps;
    private IMFTransform _transform;
    private byte[] _bgra = Array.Empty<byte>();
    private byte[] _nv12 = Array.Empty<byte>();

    /// <summary>Last decoded frame dimensions (may change after the decoder parses SPS).</summary>
    public int Width => _width;
    public int Height => _height;
    public string Info { get; }

    private H264Decoder(int width, int height, int fps, IMFTransform transform, string info)
    {
        _width = width; _height = height; _fps = fps; _transform = transform; Info = info;
        Alloc();
    }

    private void Alloc()
    {
        _bgra = new byte[_width * _height * 4];
        _nv12 = new byte[_width * _height * 3 / 2];
    }

    public static H264Decoder? TryCreate(int width, int height, int fps, out string reason)
    {
        reason = "";
        width &= ~1; height &= ~1;
        MediaFactory.MFStartup(true);
        IntPtr activateArr = IntPtr.Zero;
        int count = 0;
        try
        {
            var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };
            IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(inInfo, inPtr, false);
            int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, inPtr, IntPtr.Zero, out activateArr, out count);
            Marshal.FreeHGlobal(inPtr);
            if (hr != 0 || count == 0) { reason = "tidak ada decoder H.264"; return null; }

            IntPtr chosen = Marshal.ReadIntPtr(activateArr, 0);
            for (int i = 1; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(activateArr, i * IntPtr.Size));

            using var activate = new IMFActivate(chosen);
            string name;
            try { name = activate.GetAllocatedString(MFT_FRIENDLY_NAME_Attribute); } catch { name = "H264 Decoder MFT"; }
            var transform = activate.ActivateObject<IMFTransform>();

            var attrs = transform.Attributes;
            if (attrs != null) { try { attrs.Set(MF_LOW_LATENCY, true); } catch { } }

            // Input = H.264.
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            inType.Set(MF_MT_INTERLACE_MODE, 2u);
            inType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            inType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
            transform.SetInputType(0, inType, 0);

            // Output = NV12. (The decoder will renegotiate on the first frame if geometry differs.)
            SetNv12Output(transform, width, height);

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            return new H264Decoder(width, height, fps, transform, name);
        }
        catch (Exception ex)
        {
            reason = $"init decoder gagal: {ex.Message}";
            return null;
        }
        finally
        {
            if (activateArr != IntPtr.Zero) CoTaskMemFree(activateArr);
        }
    }

    private static void SetNv12Output(IMFTransform transform, int width, int height)
    {
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        outType.Set(MF_MT_INTERLACE_MODE, 2u);
        outType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
        transform.SetOutputType(0, outType, 0);
    }

    /// <summary>
    /// Feed one Annex-B frame; returns the decoded BGRA buffer (top-down, stride = Width*4) or null if
    /// the decoder needs more data before it can emit a picture. The returned array is reused between
    /// calls — copy it if you need to keep it.
    /// </summary>
    public byte[]? Decode(byte[] annexB, out int stride)
    {
        stride = _width * 4;
        var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(annexB, 0, ptr, annexB.Length);
        buffer.Unlock();
        buffer.CurrentLength = annexB.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();

        try { _transform.ProcessInput(0, sample, 0); }
        catch { sample.Dispose(); return null; }
        sample.Dispose();

        byte[]? result = null;
        while (DrainOne(ref result, ref stride)) { }
        return result;
    }

    private bool DrainOne(ref byte[]? result, ref int stride)
    {
        var info = _transform.GetOutputStreamInfo(0);
        int size = info.Size > 0 ? info.Size : _width * _height * 3 / 2;

        var db = new OutputDataBuffer { StreamID = 0 };
        var outSample = MediaFactory.MFCreateSample();
        var outBuf = MediaFactory.MFCreateMemoryBuffer(size);
        outSample.AddBuffer(outBuf);
        db.Sample = outSample;

        Result r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

        if (r.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            outSample.Dispose();
            return false;
        }
        if (r.Code == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            // The decoder parsed SPS and wants a new output type; adopt the real geometry and retry.
            outSample.Dispose();
            UpdateGeometryFromStream();
            SetNv12Output(_transform, _width, _height);
            return true;
        }
        if (r.Failure || db.Sample == null) { outSample.Dispose(); return false; }

        try
        {
            using var contig = db.Sample.ConvertToContiguousBuffer();
            contig.Lock(out IntPtr ptr, out _, out int cur);
            if (cur >= _width * _height * 3 / 2)
            {
                Nv12ToBgra(ptr);
                result = _bgra;
                stride = _width * 4;
            }
            contig.Unlock();
        }
        finally
        {
            db.Sample.Dispose();
        }
        return false; // one picture per input frame is enough for our stream
    }

    private void UpdateGeometryFromStream()
    {
        try
        {
            var outType = _transform.GetOutputAvailableType(0, 0);
            ulong packed = outType.GetUInt64(MF_MT_FRAME_SIZE);
            int w = (int)(packed >> 32), h = (int)(packed & 0xffffffff);
            if (w > 0 && h > 0 && (w != _width || h != _height))
            {
                _width = w & ~1; _height = h & ~1;
                Alloc();
            }
        }
        catch { }
    }

    /// <summary>NV12 → BGRA (BT.601), top-down. Rows parallelized.</summary>
    private unsafe void Nv12ToBgra(IntPtr nv12)
    {
        int w = _width, h = _height, ySize = w * h;
        byte* src = (byte*)nv12;
        fixed (byte* dstFixed = _bgra)
        {
            byte* dst = dstFixed;
            byte* uvPlane = src + ySize;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                byte* yr = src + (long)y * w;
                byte* uvr = uvPlane + (long)(y >> 1) * w;
                byte* dr = dst + (long)y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int c = yr[x] - 16;
                    int d = uvr[x & ~1] - 128;      // U
                    int e = uvr[(x & ~1) + 1] - 128; // V
                    int r = (298 * c + 409 * e + 128) >> 8;
                    int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                    int b = (298 * c + 516 * d + 128) >> 8;
                    byte* px = dr + x * 4;
                    px[0] = Clamp(b); px[1] = Clamp(g); px[2] = Clamp(r); px[3] = 255;
                }
            });
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Dispose()
    {
        try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        _transform?.Dispose();
        try { MediaFactory.MFShutdown(); } catch { }
    }

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    private const int MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid guidCategory, int flags, IntPtr inputType, IntPtr outputType,
        out IntPtr pppMFTActivate, out int numMFTActivate);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);
}
