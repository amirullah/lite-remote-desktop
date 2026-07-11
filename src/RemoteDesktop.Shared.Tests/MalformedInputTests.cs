using System.IO;
using System.Text;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// Audit M-A0 gelombang G1: deserializer harus MENOLAK input tak-tepercaya yang cacat dengan
/// InvalidDataException yang bersih — bukan ArgumentOutOfRange/IndexOutOfRange/NullReference liar.
/// Setiap test di sini merah pada kode lama dan hijau setelah fix AUD-nya.
/// </summary>
public class MalformedInputTests
{
    // ---------- AUD-001: VideoFrameCodec.Decode ----------

    [Fact]
    public void VideoFrame_TooShort_ThrowsInvalidData()
    {
        var bad = new byte[] { 1, 2, 3 }; // < 7-byte frame header
        Assert.Throws<InvalidDataException>(() => VideoFrameCodec.Decode(bad));
    }

    [Fact]
    public void VideoFrame_TileHeaderPastPayload_ThrowsInvalidData()
    {
        // frameId=1, flags=0, tileCount=1, then payload ends (no tile bytes).
        var bad = new byte[] { 1, 0, 0, 0, 0, 1, 0 };
        Assert.Throws<InvalidDataException>(() => VideoFrameCodec.Decode(bad));
    }

    [Fact]
    public void VideoFrame_TileDataLengthOverflow_ThrowsInvalidData()
    {
        // one tile with dataLen = 0xFFFFFFFF but no data bytes present.
        var bad = new byte[]
        {
            1, 0, 0, 0,          // frameId
            0,                   // flags
            1, 0,                // tileCount = 1
            0, 0, 0, 0, 0, 0, 0, 0, // x,y,w,h
            0xFF, 0xFF, 0xFF, 0xFF, // dataLen = 4294967295
        };
        Assert.Throws<InvalidDataException>(() => VideoFrameCodec.Decode(bad));
    }

    // ---------- AUD-007: ClipboardCodec.Decode ----------

    [Fact]
    public void Clipboard_TooShort_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardCodec.Decode(new byte[] { 1, 2 }));
    }

    [Fact]
    public void Clipboard_LengthExceedsPayload_ThrowsInvalidData()
    {
        // format=Text, len=0x7FFFFFFF, but no data.
        var bad = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0x7F };
        Assert.Throws<InvalidDataException>(() => ClipboardCodec.Decode(bad));
    }

    [Fact]
    public void Clipboard_NegativeLength_ThrowsInvalidData()
    {
        var bad = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF }; // len = -1
        Assert.Throws<InvalidDataException>(() => ClipboardCodec.Decode(bad));
    }

    // ---------- AUD-012: PayloadCodec.Reader bounds ----------

    [Fact]
    public void Settings_Truncated_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => PayloadCodec.ReadSettings(Array.Empty<byte>()));
    }

    [Fact]
    public void DisplayList_CountWithoutData_ThrowsInvalidData()
    {
        var bad = new byte[] { 5, 0 }; // count=5, no display records follow
        Assert.Throws<InvalidDataException>(() => PayloadCodec.ReadDisplayList(bad));
    }

    [Fact]
    public void Stat_Truncated_ThrowsInvalidData()
    {
        var bad = new byte[] { 1, 2, 3 }; // not even the leading Fps I32
        Assert.Throws<InvalidDataException>(() => PayloadCodec.ReadStat(bad));
    }

    // ---------- AUD-013: AuthProtocol.Unwrap ----------

    [Fact]
    public void Auth_NullJson_ThrowsInvalidData()
    {
        var nul = Encoding.UTF8.GetBytes("null");
        Assert.Throws<InvalidDataException>(() => AuthProtocol.ReadResponse(nul));
    }

    [Fact]
    public void Auth_GarbageJson_ThrowsInvalidData()
    {
        var garbage = new byte[] { 0xFF, 0x00, 0x7B, 0x12 };
        Assert.Throws<InvalidDataException>(() => AuthProtocol.ReadResponse(garbage));
    }
}
