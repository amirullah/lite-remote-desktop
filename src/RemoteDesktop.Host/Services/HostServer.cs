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
    private RelayClient? _relay;

    /// <summary>This host's 9-digit relay ID (empty when ID access is disabled).</summary>
    public string HostId => _config.HostId;

    /// <summary>True when currently registered and reachable via the relay.</summary>
    public bool RelayOnline => _relay?.IsRegistered ?? false;

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
        // A freshly restarted host can race the previous instance's socket teardown — retry the
        // bind briefly instead of dying with "address already in use".
        for (int attempt = 1; ; attempt++)
        {
            try { _listener.Start(); break; }
            catch (SocketException) when (attempt < 5) { Thread.Sleep(400); }
        }
        _log.LogInformation("Listening on {Bind}:{Port}", _config.BindAddress, _config.Port);

        // Register at the relay for ID-based (TeamViewer-style) access, if configured.
        if (!string.IsNullOrWhiteSpace(_config.RelayAddress))
        {
            _config.EnsureIdentity();
            _relay = new RelayClient(_config.RelayAddress, _config.HostId, _config.RelaySecret,
                stream => ServeStreamAsync(stream, $"relay/{_config.HostId}", _cts.Token), _log);
            _relay.Start();
        }

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
            await ServeStreamAsync(tcp.GetStream(), remote?.ToString() ?? "?", ct).ConfigureAwait(false);
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

    /// <summary>
    /// Run the full TLS + auth + session over an already-connected stream. Used both for direct TCP
    /// clients and for viewer streams handed over by the relay, so ID-based sessions get the exact
    /// same end-to-end security as direct ones.
    /// </summary>
    public async Task ServeStreamAsync(Stream transport, string remote, CancellationToken ct)
    {
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _currentCts?.Cancel();
        if (_relay != null) await _relay.DisposeAsync().ConfigureAwait(false);
        _listener?.Stop();
        _certificate.Dispose();
    }
}
