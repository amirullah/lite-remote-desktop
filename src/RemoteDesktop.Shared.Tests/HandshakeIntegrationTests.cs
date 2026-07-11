using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// M-A1 "hello-connect", terbukti runnable: handshake TLS + cert-pinning + auth end-to-end di loopback
/// memakai HANYA RemoteDesktop.Shared — inti jaringan yang akan dipakai klien MAUI (Android/Mac).
/// Membuktikan reuse Shared: MessageChannel, Framing, AuthProtocol (dengan versi AUD-010),
/// CertificateManager (pin), PasswordHasher.
/// </summary>
public class HandshakeIntegrationTests
{
    [Fact]
    public async Task Loopback_TlsPinnedAuthHandshake_Succeeds()
    {
        var certPath = Path.Combine(Path.GetTempPath(), "literemote-hs-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            var cert = CertificateManager.GetOrCreateHostCertificate(certPath);
            var pin = CertificateManager.PublicKeyFingerprint(cert);
            var passwordHash = PasswordHasher.Hash("s3cret!");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // ---- HOST side ----
            var hostTask = Task.Run(async () =>
            {
                using var htcp = await listener.AcceptTcpClientAsync();
                var ssl = new SslStream(htcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(cert, clientCertificateRequired: false,
                    SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);

                await using var ch = new MessageChannel(ssl);
                await ch.SendAsync(AuthProtocol.Request(new AuthRequestData(AuthMethod.Password, "nonce123")));

                int clientVersion = -1;
                bool ok = false;
                await foreach (var m in ch.Inbound.ReadAllAsync())
                {
                    if (m.Type != MessageType.AuthResponse) continue;
                    var resp = AuthProtocol.ReadResponse(m.Span);
                    clientVersion = resp.ProtocolVersion;
                    ok = resp.Method == AuthMethod.Password && PasswordHasher.Verify(resp.Secret, passwordHash);
                    await ch.SendAsync(AuthProtocol.Result(new AuthResultData(ok, ok ? "ok" : "bad", ok ? "tok" : "")));
                    // keep reading until the client closes so the AuthResult is flushed first
                }
                return (clientVersion, ok);
            });

            // ---- CLIENT side (what the mobile viewer will do) ----
            var clientTask = Task.Run(async () =>
            {
                using var ctcp = new TcpClient();
                await ctcp.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(ctcp.GetStream(), leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (_, c, _, _) =>
                    {
                        using var c2 = new X509Certificate2(c!);
                        return CertificateManager.PublicKeyFingerprint(c2) == pin; // trust-on-first-use pin
                    });
                await ssl.AuthenticateAsClientAsync("RemoteDesktopHost");

                await using var ch = new MessageChannel(ssl);
                bool authed = false;
                await foreach (var m in ch.Inbound.ReadAllAsync())
                {
                    if (m.Type == MessageType.AuthRequest)
                    {
                        _ = AuthProtocol.ReadRequest(m.Span);
                        await ch.SendAsync(AuthProtocol.Response(new AuthResponseData(AuthMethod.Password, "s3cret!")));
                    }
                    else if (m.Type == MessageType.AuthResult)
                    {
                        authed = AuthProtocol.ReadResult(m.Span).Ok;
                        break; // done -> dispose closes the socket, letting the host loop end
                    }
                }
                return authed;
            });

            bool authedClient = await clientTask.WaitAsync(TimeSpan.FromSeconds(20));
            var (clientVersion, okHost) = await hostTask.WaitAsync(TimeSpan.FromSeconds(20));
            listener.Stop();

            Assert.True(authedClient, "client should be authenticated");
            Assert.True(okHost, "host should have accepted the password");
            Assert.Equal(ProtocolInfo.Current, clientVersion); // version negotiated over the wire
        }
        finally
        {
            try { File.Delete(certPath); } catch { }
        }
    }

    [Fact]
    public async Task Loopback_WrongPin_RejectsConnection()
    {
        var certPath = Path.Combine(Path.GetTempPath(), "literemote-hs-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            var cert = CertificateManager.GetOrCreateHostCertificate(certPath);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var hostTask = Task.Run(async () =>
            {
                try
                {
                    using var htcp = await listener.AcceptTcpClientAsync();
                    var ssl = new SslStream(htcp.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                }
                catch { /* client aborts on pin mismatch */ }
            });

            var clientTask = Task.Run(async () =>
            {
                using var ctcp = new TcpClient();
                await ctcp.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(ctcp.GetStream(), false,
                    userCertificateValidationCallback: (_, _, _, _) => false); // pin mismatch -> reject
                await ssl.AuthenticateAsClientAsync("RemoteDesktopHost");
            });

            // A mismatched pin must abort the TLS handshake, not silently connect.
            await Assert.ThrowsAnyAsync<AuthenticationException>(() => clientTask.WaitAsync(TimeSpan.FromSeconds(20)));
            try { await hostTask.WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            listener.Stop();
        }
        finally
        {
            try { File.Delete(certPath); } catch { }
        }
    }
}
