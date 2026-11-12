using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
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
        // Encrypt the private key at rest with DPAPI (per-user) so a stolen host.pfx can't be used to
        // impersonate this host to pinned clients. Legacy plaintext files upgrade in place. (AUD-011)
        _certificate = CertificateManager.GetOrCreateHostCertificate(
            AppPaths.HostCertificate, protect: DpapiProtect, unprotect: DpapiUnprotect);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] data) =>
        ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

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
        // Throttle key: the peer IP for direct clients; all relay streams share one bucket since the
        // relay hides the real source.
        string throttleKey = remote.StartsWith("relay/", StringComparison.Ordinal) ? "relay" : StripPort(remote);
        if (IsLockedOut(throttleKey))
        {
            _log.LogWarning("Rejected {Remote}: too many failed logins, temporarily locked out.", remote);
            transport.Dispose();
            return;
        }
        // Bound concurrent pre-auth handshakes so parallel connections can't multiply guesses or pile
        // up Argon2 work (CPU/memory DoS).
        if (!await _handshakeSlots.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            _log.LogWarning("Rejected {Remote}: too many concurrent handshakes.", remote);
            transport.Dispose();
            return;
        }
        int slotHeld = 1;
        void ReleaseSlot() { if (Interlocked.Exchange(ref slotHeld, 0) == 1) _handshakeSlots.Release(); }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // The TLS handshake holds a pre-auth slot, so it MUST be bounded: otherwise 8 silent peers
            // (no credentials needed) could pin every slot and lock all real users out.
            using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hsCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await ssl.AuthenticateAsServerAsync(options, hsCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning("TLS handshake with {Remote} timed out.", remote);
                    ssl.Dispose();   // also closes the transport (relay streams have no other owner)
                    return;
                }
            }
            _log.LogInformation("TLS established with {Remote} ({Protocol})", remote, ssl.SslProtocol);

            await using var channel = new MessageChannel(ssl);
            HostSession? session = null;
            session = new HostSession(channel, _config, _log, async (ok, authCt) =>
            {
                ReleaseSlot();
                if (ok)
                {
                    ResetFailures(throttleKey);
                    // Evict the existing controller only once the newcomer has proven who it is —
                    // otherwise any unauthenticated peer could knock the active user off. Single-seat.
                    var previous = Interlocked.Exchange(ref _currentCts, sessionCts);
                    if (previous != null)
                    {
                        try { previous.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    _current = session;
                }
                else
                {
                    var delay = RecordFailure(throttleKey);
                    _log.LogWarning("Failed login from {Remote}; delaying {Delay}s.", remote, delay.TotalSeconds);
                    await Task.Delay(delay, authCt).ConfigureAwait(false);
                }
            });
            await session.RunAsync(sessionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            ReleaseSlot();
            // Clear the seat only if it is still ours; a newer session may already own it.
            Interlocked.CompareExchange(ref _currentCts, null, sessionCts);
            sessionCts.Dispose();
        }
    }

    // ---------------- failed-login throttling ----------------

    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _handshakeSlots = new(8, 8);
    private readonly object _failLock = new();
    private readonly Dictionary<string, (int Count, DateTime First, DateTime LockedUntil)> _failures = new();

    private static string StripPort(string remote)
    {
        // "1.2.3.4:5678" / "[::1]:5678" -> address only, so reconnects from new ports share a bucket.
        if (IPEndPoint.TryParse(remote, out var ep)) return ep.Address.ToString();
        return remote;
    }

    private bool IsLockedOut(string key)
    {
        lock (_failLock)
            return _failures.TryGetValue(key, out var f) && f.LockedUntil > DateTime.UtcNow;
    }

    private void ResetFailures(string key)
    {
        lock (_failLock) _failures.Remove(key);
    }

    /// <summary>Counts a failed login and returns the backoff to apply before answering (1s doubling, max 30s).</summary>
    private TimeSpan RecordFailure(string key)
    {
        lock (_failLock)
        {
            var now = DateTime.UtcNow;
            // Drop stale entries so the table can't grow without bound.
            if (_failures.Count > 1024)
                foreach (var k in _failures.Where(p => now - p.Value.First > FailureWindow && p.Value.LockedUntil < now)
                                           .Select(p => p.Key).ToList())
                    _failures.Remove(k);

            (int Count, DateTime First, DateTime LockedUntil) f =
                _failures.TryGetValue(key, out var cur) && now - cur.First <= FailureWindow
                    ? cur : (0, now, DateTime.MinValue);
            f.Count++;
            // The shared relay bucket only gets backoff, never a lockout — one attacker on the relay
            // must not be able to lock every legitimate ID-based user out.
            if (f.Count >= MaxFailures && key != "relay")
            {
                f.LockedUntil = now + LockoutDuration;
                _log.LogWarning("Locking out {Key} for {Min} min after {Count} failed logins.",
                    key, LockoutDuration.TotalMinutes, f.Count);
            }
            _failures[key] = f;
            return TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(f.Count - 1, 5)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _currentCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_relay != null) await _relay.DisposeAsync().ConfigureAwait(false);
        _listener?.Stop();
        _certificate.Dispose();
    }
}
