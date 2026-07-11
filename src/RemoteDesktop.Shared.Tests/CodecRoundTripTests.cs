using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// Golden baseline (audit M-A0, gelombang G0): karakterisasi perilaku BENAR yang ada sekarang.
/// Semua HIJAU pada kode saat ini; perbaikan G1 tidak boleh membuat satu pun jadi merah.
/// Uji penolakan input cacat ada di MalformedInputTests.cs dan menyertai fix masing-masing.
/// </summary>
public class CodecRoundTripTests
{
    [Fact]
    public void Framing_Header_RoundTrips()
    {
        Span<byte> h = stackalloc byte[Framing.HeaderSize];
        Framing.WriteHeader(h, MessageType.VideoFrame, 123456);
        var (type, len) = Framing.ReadHeader(h);
        Assert.Equal(MessageType.VideoFrame, type);
        Assert.Equal(123456, len);
    }

    [Fact]
    public void MouseMove_RoundTrips()
    {
        var e = new MouseMoveEvent(40000, 12345);
        var back = PayloadCodec.ReadMouseMove(PayloadCodec.MouseMove(e).Span);
        Assert.Equal(e, back);
    }

    [Fact]
    public void MouseButton_RoundTrips()
    {
        var e = new MouseButtonEvent(MouseButton.Right, true, 100, 200);
        var back = PayloadCodec.ReadMouseButton(PayloadCodec.MouseButtonMsg(e).Span);
        Assert.Equal(e, back);
    }

    [Fact]
    public void MouseWheel_RoundTrips()
    {
        var e = new MouseWheelEvent(-120, 240, 10, 20);
        var back = PayloadCodec.ReadMouseWheel(PayloadCodec.MouseWheelMsg(e).Span);
        Assert.Equal(e, back);
    }

    [Fact]
    public void KeyEvent_RoundTrips()
    {
        var e = new KeyEventData(0x41, 0x1E, true, false);
        var back = PayloadCodec.ReadKey(PayloadCodec.KeyMsg(e).Span);
        Assert.Equal(e, back);
    }

    [Fact]
    public void Settings_RoundTrips()
    {
        var s = new SessionSettings
        {
            FrameRateMode = FrameRateMode.Fixed,
            TargetFps = 30,
            MaxFps = 90,
            ResolutionMode = ResolutionMode.Scaled,
            ScaledWidth = 1280,
            ScaledHeight = 720,
            DisplayIndex = 1,
            PreferredCodec = VideoCodec.H265,
            Quality = 80,
            ClipboardSync = false,
            BlankHostScreen = true,
            LockHostInput = true,
        };
        var back = PayloadCodec.ReadSettings(PayloadCodec.Settings(s).Span);
        Assert.Equal(s, back);
    }

    [Fact]
    public void VideoConfig_RoundTrips()
    {
        var v = new VideoConfig(1920, 1080, VideoCodec.H264, 64);
        var back = PayloadCodec.ReadVideoConfig(PayloadCodec.VideoConfigMsg(v).Span);
        Assert.Equal(v, back);
    }

    [Fact]
    public void DisplayList_RoundTrips_WithUnicodeName()
    {
        var displays = new List<DisplayInfo>
        {
            new(0, "Monitor-Utama-™", 0, 0, 1920, 1080, true, 60),
            new(1, "Second", 1920, 0, 2560, 1440, false, 144),
        };
        var back = PayloadCodec.ReadDisplayList(PayloadCodec.DisplayList(displays).Span);
        Assert.Equal(displays, back);
    }

    [Fact]
    public void Stat_RoundTrips_IncludingDouble()
    {
        var s = new SessionStat(60, 12.5, 8, 3, "H264 (HW)");
        var back = PayloadCodec.ReadStat(PayloadCodec.Stat(s).Span);
        Assert.Equal(s, back);
    }

    [Fact]
    public void VideoFrame_RoundTrips_MultipleTiles()
    {
        var tiles = new List<Tile>
        {
            new(1, 2, 3, 4, new byte[] { 10, 20, 30 }),
            new(5, 6, 7, 8, new byte[] { 40 }),
            new(9, 10, 11, 12, Array.Empty<byte>()),
        };
        var msg = VideoFrameCodec.Encode(42, FrameFlags.KeyFrame, tiles);
        var (id, flags, decoded) = VideoFrameCodec.Decode(msg.Payload.AsMemory(0, msg.Length));

        Assert.Equal(42u, id);
        Assert.Equal(FrameFlags.KeyFrame, flags);
        Assert.Equal(3, decoded.Count);
        for (int i = 0; i < tiles.Count; i++)
        {
            Assert.Equal(tiles[i].X, decoded[i].X);
            Assert.Equal(tiles[i].Y, decoded[i].Y);
            Assert.Equal(tiles[i].Width, decoded[i].Width);
            Assert.Equal(tiles[i].Height, decoded[i].Height);
            Assert.True(tiles[i].Data.Span.SequenceEqual(decoded[i].Data.Span));
        }
    }

    [Fact]
    public void Clipboard_Text_RoundTrips()
    {
        var data = ClipboardData.FromText("hello ünïcode ✓");
        var back = ClipboardCodec.Decode(ClipboardCodec.Encode(data).Span);
        Assert.Equal(ClipboardFormat.Text, back.Format);
        Assert.Equal("hello ünïcode ✓", back.AsText());
    }

    [Fact]
    public void Clipboard_FileList_RoundTrips()
    {
        var data = ClipboardData.FromFileList(new[] { @"C:\a.txt", @"D:\b\c.png" });
        var back = ClipboardCodec.Decode(ClipboardCodec.Encode(data).Span);
        Assert.Equal(ClipboardFormat.FileList, back.Format);
        Assert.Equal(new[] { @"C:\a.txt", @"D:\b\c.png" }, back.AsFileList());
    }

    [Fact]
    public void Clipboard_Fingerprint_IsStableAndDistinct()
    {
        var a = ClipboardData.FromText("same");
        var b = ClipboardData.FromText("same");
        var c = ClipboardData.FromText("different");
        Assert.Equal(ClipboardCodec.Fingerprint(a), ClipboardCodec.Fingerprint(b));
        Assert.NotEqual(ClipboardCodec.Fingerprint(a), ClipboardCodec.Fingerprint(c));
    }
}
