using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Maui.Storage;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Relay;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Maui.Services;

/// <summary>
/// Result of a connect attempt. On success <see cref="Channel"/> is an open, authenticated
/// <see cref="MessageChannel"/> the caller drives with a <c>ViewerSession</c> (and must dispose).
/// </summary>
public sealed record ConnectOutcome(MessageChannel? Channel, bool Ok, string Message, string? Fingerprint);

/// <summary>
/// Viewer-side connect for the mobile app. Two ways in — direct <c>host:port</c> or by 9-digit ID
/// through a relay — both share one TLS + certificate-pinning + auth path. Pinning is persistent
/// (trust-on-first-use, stored per endpoint): a first connect is trusted and saved; a later key change
/// is refused as a possible MITM. Built on the audited <c>RemoteDesktop.Shared</c> contract.
/// </summary>
public static class ViewerConnection
{
    private static string PinPath => Path.Combine(FileSystem.AppDataDirectory, "pins.json");

    public static async Task<ConnectOutcome> ConnectAsync(
        string host, int port, string password, CancellationToken ct = default)
    {
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return await AuthenticateOverStreamAsync(tcp.GetStream(), host, $"{host}:{port}", password, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ConnectOutcome(null, false, ex.Message, null);
        }
    }

    public static async Task<ConnectOutcome> ConnectViaRelayAsync(
        string relayHost, int relayPort, string id, string password, CancellationToken ct = default)
    {
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(relayHost, relayPort, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();

            // Ask the relay to splice us to the host registered under this id. After "ok" the same
            // socket becomes a raw pipe carrying the end-to-end TLS session — the relay never sees plaintext.
            await RelayProtocol.SendAsync(stream, new RelayMsg { Op = "connect", Id = id }, ct).ConfigureAwait(false);
            var reply = await RelayProtocol.ReadAsync(stream, ct).ConfigureAwait(false);
            if (reply is null || !reply.Ok)
                return new ConnectOutcome(null, false, reply?.Error ?? "Relay did not answer.", null);

            return await AuthenticateOverStreamAsync(stream, id, $"id:{id}", password, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ConnectOutcome(null, false, ex.Message, null);
        }
    }

    private static async Task<ConnectOutcome> AuthenticateOverStreamAsync(
        Stream inner, string targetHost, string endpoint, string password, CancellationToken ct)
    {
        var pins = new PinStore(PinPath);
        MessageChannel? channel = null;
        string? fingerprint = null;
        var pinCheck = PinCheck.FirstUse;
        bool saveOnSuccess = false;

        try
        {
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var c2 = new X509Certificate2(cert);
                    fingerprint = CertificateManager.PublicKeyFingerprint(c2);
                    pinCheck = pins.Check(endpoint, fingerprint);
                    if (pinCheck == PinCheck.Mismatch) return false;         // possible MITM -> abort
                    if (pinCheck == PinCheck.FirstUse) saveOnSuccess = true;  // trust-on-first-use
                    return true;
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
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
                    {
                        if (saveOnSuccess && fingerprint is not null) pins.Pin(endpoint, fingerprint);
                        return new ConnectOutcome(channel, true, "Authenticated", Pretty(fingerprint));
                    }
                    await channel.DisposeAsync().ConfigureAwait(false);
                    return new ConnectOutcome(null, false, $"Denied: {result.Reason}", Pretty(fingerprint));
                }
            }

            await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false, "Connection closed before an auth result.", Pretty(fingerprint));
        }
        catch (AuthenticationException) when (pinCheck == PinCheck.Mismatch)
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false,
                "Host key changed — refused (possible man-in-the-middle).", Pretty(fingerprint));
        }
        catch (Exception ex)
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            return new ConnectOutcome(null, false, ex.Message, Pretty(fingerprint));
        }
    }

    private static string? Pretty(string? hexFingerprint)
        => hexFingerprint is null ? null : CertificateManager.FormatFingerprint(hexFingerprint);
}
