using System.Buffers;
using System.Buffers.Binary;

namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// One changed rectangle inside a frame. <see cref="Data"/> is codec-specific:
/// JPEG bytes for <c>JpegTiles</c>, or a NAL slice for H.264/H.265.
/// </summary>
public readonly struct Tile
{
    public readonly ushort X, Y, Width, Height;
    public readonly ReadOnlyMemory<byte> Data;

    public Tile(ushort x, ushort y, ushort width, ushort height, ReadOnlyMemory<byte> data)
    {
        X = x; Y = y; Width = width; Height = height; Data = data;
    }
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    KeyFrame = 1,   // a full frame — safe to start decoding here
    Continued = 2,  // reserved: this frame is split across multiple messages
}

/// <summary>
/// Serializes a set of dirty tiles into a single <see cref="MessageType.VideoFrame"/> payload.
///
/// Layout:
/// <code>
/// [u32 frameId][u8 flags][u16 tileCount]
/// repeated tileCount times:
///   [u16 x][u16 y][u16 w][u16 h][u32 dataLen][dataLen bytes]
/// </code>
/// The whole payload is rented from the shared pool and returned by the write pump after send.
/// </summary>
public static class VideoFrameCodec
{
    private const int FrameHeader = 4 + 1 + 2;
    private const int TileHeader = 2 + 2 + 2 + 2 + 4;

    public static Message Encode(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles)
    {
        int size = FrameHeader;
        for (int i = 0; i < tiles.Count; i++)
            size += TileHeader + tiles[i].Data.Length;

        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        int pos = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), frameId); pos += 4;
        buf[pos++] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), (ushort)tiles.Count); pos += 2;

        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), t.X); pos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), t.Y); pos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), t.Width); pos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), t.Height); pos += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), (uint)t.Data.Length); pos += 4;
            t.Data.Span.CopyTo(buf.AsSpan(pos)); pos += t.Data.Length;
        }

        return new Message(MessageType.VideoFrame, buf, pos);
    }

    /// <summary>
    /// Enumerates tiles out of a received payload without copying tile data — the returned
    /// <see cref="Tile"/> memory points into <paramref name="payload"/>, valid until it is returned to the pool.
    /// </summary>
    public static (uint frameId, FrameFlags flags, List<Tile> tiles) Decode(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        int pos = 0;

        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]); pos += 4;
        var flags = (FrameFlags)span[pos++];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]); pos += 2;

        var tiles = new List<Tile>(count);
        for (int i = 0; i < count; i++)
        {
            ushort x = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]); pos += 2;
            ushort y = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]); pos += 2;
            ushort w = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]); pos += 2;
            ushort h = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]); pos += 2;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]); pos += 4;
            tiles.Add(new Tile(x, y, w, h, payload.Slice(pos, len)));
            pos += len;
        }
        return (frameId, flags, tiles);
    }
}
