using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Relay;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Keeps this host registered at the relay server under its 9-digit ID so viewers can reach it
/// without knowing an IP address (TeamViewer-style). When the relay announces an incoming viewer,
/// we dial a second connection, join the splice token, and hand the raw stream to
/// <see cref="HostServer"/> — from there it is a normal TLS + auth session, so the relay never
/// weakens security, it only provides reachability. Reconnects automatically with backoff.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _id;
    private readonly string _secret;
    private readonly Func<Stream, Task> _onIncoming;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public bool IsRegistered { get; private set; }

    public RelayClient(string relayAddress, string id, string secret, Func<Stream, Task> onIncoming, ILogger log)
    {
        var parts = relayAddress.Split(':', 2);
        _host = parts[0].Trim();
        _port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : RelayProtocol.DefaultPort;
        _id = id;
        _secret = secret;
        _onIncoming = onIncoming;
        _log = log;
    }

    public void Start() => _loop = Task.Run(RegistrationLoopAsync);

    private async Task RegistrationLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                var stream = tcp.GetStream();

                await RelayProtocol.SendAsync(stream, new RelayMsg { Op = "register", Id = _id, Key = _secret }, ct)
                    .ConfigureAwait(false);
                var reply = await RelayProtocol.ReadAsync(stream, ct).ConfigureAwait(false);
                if (reply is not { Ok: true })
                {
                    _log.LogError("Relay refused registration: {Error}", reply?.Error ?? "no reply");
                    IsRegistered = false;
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                IsRegistered = true;
                _log.LogInformation("Registered at relay {Host}:{Port} as ID {Id}",
                    _host, _port, RelayProtocol.FormatId(_id));

                // Read offers; ping every 30s to keep NAT mappings alive.
                // (Fully qualified: WinForms' global using also defines a Timer.)
                using var pinger = new System.Threading.Timer(_ =>
                {
                    try { RelayProtocol.SendAsync(stream, new RelayMsg { Op = "ping" }).GetAwaiter().GetResult(); }
                    catch { }
                }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                while (!ct.IsCancellationRequested)
                {
                    var msg = await RelayProtocol.ReadAsync(stream, ct).ConfigureAwait(false);
                    if (msg is null) break; // relay dropped us — reconnect
                    if (msg.Op == "offer" && msg.Session is not null)
                        _ = Task.Run(() => JoinAsync(msg.Session), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("Relay connection lost: {Message}", ex.Message);
            }
            finally
            {
                IsRegistered = false;
                tcp?.Dispose();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task JoinAsync(string session)
    {
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(_host, _port, _cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            await RelayProtocol.SendAsync(stream, new RelayMsg { Op = "join", Session = session }, _cts.Token)
                .ConfigureAwait(false);
            var reply = await RelayProtocol.ReadAsync(stream, _cts.Token).ConfigureAwait(false);
            if (reply is not { Ok: true }) { tcp.Close(); return; }

            _log.LogInformation("Incoming viewer via relay (session {Session}).", session[..8]);
            await _onIncoming(stream).ConfigureAwait(false); // serves TLS + auth + session, then returns
            tcp.Close();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Relay join failed: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
