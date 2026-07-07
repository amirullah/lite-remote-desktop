using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Accepts inbound TLS connections and hands each one to a <see cref="HostSession"/>. TLS 1.2/1.3
/// only, using the host's persistent self-signed certificate; the client pins its public key.
/// One controlling client at a time by default — a new authenticated client evicts the old one.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly HostConfig _config;
    private readonly ILogger _log;
    private readonly X509Certificate2 _certificate;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private HostSession? _current;
    private CancellationTokenSource? _currentCts;

    public HostServer(HostConfig config, ILogger log)
    {
        _config = config;
        _log = log;
        _certificate = CertificateManager.GetOrCreateHostCertificate(AppPaths.HostCertificate);
    }

    /// <summary>The public-key fingerprint the user shares out-of-band so a client can pin it.</summary>
    public string Fingerprint => CertificateManager.FormatFingerprint(
        CertificateManager.PublicKeyFingerprint(_certificate));

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var bind = IPAddress.Parse(_config.BindAddress);
        _listener = new TcpListener(bind, _config.Port);
        _listener.Start();
        _log.LogInformation("Listening on {Bind}:{Port}", _config.BindAddress, _config.Port);
        return Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Accept failed."); continue; }

            _ = HandleClientAsync(tcp, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = (IPEndPoint?)tcp.Client.RemoteEndPoint;
        try
        {
            if (remote != null && !CidrMatcher.IsAllowed(remote.Address, _config.AllowedClientCidrs))
            {
                _log.LogWarning("Rejected {Remote}: outside allowed CIDR ranges.", remote);
                tcp.Close();
                return;
            }

            tcp.NoDelay = true; // latency over throughput for interactive input
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            await ssl.AuthenticateAsServerAsync(options, ct).ConfigureAwait(false);
            _log.LogInformation("TLS established with {Remote} ({Protocol})", remote, ssl.SslProtocol);

            // Evict any existing controller — single-seat by default.
            _currentCts?.Cancel();

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts = sessionCts;

            await using var channel = new MessageChannel(ssl);
            var session = new HostSession(channel, _config, _log);
            _current = session;
            await session.RunAsync(sessionCts.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            _log.LogWarning(ex, "TLS handshake failed with {Remote}.", remote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Client handler error for {Remote}.", remote);
        }
        finally
        {
            _log.LogInformation("Client {Remote} disconnected.", remote);
            tcp.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _currentCts?.Cancel();
        _listener?.Stop();
        _certificate.Dispose();
        await Task.CompletedTask;
    }
}
