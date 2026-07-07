using System.Buffers.Binary;

namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// A single wire message: a <see cref="MessageType"/> and an opaque payload.
/// The payload is a rented/owned <see cref="byte"/> buffer whose *used* length is <see cref="Length"/>.
/// </summary>
public readonly struct Message
{
    public readonly MessageType Type;
    public readonly byte[] Payload;
    public readonly int Length;

    public Message(MessageType type, byte[] payload, int length)
    {
        Type = type;
        Payload = payload;
        Length = length;
    }

    public Message(MessageType type, byte[] payload) : this(type, payload, payload.Length) { }

    public ReadOnlySpan<byte> Span => Payload.AsSpan(0, Length);

    public static Message Empty(MessageType type) => new(type, Array.Empty<byte>(), 0);
}

/// <summary>
/// Frame layout on the wire (after the TLS layer):
/// <code>
/// [1 byte  MessageType]
/// [4 bytes payload length, little-endian, uint]
/// [N bytes payload]
/// </code>
/// Little-endian is chosen deliberately: both peers are x86/ARM Windows, so no byte swap.
/// </summary>
public static class Framing
{
    public const int HeaderSize = 5;

    /// <summary>Absolute ceiling on a single payload to stop a hostile/broken peer exhausting memory.</summary>
    public const int MaxPayloadSize = 64 * 1024 * 1024; // 64 MiB

    public static void WriteHeader(Span<byte> header, MessageType type, int payloadLength)
    {
        header[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(header[1..], (uint)payloadLength);
    }

    public static (MessageType type, int length) ReadHeader(ReadOnlySpan<byte> header)
    {
        var type = (MessageType)header[0];
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[1..]);
        return (type, length);
    }
}
