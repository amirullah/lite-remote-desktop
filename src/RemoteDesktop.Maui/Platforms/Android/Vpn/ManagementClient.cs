using System.Text;
using Android.Net;
using Java.IO;

namespace RemoteDesktop.Maui.Vpn;

/// <summary>
/// Client for OpenVPN's management interface over a filesystem unix socket (LocalSocket). Besides plain
/// text lines, the Android build of OpenVPN (ics-openvpn fork) passes file descriptors as SCM_RIGHTS
/// ancillary data: it SENDS the socket fd it wants protected (PROTECTFD) and RECEIVES the tun fd we
/// create (OPENTUN). Received fds are queued in arrival order and taken by the PROTECTFD handler.
/// </summary>
internal sealed class ManagementClient : IDisposable
{
    private readonly LocalSocket _sock;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly object _writeLock = new();
    private readonly Queue<FileDescriptor> _fds = new();
    private volatile bool _closed;

    /// <summary>Raised on the reader thread for each complete line (without CR/LF).</summary>
    public event Action<string>? Line;
    /// <summary>Raised once when the socket closes (OpenVPN exited or we disposed).</summary>
    public event Action? Closed;

    private ManagementClient(LocalSocket sock)
    {
        _sock = sock;
        _in = sock.InputStream!;
        _out = sock.OutputStream!;
    }

    /// <summary>OpenVPN creates the socket a moment after it starts; retry until it is there.</summary>
    public static async Task<ManagementClient> ConnectAsync(string path, Func<bool> processAlive, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var s = new LocalSocket();
            try
            {
                s.Connect(new LocalSocketAddress(path, LocalSocketAddress.Namespace.Filesystem!));
                return new ManagementClient(s);
            }
            catch (Java.Lang.Exception)
            {
                try { s.Close(); } catch { }
                if (!processAlive()) throw new InvalidOperationException("Mesin VPN berhenti sebelum siap.");
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Mesin VPN tidak merespons (management socket).");
                await Task.Delay(200, ct);
            }
        }
    }

    public void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "ovpn-mgmt" }.Start();

    private void ReadLoop()
    {
        var buf = new byte[4096];
        var pending = new StringBuilder();
        try
        {
            while (!_closed)
            {
                int n = _in.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                // fds ride on the bytes just read: queue them BEFORE handling the line that refers to them.
                try
                {
                    var fds = _sock.GetAncillaryFileDescriptors();
                    if (fds is not null) lock (_fds) foreach (var fd in fds) if (fd is not null) _fds.Enqueue(fd);
                }
                catch { }
                pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                int nl;
                while ((nl = IndexOfNewline(pending)) >= 0)
                {
                    var line = pending.ToString(0, nl).TrimEnd('\r');
                    pending.Remove(0, nl + 1);
                    if (line.Length == 0) continue;
                    try { Line?.Invoke(line); } catch (Exception ex) { Android.Util.Log.Warn("LiteVpn", "handler: " + ex); }
                }
            }
        }
        catch (Exception ex) when (!_closed) { Android.Util.Log.Info("LiteVpn", "mgmt read ended: " + ex.Message); }
        catch { }
        finally { Closed?.Invoke(); }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    public FileDescriptor? TakeFd()
    {
        lock (_fds) return _fds.Count > 0 ? _fds.Dequeue() : null;
    }

    public void Send(string command)
    {
        if (_closed) return;
        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        lock (_writeLock)
        {
            try { _out.Write(bytes, 0, bytes.Length); _out.Flush(); }
            catch (Exception ex) { Android.Util.Log.Warn("LiteVpn", "mgmt write failed: " + ex.Message); }
        }
    }

    /// <summary>Send <paramref name="command"/> with <paramref name="fd"/> attached (SCM_RIGHTS), as
    /// ics-openvpn does for "needok 'OPENTUN' ok".</summary>
    public void SendWithFd(string command, FileDescriptor fd)
    {
        if (_closed) return;
        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        lock (_writeLock)
        {
            try
            {
                _sock.SetFileDescriptorsForSend(new[] { fd });
                _out.Write(bytes, 0, bytes.Length);
                _out.Flush();
            }
            catch (Exception ex) { Android.Util.Log.Warn("LiteVpn", "mgmt fd write failed: " + ex.Message); }
            finally { try { _sock.SetFileDescriptorsForSend(null); } catch { } }
        }
    }

    /// <summary>Quote a value for the management protocol (backslash and double quote escaped).</summary>
    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _sock.ShutdownInput(); } catch { }
        try { _sock.Close(); } catch { }
        lock (_fds) { while (_fds.Count > 0) try { Android.Systems.Os.Close(_fds.Dequeue()); } catch { } }
    }
}
