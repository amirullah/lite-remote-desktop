using System.Buffers;
using System.IO;
using System.Threading.Channels;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Net;

/// <summary>
/// Full-duplex, framed message transport over any <see cref="Stream"/> (typically an
/// <c>SslStream</c>). Reads and writes are decoupled so a slow consumer never blocks the socket
/// read loop.
///
/// The write side splits traffic into two lanes, and this is the single most important thing for
/// perceived latency:
///
///   • <b>Control lane</b> (auth, settings, input, clipboard, keyframe requests, pong) — unbounded,
///     lossless, and always drained first so a keystroke or settings change is never stuck behind
///     video bytes.
///   • <b>Video lane</b> — a <i>shallow</i> bounded queue (2 frames). Because the codec is a dirty-
///     tile delta stream, frames must not be dropped (a lost delta leaves stale pixels), so instead
///     the queue applies backpressure: when the link can't keep up, the encoder blocks on send and
///     naturally slows to the link's rate. That caps end-to-end latency at ~2 frames instead of
///     letting hundreds pile into a queue the viewer then trails seconds behind.
///
/// The old design used a single 256-deep queue for everything; on any link slower than the encoder
/// that buffered multiple seconds of video — the host drew instantly but the viewer lagged far
/// behind. A depth of 2 keeps just enough slack to overlap "encode N+1" with "send N".
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
    private const int VideoQueueDepth = 2;

    private readonly Stream _stream;
    private readonly Channel<Message> _control;
    private readonly Channel<Message> _video;
    private readonly Channel<Message> _inbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Task _writeLoop;
    private volatile int _maxInboundPayload = Framing.MaxPayloadSize;

    /// <summary>
    /// Upper bound (bytes) on a single inbound frame's declared length. Defaults to the protocol max;
    /// a host can lower it during the pre-auth handshake so an unauthenticated peer cannot force large
    /// per-frame allocations, then raise it back once authenticated. (audit M-A0: AUD-004)
    /// </summary>
    public int MaxInboundPayloadSize
    {
        get => _maxInboundPayload;
        set => _maxInboundPayload = value < 0 ? 0 : Math.Min(value, Framing.MaxPayloadSize);
    }

    public MessageChannel(Stream stream, int inboundCapacity = 256)
    {
        _stream = stream;

        _control = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        _video = Channel.CreateBounded<Message>(new BoundedChannelOptions(VideoQueueDepth)
        {
            FullMode = BoundedChannelFullMode.Wait, // backpressure the encoder, never drop a delta
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

    /// <summary>Enqueue a control message (lossless, ordered, high priority).</summary>
    public ValueTask SendAsync(Message message, CancellationToken ct = default)
    {
        if (IsUnsendable(message)) return ValueTask.CompletedTask; // drop rather than desync the peer
        if (message.Type == MessageType.VideoFrame) return SendVideoAsync(message, ct);
        _control.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A message whose length can't be framed (the wire length field is bounded by
    /// <see cref="Framing.MaxPayloadSize"/>). Reject at the door instead of writing an oversized frame
    /// the peer silently rejects. Recycles a pooled video buffer so it isn't leaked. (audit M-A0: AUD-003)
    /// </summary>
    private static bool IsUnsendable(Message message)
    {
        if (message.Length >= 0 && message.Length <= Framing.MaxPayloadSize) return false;
        if (message.Type == MessageType.VideoFrame && message.Payload.Length > 0)
            ArrayPool<byte>.Shared.Return(message.Payload);
        return true;
    }

    /// <summary>
    /// Send a message. Control messages are queued losslessly and return immediately. Video frames
    /// go through the shallow video lane and <b>block</b> the caller when it is full — this is the
    /// backpressure that keeps the encoder from running ahead of the link. Returns false only if the
    /// channel is shutting down.
    /// </summary>
    public bool TrySend(Message message)
    {
        if (IsUnsendable(message)) return false; // oversized -> caller learns the send didn't happen
        if (message.Type != MessageType.VideoFrame)
        {
            return _control.Writer.TryWrite(message);
        }
        try
        {
            // Synchronous blocking wait: the caller is the dedicated capture/encode thread, and
            // blocking it here is exactly the throttle we want.
            SendVideoAsync(message, _cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false; // cancelled / faulted — let the caller recycle its buffer
        }
    }

    private ValueTask SendVideoAsync(Message message, CancellationToken ct)
        => _video.Writer.WriteAsync(message, ct);

    private async Task WriteLoopAsync()
    {
        var header = new byte[Framing.HeaderSize];
        Task<bool>? ctrlWait = null, videoWait = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Recreate only the lane wait that has been consumed, so we don't pile up abandoned
                // WaitToReadAsync registrations on the channels.
                ctrlWait ??= _control.Reader.WaitToReadAsync(_cts.Token).AsTask();
                videoWait ??= _video.Reader.WaitToReadAsync(_cts.Token).AsTask();
                await Task.WhenAny(ctrlWait, videoWait).ConfigureAwait(false);

                // Always flush all pending control first (never starve input/keyframe behind video).
                if (ctrlWait.IsCompleted)
                {
                    ctrlWait = null;
                    while (_control.Reader.TryRead(out var ctrl))
                        await WriteOneAsync(header, ctrl, recycle: false).ConfigureAwait(false);
                }

                // Then send at most one video frame per pass.
                if (videoWait.IsCompleted)
                {
                    videoWait = null;
                    if (_video.Reader.TryRead(out var video))
                        await WriteOneAsync(header, video, recycle: true).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Fault(); }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteOneAsync(byte[] header, Message msg, bool recycle)
    {
        Framing.WriteHeader(header, msg.Type, msg.Length);
        await _stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
        if (msg.Length > 0)
            await _stream.WriteAsync(msg.Payload.AsMemory(0, msg.Length), _cts.Token).ConfigureAwait(false);
        // Flush every message so it hits the wire immediately — latency over throughput.
        await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);

        // Video payloads are pooled; return them once safely on the wire.
        if (recycle && msg.Payload.Length > 0)
            ArrayPool<byte>.Shared.Return(msg.Payload);
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
                if (length < 0 || length > _maxInboundPayload)
                    throw new InvalidDataException($"Frame length {length} exceeds inbound cap {_maxInboundPayload} for {type}.");

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

    private void Fault()
    {
        _control.Writer.TryComplete();
        _video.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _control.Writer.TryComplete();
        _video.Writer.TryComplete();
        try { await Task.WhenAll(_readLoop, _writeLoop).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
