using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Maui.Services;

/// <summary>Outcome of a hello-connect attempt.</summary>
public sealed record ConnectResult(bool Ok, string Message, string? Fingerprint);

/// <summary>
/// M-A1 "hello-connect" for the mobile viewer: open TLS to a LiteRemote host, surface the host's
/// public-key fingerprint (trust-on-first-use), and run the password auth handshake — all on the same
/// audited RemoteDesktop.Shared contract the Windows client uses. A real pin store, video decode
/// (MediaCodec) and input come in M-A2+; this proves the protocol reuse end-to-end.
/// </summary>
public static class HostConnection
{
    public static async Task<ConnectResult> HelloConnectAsync(
        string host, int port, string password, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
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
                    // TOFU: accept and surface the fingerprint for the user to verify. A persistent
                    // per-endpoint pin store (mismatch -> abort) lands in M-A2.
                    return true;
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }).ConfigureAwait(false);

            await using var channel = new MessageChannel(ssl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            await foreach (var msg in channel.Inbound.ReadAllAsync(timeout.Token).ConfigureAwait(false))
            {
                switch (msg.Type)
                {
                    case MessageType.AuthRequest:
                        _ = AuthProtocol.ReadRequest(msg.Span); // methods/nonce/version
                        await channel.SendAsync(
                            AuthProtocol.Response(new AuthResponseData(AuthMethod.Password, password)),
                            timeout.Token).ConfigureAwait(false);
                        break;

                    case MessageType.AuthResult:
                        var result = AuthProtocol.ReadResult(msg.Span);
                        return new ConnectResult(result.Ok,
                            result.Ok ? "Authenticated ✓" : $"Denied: {result.Reason}", fingerprint);
                }
            }
            return new ConnectResult(false, "Connection closed before an auth result.", fingerprint);
        }
        catch (OperationCanceledException)
        {
            return new ConnectResult(false, "Timed out.", null);
        }
        catch (Exception ex)
        {
            return new ConnectResult(false, ex.Message, null);
        }
    }
}
