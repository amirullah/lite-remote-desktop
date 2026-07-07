using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesktop.Shared.Relay;

// LiteRemote relay / rendezvous server.
//
// Run it on any machine both sides can reach (a small VPS is plenty — it only forwards
// already-encrypted bytes, so CPU use is trivial and it can never read session contents):
//
//   LiteRemoteRelay [port]          (default 7500)
//
// Hosts register their 9-digit ID over a persistent control connection; viewers ask for an ID and
// the relay splices the two TCP streams together. The ID is protected by a per-host secret so a
// second machine cannot claim an ID that is already bound to a different secret.

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : RelayProtocol.DefaultPort;
var relay = new RelayServer(port);
Console.WriteLine($"[relay] LiteRemote relay listening on 0.0.0.0:{port}");
await relay.RunAsync();

internal sealed class RelayServer
{
    private readonly int _port;

    // id -> registered host control connection
    private readonly ConcurrentDictionary<string, HostEntry> _hosts = new();
    // id -> hash of the secret that first claimed it (anti-hijack, in-memory)
    private readonly ConcurrentDictionary<string, string> _idKeys = new();
    // splice token -> waiting viewer
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Stream>> _sessions = new();

    public RelayServer(int port) => _port = port;

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleAsync(tcp));
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        try
        {
            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = await RelayProtocol.ReadAsync(stream, handshakeCts.Token);
            switch (msg?.Op)
            {
                case "register": await HandleRegisterAsync(tcp, stream, msg); return;
                case "connect": await HandleConnectAsync(tcp, stream, msg); return;
                case "join": await HandleJoinAsync(tcp, stream, msg); return;
                default: tcp.Close(); return;
            }
        }
        catch
        {
            tcp.Close();
        }
    }

    // ---------- host side ----------

    private async Task HandleRegisterAsync(TcpClient tcp, Stream stream, RelayMsg msg)
    {
        var id = RelayProtocol.NormalizeId(msg.Id ?? "");
        if (id.Length != 9 || string.IsNullOrEmpty(msg.Key))
        {
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "bad id/key" });
            tcp.Close();
            return;
        }

        var keyHash = Hash(msg.Key);
        var boundKey = _idKeys.GetOrAdd(id, keyHash);
        if (boundKey != keyHash)
        {
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "id already bound to another host" });
            tcp.Close();
            return;
        }

        var entry = new HostEntry(tcp, stream);
        _hosts.AddOrUpdate(id, entry, (_, old) => { old.Close(); return entry; });
        await entry.SendAsync(new RelayMsg { Ok = true });
        Console.WriteLine($"[relay] host {RelayProtocol.FormatId(id)} online ({tcp.Client.RemoteEndPoint})");

        // Keep reading pings until the host drops.
        try
        {
            while (true)
            {
                var m = await RelayProtocol.ReadAsync(stream);
                if (m is null) break;
                if (m.Op == "ping") await entry.SendAsync(new RelayMsg { Op = "pong" });
            }
        }
        catch { }
        finally
        {
            _hosts.TryRemove(new KeyValuePair<string, HostEntry>(id, entry));
            entry.Close();
            Console.WriteLine($"[relay] host {RelayProtocol.FormatId(id)} offline");
        }
    }

    private async Task HandleJoinAsync(TcpClient tcp, Stream stream, RelayMsg msg)
    {
        if (msg.Session is null || !_sessions.TryRemove(msg.Session, out var waiter))
        {
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "unknown session" });
            tcp.Close();
            return;
        }
        await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = true });
        // Hand the host stream to the waiting viewer handler; it owns the piping from here.
        if (!waiter.TrySetResult(stream)) tcp.Close();
        // Keep this task alive until the pipe closes the socket — nothing else to do here.
    }

    // ---------- viewer side ----------

    private async Task HandleConnectAsync(TcpClient tcp, Stream stream, RelayMsg msg)
    {
        var id = RelayProtocol.NormalizeId(msg.Id ?? "");
        if (!_hosts.TryGetValue(id, out var host))
        {
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "host offline or unknown id" });
            tcp.Close();
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var waiter = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessions[token] = waiter;

        try
        {
            await host.SendAsync(new RelayMsg { Op = "offer", Session = token });
        }
        catch
        {
            _sessions.TryRemove(token, out _);
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "host unreachable" });
            tcp.Close();
            return;
        }

        var joined = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (joined != waiter.Task)
        {
            _sessions.TryRemove(token, out _);
            await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = false, Error = "host did not answer" });
            tcp.Close();
            return;
        }

        var hostStream = await waiter.Task;
        await RelayProtocol.SendAsync(stream, new RelayMsg { Ok = true });
        Console.WriteLine($"[relay] splicing viewer {tcp.Client.RemoteEndPoint} <-> host {RelayProtocol.FormatId(id)}");

        // From here on we are a dumb pipe for the end-to-end TLS session.
        await PipeAsync(stream, hostStream);
        tcp.Close();
    }

    private static async Task PipeAsync(Stream a, Stream b)
    {
        try
        {
            var t1 = a.CopyToAsync(b);
            var t2 = b.CopyToAsync(a);
            await Task.WhenAny(t1, t2);
        }
        catch { }
        finally
        {
            try { a.Dispose(); } catch { }
            try { b.Dispose(); } catch { }
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed class HostEntry
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public HostEntry(TcpClient tcp, Stream stream) { _tcp = tcp; _stream = stream; }

        public async Task SendAsync(RelayMsg msg)
        {
            await _writeLock.WaitAsync();
            try { await RelayProtocol.SendAsync(_stream, msg); }
            finally { _writeLock.Release(); }
        }

        public void Close() { try { _tcp.Close(); } catch { } }
    }
}
