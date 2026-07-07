using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Services;

public enum ConnectionState { Idle, Connecting, Authenticating, Connected, Failed, Disconnected }

/// <summary>How the user chose to prove who they are for this connection.</summary>
public abstract record Credential;
public sealed record PasswordCredential(string Password) : Credential;
public sealed record GoogleCredential(string IdToken) : Credential;

/// <summary>
/// The client end of the protocol: establishes a pinned TLS connection, authenticates, then
/// exposes a stream of high-level events (video config, frames, stats, clipboard, displays) and
/// methods to send input/settings back to the host. All heavy work (decode, render) happens in the
/// consumer via the raised events; this class is just plumbing.
/// </summary>
public sealed class RemoteConnection : IAsyncDisposable
{
    private readonly PinStore _pins;
    private MessageChannel? _channel;
    private CancellationTokenSource? _cts;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    // --- events (raised on the message-loop task; marshal to UI as needed) ---
    public event Action<ConnectionState, string>? StateChanged;
    public event Action<VideoConfig>? VideoConfigured;
    public event Action<uint, FrameFlags, IReadOnlyList<Tile>, ReadOnlyMemory<byte>>? FrameReceived;
    public event Action<SessionStat>? StatReceived;
    public event Action<IReadOnlyList<DisplayInfo>>? DisplaysReceived;
    public event Action<ClipboardData>? ClipboardReceived;

    /// <summary>
    /// Called on first-ever connection to an endpoint. Return true to trust and pin the fingerprint.
    /// This is the user's out-of-band verification step (they compare it to the host's tray dialog).
    /// </summary>
    public Func<string, string, bool>? ConfirmFingerprint;

    public RemoteConnection(PinStore pins) => _pins = pins;

    /// <param name="bindAddress">
    /// When set, the socket is bound to this local address before connecting. Passing the VPN
    /// tunnel's address here is what makes the connection travel through the VPN while the rest of
    /// the machine keeps using the direct internet — per-app VPN without a kernel driver.
    /// </param>
    public async Task<bool> ConnectAsync(string host, int port, Credential credential, SessionSettings initial,
        System.Net.IPAddress? bindAddress = null, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var endpoint = $"{host}:{port}";
        SetState(ConnectionState.Connecting, $"Connecting to {endpoint}…");

        try
        {
            var tcp = bindAddress is null
                ? new TcpClient { NoDelay = true }
                : new TcpClient(new System.Net.IPEndPoint(bindAddress, 0)) { NoDelay = true };
            await tcp.ConnectAsync(host, port, _cts.Token).ConfigureAwait(false);
            return await RunSessionAsync(tcp.GetStream(), endpoint, host, credential, initial, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            SetState(ConnectionState.Failed, $"TLS/certificate error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Failed, $"Connection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Connect by 9-digit ID through the relay (TeamViewer-style). We open a "connect" request to
    /// the relay; once it splices us to the host, the very same socket carries the end-to-end TLS
    /// session — so pinning and auth are identical to a direct connection, keyed by the host ID.
    /// </summary>
    public async Task<bool> ConnectViaRelayAsync(string relayAddress, string hostId, Credential credential,
        SessionSettings initial, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var id = Shared.Relay.RelayProtocol.NormalizeId(hostId);
        SetState(ConnectionState.Connecting, $"Reaching ID {Shared.Relay.RelayProtocol.FormatId(id)}…");

        try
        {
            var parts = relayAddress.Split(':', 2);
            string rhost = parts[0].Trim();
            int rport = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : Shared.Relay.RelayProtocol.DefaultPort;

            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(rhost, rport, _cts.Token).ConfigureAwait(false);
            var raw = tcp.GetStream();

            await Shared.Relay.RelayProtocol.SendAsync(raw, new Shared.Relay.RelayMsg { Op = "connect", Id = id }, _cts.Token)
                .ConfigureAwait(false);
            var reply = await Shared.Relay.RelayProtocol.ReadAsync(raw, _cts.Token).ConfigureAwait(false);
            if (reply is not { Ok: true })
            {
                SetState(ConnectionState.Failed, reply?.Error ?? "Relay could not reach that ID.");
                return false;
            }

            // Pin/auth are keyed by the stable ID, not a transient relay address.
            return await RunSessionAsync(raw, $"id:{id}", id, credential, initial, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Failed, $"Relay connection failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RunSessionAsync(Stream transport, string pinKey, string targetHost,
        Credential credential, SessionSettings initial, CancellationToken ct)
    {
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false,
            (_, cert, _, _) => ValidateCertificate(pinKey, cert));

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct).ConfigureAwait(false);

        _channel = new MessageChannel(ssl);

        SetState(ConnectionState.Authenticating, "Authenticating…");
        if (!await AuthenticateAsync(credential, ct).ConfigureAwait(false))
        {
            SetState(ConnectionState.Failed, "Authentication rejected by host.");
            return false;
        }

        await _channel.SendAsync(PayloadCodec.Settings(initial), ct).ConfigureAwait(false);
        SetState(ConnectionState.Connected, "Connected.");
        _ = Task.Run(() => MessageLoopAsync(_cts!.Token));
        return true;
    }

    private bool ValidateCertificate(string endpoint, X509Certificate? cert)
    {
        if (cert is null) return false;
        using var cert2 = new X509Certificate2(cert);
        var fingerprint = CertificateManager.PublicKeyFingerprint(cert2);

        return _pins.Check(endpoint, fingerprint) switch
        {
            PinCheck.Match => true,
            PinCheck.Mismatch => false, // possible MITM — refuse
            PinCheck.FirstUse => ConfirmAndPin(endpoint, fingerprint),
            _ => false,
        };
    }

    private bool ConfirmAndPin(string endpoint, string fingerprint)
    {
        var pretty = CertificateManager.FormatFingerprint(fingerprint);
        bool trust = ConfirmFingerprint?.Invoke(endpoint, pretty) ?? false;
        if (trust) _pins.Pin(endpoint, fingerprint);
        return trust;
    }

    private async Task<bool> AuthenticateAsync(Credential credential, CancellationToken ct)
    {
        await foreach (var msg in _channel!.Inbound.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (msg.Type == MessageType.AuthRequest)
            {
                var req = AuthProtocol.ReadRequest(msg.Span);
                var response = credential switch
                {
                    PasswordCredential p when req.Methods.HasFlag(AuthMethod.Password)
                        => new AuthResponseData(AuthMethod.Password, p.Password),
                    GoogleCredential g when req.Methods.HasFlag(AuthMethod.Google)
                        => new AuthResponseData(AuthMethod.Google, g.IdToken),
                    _ => null,
                };
                if (response is null)
                {
                    SetState(ConnectionState.Failed, "Host does not accept the chosen login method.");
                    return false;
                }
                await _channel.SendAsync(AuthProtocol.Response(response), ct).ConfigureAwait(false);
            }
            else if (msg.Type == MessageType.AuthResult)
            {
                var result = AuthProtocol.ReadResult(msg.Span);
                if (!result.Ok) SetState(ConnectionState.Failed, result.Reason);
                return result.Ok;
            }
        }
        return false;
    }

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _channel!.Inbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (msg.Type)
                {
                    case MessageType.VideoConfig:
                        VideoConfigured?.Invoke(PayloadCodec.ReadVideoConfig(msg.Span));
                        break;
                    case MessageType.VideoFrame:
                        var mem = new ReadOnlyMemory<byte>(msg.Payload, 0, msg.Length);
                        var (id, flags, tiles) = VideoFrameCodec.Decode(mem);
                        FrameReceived?.Invoke(id, flags, tiles, mem);
                        break;
                    case MessageType.DisplayList:
                        DisplaysReceived?.Invoke(PayloadCodec.ReadDisplayList(msg.Span));
                        break;
                    case MessageType.Stat:
                        StatReceived?.Invoke(PayloadCodec.ReadStat(msg.Span));
                        break;
                    case MessageType.ClipboardUpdate:
                        ClipboardReceived?.Invoke(ClipboardCodec.Decode(msg.Span));
                        break;
                    case MessageType.Pong:
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetState(ConnectionState.Disconnected, ex.Message); }
        finally
        {
            if (State == ConnectionState.Connected)
                SetState(ConnectionState.Disconnected, "Connection closed.");
        }
    }

    // --- outbound helpers ---

    public void SendMouseMove(in MouseMoveEvent e) => _channel?.TrySend(PayloadCodec.MouseMove(e));
    public void SendMouseButton(in MouseButtonEvent e) => _channel?.TrySend(PayloadCodec.MouseButtonMsg(e));
    public void SendMouseWheel(in MouseWheelEvent e) => _channel?.TrySend(PayloadCodec.MouseWheelMsg(e));
    public void SendKey(in KeyEventData e) => _channel?.TrySend(PayloadCodec.KeyMsg(e));
    public void SendClipboard(ClipboardData data) => _channel?.TrySend(ClipboardCodec.Encode(data));
    public void RequestKeyFrame() => _channel?.TrySend(Message.Empty(MessageType.KeyFrameRequest));

    public async Task SendSettingsAsync(SessionSettings settings)
    {
        if (_channel != null) await _channel.SendAsync(PayloadCodec.Settings(settings)).ConfigureAwait(false);
    }

    private void SetState(ConnectionState state, string message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel?.TrySend(Message.Empty(MessageType.Bye)); } catch { }
        _cts?.Cancel();
        if (_channel != null) await _channel.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
