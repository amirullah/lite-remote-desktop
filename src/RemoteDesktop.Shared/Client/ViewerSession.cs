using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Client;

/// <summary>
/// Portable viewer-side session driver. Over an already-authenticated <see cref="MessageChannel"/> it
/// sends the initial <see cref="SessionSettings"/> (which starts the host capture), then pumps the
/// inbound stream — forwarding <see cref="MessageType.VideoConfig"/>/<see cref="MessageType.VideoFrame"/>
/// to an <see cref="IVideoDecoder"/> and surfacing telemetry via events. Input helpers live here too so
/// every client (Windows/Android/Mac) shares one session loop. Fully portable (net8.0) and covered by
/// the loopback VideoStreamIntegrationTests.
/// </summary>
public sealed class ViewerSession
{
    private readonly MessageChannel _channel;
    private readonly IVideoDecoder _decoder;

    /// <summary>Raised when the host announces the stream geometry (width, height).</summary>
    public event Action<int, int>? VideoConfigured;

    /// <summary>Raised for each telemetry update (fps / bandwidth / latency).</summary>
    public event Action<SessionStat>? StatReceived;

    public ViewerSession(MessageChannel channel, IVideoDecoder decoder)
    {
        _channel = channel;
        _decoder = decoder;
    }

    /// <summary>
    /// Send the initial settings, then process inbound messages until the connection closes or
    /// <paramref name="ct"/> fires.
    /// </summary>
    public async Task RunAsync(SessionSettings settings, CancellationToken ct = default)
    {
        await _channel.SendAsync(PayloadCodec.Settings(settings), ct).ConfigureAwait(false);

        await foreach (var msg in _channel.Inbound.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (msg.Type)
            {
                case MessageType.VideoConfig:
                    var cfg = PayloadCodec.ReadVideoConfig(msg.Span);
                    _decoder.Configure(cfg.Width, cfg.Height, cfg.Codec);
                    VideoConfigured?.Invoke(cfg.Width, cfg.Height);
                    break;

                case MessageType.VideoFrame:
                    var (id, flags, tiles) = VideoFrameCodec.Decode(
                        new ReadOnlyMemory<byte>(msg.Payload, 0, msg.Length));
                    _decoder.SubmitFrame(id, flags, tiles);
                    break;

                case MessageType.Stat:
                    StatReceived?.Invoke(PayloadCodec.ReadStat(msg.Span));
                    break;

                // DisplayList / ClipboardUpdate / Pong are not needed for M-A2 video bring-up.
            }
        }
    }

    /// <summary>Ask the host to resend a full keyframe (e.g. after a decoder reset or packet loss).</summary>
    public ValueTask RequestKeyFrameAsync(CancellationToken ct = default)
        => _channel.SendAsync(Message.Empty(MessageType.KeyFrameRequest), ct);

    // --- input (touch mapped to mouse); coordinates are normalized 0..65535 so DPI/resolution never desync ---

    public ValueTask SendPointerMoveAsync(ushort nx, ushort ny, CancellationToken ct = default)
        => _channel.SendAsync(PayloadCodec.MouseMove(new MouseMoveEvent(nx, ny)), ct);

    public ValueTask SendPointerButtonAsync(MouseButton button, bool down, ushort nx, ushort ny, CancellationToken ct = default)
        => _channel.SendAsync(PayloadCodec.MouseButtonMsg(new MouseButtonEvent(button, down, nx, ny)), ct);

    public ValueTask SendWheelAsync(short deltaX, short deltaY, ushort nx, ushort ny, CancellationToken ct = default)
        => _channel.SendAsync(PayloadCodec.MouseWheelMsg(new MouseWheelEvent(deltaX, deltaY, nx, ny)), ct);
}
