using System.Buffers.Binary;
using System.Text;

namespace RemoteDesktop.Shared.Protocol;

public enum ClipboardFormat : byte
{
    Empty = 0,
    Text = 1,       // UTF-8
    Png = 2,        // image bytes, PNG-encoded
    FileList = 3,   // newline-separated file paths (copied file names, not contents)
}

/// <summary>An item currently on a clipboard, in a transport-neutral form.</summary>
public sealed record ClipboardData(ClipboardFormat Format, byte[] Bytes)
{
    public static readonly ClipboardData Empty = new(ClipboardFormat.Empty, Array.Empty<byte>());

    public string AsText() => Encoding.UTF8.GetString(Bytes);
    public static ClipboardData FromText(string text) => new(ClipboardFormat.Text, Encoding.UTF8.GetBytes(text));
    public static ClipboardData FromFileList(IEnumerable<string> paths) =>
        new(ClipboardFormat.FileList, Encoding.UTF8.GetBytes(string.Join('\n', paths)));

    public string[] AsFileList() => AsText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Content hashing lets both ends short-circuit clipboard echo loops: when we push a value we
/// remember its hash, and ignore the very next update that carries the same hash.
/// </summary>
public static class ClipboardCodec
{
    public static Message Encode(ClipboardData data)
    {
        var buf = new byte[1 + 4 + data.Bytes.Length];
        buf[0] = (byte)data.Format;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), data.Bytes.Length);
        data.Bytes.CopyTo(buf.AsSpan(5));
        return new Message(MessageType.ClipboardUpdate, buf);
    }

    public static ClipboardData Decode(ReadOnlySpan<byte> p)
    {
        var format = (ClipboardFormat)p[0];
        int len = BinaryPrimitives.ReadInt32LittleEndian(p[1..]);
        return new ClipboardData(format, p.Slice(5, len).ToArray());
    }

    public static ulong Fingerprint(ClipboardData data)
    {
        // FNV-1a over format + bytes; good enough to dedupe our own echoes.
        ulong hash = 14695981039346656037UL;
        hash = (hash ^ (byte)data.Format) * 1099511628211UL;
        foreach (var b in data.Bytes) hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }
}
