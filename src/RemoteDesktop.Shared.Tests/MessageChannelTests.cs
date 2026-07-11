using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>Audit M-A0 gelombang G1d: framing/channel — batas ukuran (DoS).</summary>
public class MessageChannelTests
{
    // ---------- AUD-003: Framing.WriteHeader guard ----------

    [Fact]
    public void WriteHeader_RejectsOversizedAndNegative()
    {
        var h = new byte[Framing.HeaderSize];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Framing.WriteHeader(h, MessageType.VideoFrame, Framing.MaxPayloadSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Framing.WriteHeader(h, MessageType.VideoFrame, -1));
    }

    [Fact]
    public void WriteHeader_AcceptsBoundaryValues()
    {
        var h = new byte[Framing.HeaderSize];
        Framing.WriteHeader(h, MessageType.Ping, 0);
        Framing.WriteHeader(h, MessageType.VideoFrame, Framing.MaxPayloadSize);
        var (type, len) = Framing.ReadHeader(h);
        Assert.Equal(MessageType.VideoFrame, type);
        Assert.Equal(Framing.MaxPayloadSize, len);
    }

    // ---------- AUD-003: oversized outbound rejected at the door ----------

    [Fact]
    public async Task TrySend_RejectsOversizedMessage()
    {
        await using var ch = new MessageChannel(new MemoryStream());
        var oversized = new Message(MessageType.ClipboardUpdate, new byte[1], Framing.MaxPayloadSize + 1);
        Assert.False(ch.TrySend(oversized));
    }

    // ---------- AUD-004: settable inbound cap ----------

    [Fact]
    public async Task MaxInboundPayloadSize_ClampsToProtocolRange()
    {
        await using var ch = new MessageChannel(new MemoryStream());
        ch.MaxInboundPayloadSize = 1000;
        Assert.Equal(1000, ch.MaxInboundPayloadSize);
        ch.MaxInboundPayloadSize = -5;
        Assert.Equal(0, ch.MaxInboundPayloadSize);
        ch.MaxInboundPayloadSize = int.MaxValue;
        Assert.Equal(Framing.MaxPayloadSize, ch.MaxInboundPayloadSize);
    }

    [Fact]
    public async Task ReadLoop_RejectsFrameOverCap_WithoutYielding()
    {
        // Hand-craft a header declaring more than the protocol max; the read loop must reject it
        // (and complete Inbound) instead of allocating a giant buffer.
        var header = new byte[Framing.HeaderSize];
        header[0] = (byte)MessageType.VideoFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)(Framing.MaxPayloadSize + 1));

        await using var ch = new MessageChannel(new MemoryStream(header));
        int count = 0;
        await foreach (var _ in ch.Inbound.ReadAllAsync()) count++;
        Assert.Equal(0, count);
    }
}
