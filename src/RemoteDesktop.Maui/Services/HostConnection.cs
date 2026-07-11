using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Maui.Services;

/// <summary>
/// Result of a connect attempt. On success <see cref="Channel"/> is an open, authenticated
/// <see cref="MessageChannel"/> the caller drives with a <c>ViewerSession</c> (and must dispose).
/// </summary>
public sealed record ConnectOutcome(MessageChannel? Channel, bool Ok, string Message, string? Fingerprint);

/// <summary>
/// Viewer-side connect for the mobile app: open TLS to a LiteRemote host (surfacing the host's
/// public-key fingerprint for trust-on-first-use), run the password auth handshake on the shared
/// <c>RemoteDesktop.Shared</c> contract, and return the live channel. Persistent pinning (mismatch ->
/// abort) and Google login land in a later milestone; today TOFU accepts and reports the fingerprint.
/// </summary>
public static class ViewerConnection
{
    public static async Task<ConnectOutcome> ConnectAsync(
        string host, int port, string password, CancellationToken ct = default)
    {
        MessageChannel? channel = null;
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

            string? fingerprint = null;
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) =>
                {
                    if (cert is not null)
                    {
                        using var c2 = new X509Certificate2(cert);
                        fingerprint = CertificateManager.FormatFingerprint(
                            CertificateManager.PublicKeyFingerprint(c2));
                    }
                    return true; // TOFU (persistent pin store: later milestone)
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }).ConfigureAwait(false);

            channel = new MessageChannel(ssl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            await foreach (var msg in channel.Inbound.ReadAllAsync(timeout.Token).ConfigureAwait(false))
            {
                if (msg.Type == MessageType.AuthRequest)
                {
                    await channel.SendAsync(
                        AuthProtocol.Response(new AuthResponseData(AuthMethod.Password, password)),
                        timeout.Token).ConfigureAwait(false);
                }
                else if (msg.Type == MessageType.AuthResult)
                {
                    var result = AuthProtocol.ReadResult(msg.Span);
                    if (result.Ok)
                        return new ConnectOutcome(channel, true, "Authenticated", fingerprint);

                    await channel.DisposeAsync().ConfigureAwait(false);
                    return new ConnectOutcome(null, false, $"Denied: {result.Reason}", fingerprint);
                }
            }

            await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false, "Connection closed before an auth result.", fingerprint);
        }
        catch (OperationCanceledException)
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false, "Timed out.", null);
        }
        catch (Exception ex)
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false, ex.Message, null);
        }
    }
}
