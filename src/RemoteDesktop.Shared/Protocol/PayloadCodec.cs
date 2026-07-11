using System.Buffers.Binary;
using System.IO;
using System.Text;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Hand-rolled little-endian (de)serializers for control payloads. We avoid JSON on the hot
/// paths (input, frames) to keep per-message overhead at a handful of bytes and zero reflection.
/// Control/handshake payloads that are rare (settings, display list) use the same primitives.
/// </summary>
public static class PayloadCodec
{
    // ---------- primitive cursor ----------

    private ref struct Cursor
    {
        public Span<byte> Buf;
        public int Pos;
        public Cursor(Span<byte> buf) { Buf = buf; Pos = 0; }

        public void U8(byte v) { Buf[Pos++] = v; }
        public void Bool(bool v) { Buf[Pos++] = v ? (byte)1 : (byte)0; }
        public void I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(Buf[Pos..], v); Pos += 2; }
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Buf[Pos..], v); Pos += 2; }
        public void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Buf[Pos..], v); Pos += 4; }
        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            U16((ushort)bytes.Length);
            bytes.CopyTo(Buf[Pos..]);
            Pos += bytes.Length;
        }
    }

    private ref struct Reader
    {
        public ReadOnlySpan<byte> Buf;
        public int Pos;
        public Reader(ReadOnlySpan<byte> buf) { Buf = buf; Pos = 0; }

        // Guard every read against a truncated/hostile payload so a bad control message is a clean
        // protocol error rather than an out-of-range read. (audit M-A0: AUD-012)
        private void Need(int n)
        {
            if (n < 0 || Pos + n > Buf.Length)
                throw new InvalidDataException($"Payload truncated: need {n} B at offset {Pos}, {Buf.Length - Pos} remaining.");
        }

        public byte U8() { Need(1); return Buf[Pos++]; }
        public bool Bool() { Need(1); return Buf[Pos++] != 0; }
        public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(Buf[Pos..]); Pos += 2; return v; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(Buf[Pos..]); Pos += 2; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(Buf[Pos..]); Pos += 4; return v; }
        public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(Buf[Pos..]); Pos += 8; return v; }
        public string Str()
        {
            int len = U16();
            Need(len);
            var s = Encoding.UTF8.GetString(Buf.Slice(Pos, len));
            Pos += len;
            return s;
        }
    }

    // ---------- input events (tiny, fixed-size) ----------

    public static Message MouseMove(in MouseMoveEvent e)
    {
        var buf = new byte[4];
        var c = new Cursor(buf); c.U16(e.Nx); c.U16(e.Ny);
        return new Message(MessageType.MouseMove, buf);
    }

    public static MouseMoveEvent ReadMouseMove(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new MouseMoveEvent(r.U16(), r.U16());
    }

    public static Message MouseButtonMsg(in MouseButtonEvent e)
    {
        var buf = new byte[6];
        var c = new Cursor(buf); c.U8((byte)e.Button); c.Bool(e.Down); c.U16(e.Nx); c.U16(e.Ny);
        return new Message(MessageType.MouseButton, buf);
    }

    public static MouseButtonEvent ReadMouseButton(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new MouseButtonEvent((MouseButton)r.U8(), r.Bool(), r.U16(), r.U16());
    }

    public static Message MouseWheelMsg(in MouseWheelEvent e)
    {
        var buf = new byte[8];
        var c = new Cursor(buf); c.I16(e.DeltaX); c.I16(e.DeltaY); c.U16(e.Nx); c.U16(e.Ny);
        return new Message(MessageType.MouseWheel, buf);
    }

    public static MouseWheelEvent ReadMouseWheel(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new MouseWheelEvent(r.I16(), r.I16(), r.U16(), r.U16());
    }

    public static Message KeyMsg(in KeyEventData e)
    {
        var buf = new byte[6];
        var c = new Cursor(buf); c.U16(e.VirtualKey); c.U16(e.ScanCode); c.Bool(e.Down); c.Bool(e.Extended);
        return new Message(MessageType.KeyEvent, buf);
    }

    public static KeyEventData ReadKey(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new KeyEventData(r.U16(), r.U16(), r.Bool(), r.Bool());
    }

    // ---------- settings ----------

    public static Message Settings(SessionSettings s)
    {
        var buf = new byte[32];
        var c = new Cursor(buf);
        c.U8((byte)s.FrameRateMode);
        c.I32(s.TargetFps);
        c.I32(s.MaxFps);
        c.U8((byte)s.ResolutionMode);
        c.I32(s.ScaledWidth);
        c.I32(s.ScaledHeight);
        c.I32(s.DisplayIndex);
        c.U8((byte)s.PreferredCodec);
        c.U8((byte)s.Quality);
        c.Bool(s.ClipboardSync);
        c.Bool(s.BlankHostScreen);
        c.Bool(s.LockHostInput);
        return new Message(MessageType.SettingsUpdate, buf, c.Pos);
    }

    public static SessionSettings ReadSettings(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new SessionSettings
        {
            FrameRateMode = (FrameRateMode)r.U8(),
            TargetFps = r.I32(),
            MaxFps = r.I32(),
            ResolutionMode = (ResolutionMode)r.U8(),
            ScaledWidth = r.I32(),
            ScaledHeight = r.I32(),
            DisplayIndex = r.I32(),
            PreferredCodec = (VideoCodec)r.U8(),
            Quality = r.U8(),
            ClipboardSync = r.Bool(),
            BlankHostScreen = r.Bool(),
            LockHostInput = r.Bool(),
        };
    }

    // ---------- video config ----------

    public static Message VideoConfigMsg(VideoConfig v)
    {
        var buf = new byte[13];
        var c = new Cursor(buf);
        c.I32(v.Width); c.I32(v.Height); c.U8((byte)v.Codec); c.I32(v.TileSize);
        return new Message(MessageType.VideoConfig, buf, c.Pos);
    }

    public static VideoConfig ReadVideoConfig(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        return new VideoConfig(r.I32(), r.I32(), (VideoCodec)r.U8(), r.I32());
    }

    // ---------- display list ----------

    public static Message DisplayList(IReadOnlyList<DisplayInfo> displays)
    {
        // Worst-case sizing: header + per-display fixed fields + name.
        int size = 2;
        foreach (var d in displays) size += 4 + 2 + Encoding.UTF8.GetByteCount(d.DeviceName) + 4 * 5 + 1 + 4;
        var buf = new byte[size];
        var c = new Cursor(buf);
        c.U16((ushort)displays.Count);
        foreach (var d in displays)
        {
            c.I32(d.Index);
            c.Str(d.DeviceName);
            c.I32(d.X); c.I32(d.Y); c.I32(d.Width); c.I32(d.Height);
            c.Bool(d.IsPrimary);
            c.I32(d.RefreshHz);
        }
        return new Message(MessageType.DisplayList, buf, c.Pos);
    }

    public static List<DisplayInfo> ReadDisplayList(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        int n = r.U16();
        var list = new List<DisplayInfo>(n);
        for (int i = 0; i < n; i++)
        {
            int index = r.I32();
            string name = r.Str();
            int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
            bool primary = r.Bool();
            int hz = r.I32();
            list.Add(new DisplayInfo(index, name, x, y, w, h, primary, hz));
        }
        return list;
    }

    // ---------- stats ----------

    public static Message Stat(SessionStat s)
    {
        int size = 4 + 8 + 4 + 4 + 2 + Encoding.UTF8.GetByteCount(s.EncoderName);
        var buf = new byte[size];
        var c = new Cursor(buf);
        c.I32(s.Fps);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(c.Pos), BitConverter.DoubleToInt64Bits(s.MbitsPerSecond));
        c.Pos += 8;
        c.I32(s.RoundTripMs);
        c.I32(s.EncodeMs);
        c.Str(s.EncoderName);
        return new Message(MessageType.Stat, buf, c.Pos);
    }

    public static SessionStat ReadStat(ReadOnlySpan<byte> p)
    {
        var r = new Reader(p);
        int fps = r.I32();
        double mbps = BitConverter.Int64BitsToDouble(r.I64());
        int rtt = r.I32();
        int enc = r.I32();
        string name = r.Str();
        return new SessionStat(fps, mbps, rtt, enc, name);
    }
}
