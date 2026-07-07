using System.Buffers;
using System.IO;
using System.Threading.Channels;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Net;

/// <summary>
/// Full-duplex, framed message transport over any <see cref="Stream"/> (typically an
/// <c>SslStream</c>). Reads and writes are decoupled through bounded channels so a slow
/// consumer never blocks the socket read loop, and vice-versa.
///
/// The write side coalesces onto a single background pump so concurrent producers
/// (video thread, input thread, clipboard) never interleave partial frames.
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<Message> _outbound;
    private readonly Channel<Message> _inbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Task _writeLoop;

    public MessageChannel(Stream stream, int outboundCapacity = 256, int inboundCapacity = 256)
    {
        _stream = stream;

        // Video frames are droppable-latest under pressure; control messages must not be lost.
        // We keep the queue bounded and let the host's encoder throttle instead of unbounded growth.
        _outbound = Channel.CreateBounded<Message>(new BoundedChannelOptions(outboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _inbound = Channel.CreateBounded<Message>(new BoundedChannelOptions(inboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });

        _readLoop = Task.Run(ReadLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Stream of decoded inbound messages. Payload buffers are rented — copy what you keep.</summary>
    public ChannelReader<Message> Inbound => _inbound.Reader;

    /// <summary>Enqueue a message for sending. Awaits if the outbound queue is momentarily full.</summary>
    public ValueTask SendAsync(Message message, CancellationToken ct = default)
        => _outbound.Writer.WriteAsync(message, ct);

    /// <summary>Best-effort send that drops the message if the queue is full (used for video frames).</summary>
    public bool TrySend(Message message) => _outbound.Writer.TryWrite(message);

    private async Task WriteLoopAsync()
    {
        var header = new byte[Framing.HeaderSize];
        try
        {
            await foreach (var msg in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                Framing.WriteHeader(header, msg.Type, msg.Length);
                await _stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                if (msg.Length > 0)
                    await _stream.WriteAsync(msg.Payload.AsMemory(0, msg.Length), _cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                // Return pooled payloads once they are safely on the wire.
                if (msg.Payload.Length > 0 && msg.Type is MessageType.VideoFrame)
                    ArrayPool<byte>.Shared.Return(msg.Payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Fault(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[Framing.HeaderSize];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await ReadExactAsync(header, _cts.Token).ConfigureAwait(false);
                var (type, length) = Framing.ReadHeader(header);
                if (length < 0 || length > Framing.MaxPayloadSize)
                    throw new InvalidDataException($"Frame length {length} out of bounds for {type}.");

                // Inbound buffers are handed to consumers and not pooled — the exact-size array is
                // owned by the consumer and collected normally. (Outbound video frames use the pool.)
                byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                if (length > 0)
                    await ReadExactAsync(payload.AsMemory(0, length), _cts.Token).ConfigureAwait(false);

                await _inbound.Writer.WriteAsync(new Message(type, payload, length), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* EOF, TLS error, malformed frame */ }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Peer closed the connection.");
            read += n;
        }
    }

    private void Fault() => _outbound.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outbound.Writer.TryComplete();
        try { await Task.WhenAll(_readLoop, _writeLoop).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
