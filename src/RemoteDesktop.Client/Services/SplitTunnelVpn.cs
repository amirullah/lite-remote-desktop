using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Brings up an OpenVPN tunnel <b>scoped to a single target host</b> using the bundled engine, entirely
/// from inside LiteRemote — no external OpenVPN app. Because we pass <c>--route-nopull</c> and add just
/// one host route (<c>--route target</c>), the server's pushed <c>redirect-gateway</c> is ignored and
/// only traffic to that host goes through the tunnel; everything else keeps using the normal internet
/// (split tunnel).
///
/// Auth, connection state, and shutdown all travel over OpenVPN's management interface on 127.0.0.1, so
/// the VPN password is never written to disk, and we can stop the elevated engine without a second UAC
/// prompt. Starting the tunnel needs admin (create the virtual adapter + add the route), so the engine
/// is launched with the "runas" verb — that is the one UAC prompt per connect (M1). A promptless helper
/// service is a later milestone.
/// </summary>
public sealed class SplitTunnelVpn : IAsyncDisposable
{
    private Process? _proc;
    private TcpClient? _mgmt;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _loop;
    private volatile bool _connected;

    public bool IsConnected => _connected;

    /// <summary>The bundled engine ships next to the app in an <c>openvpn\</c> subfolder.</summary>
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "openvpn", "openvpn.exe");
    public static bool EngineAvailable => File.Exists(EnginePath);

    /// <summary>
    /// Launch the split tunnel and wait until OpenVPN reports CONNECTED (route in place). One UAC prompt.
    /// </summary>
    public async Task<bool> ConnectAsync(string ovpnProfile, string vpnUser, string vpnPassword,
                                         string targetIp, Action<string> status, CancellationToken ct)
    {
        if (!EngineAvailable) { status("Mesin OpenVPN belum terpasang — install ulang LiteRemote (versi ber-VPN)."); return false; }
        if (!File.Exists(ovpnProfile)) { status("Profil .ovpn tidak ditemukan: " + ovpnProfile); return false; }

        int port = FreeTcpPort();
        string logFile = Path.Combine(Path.GetTempPath(), $"literemote-ovpn-{port}.log");

        // route-nopull → ignore pushed routes (incl. redirect-gateway) → NOT a full tunnel.
        // route <target> → only that host is sent through the tunnel.
        // management + hold + query-passwords → we drive auth/state/stop over 127.0.0.1 (no creds on disk).
        string args =
            $"--config \"{ovpnProfile}\" " +
            $"--management 127.0.0.1 {port} --management-hold --management-query-passwords " +
            "--auth-nocache --auth-retry interact " +
            "--route-nopull " +
            $"--route {targetIp} 255.255.255.255 " +
            "--windows-driver wintun --script-security 1 " +
            $"--verb 3 --log \"{logFile}\"";

        status("Meminta izin admin untuk menyalakan VPN…");
        try
        {
            _proc = Process.Start(new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
                UseShellExecute = true,       // required for the runas verb
                Verb = "runas",               // elevate: create adapter + add route
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) // typically the user declined the UAC prompt
        {
            status("VPN dibatalkan (izin admin ditolak).");
            Services.Diag.Log("SplitTunnelVpn runas failed: " + ex);
            return false;
        }

        status("Menghubungkan ke mesin VPN…");
        if (!await ConnectMgmtAsync(port, TimeSpan.FromSeconds(20), ct))
        {
            status("Mesin VPN tidak merespons. Lihat log: " + logFile);
            return false;
        }

        _loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(vpnUser, vpnPassword, status, connectedTcs, _loop.Token));

        await SendAsync("state on");
        await SendAsync("hold release");   // let openvpn proceed to connect

        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var reg = to.Token.Register(() => connectedTcs.TrySetResult(false));
        using var reg2 = ct.Register(() => connectedTcs.TrySetResult(false));
        _connected = await connectedTcs.Task;
        if (!_connected) status("VPN gagal terhubung (cek user/password myITS atau jaringan).");
        return _connected;
    }

    private async Task<bool> ConnectMgmtAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                _mgmt = c;
                var s = c.GetStream();
                _reader = new StreamReader(s, Encoding.ASCII);
                _writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return true;
            }
            catch { await Task.Delay(400, ct); }
        }
        return false;
    }

    private async Task ReadLoopAsync(string user, string password, Action<string> status,
                                     TaskCompletionSource<bool> connectedTcs, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await _reader!.ReadLineAsync()) != null)
            {
                if (line.StartsWith(">PASSWORD:Need", StringComparison.Ordinal))
                {
                    // openvpn is asking for the auth-user-pass credentials.
                    await SendAsync($"username \"Auth\" \"{Escape(user)}\"");
                    await SendAsync($"password \"Auth\" \"{Escape(password)}\"");
                }
                else if (line.StartsWith(">PASSWORD:Verification Failed", StringComparison.Ordinal))
                {
                    status("Login VPN ditolak (user/password myITS salah).");
                    connectedTcs.TrySetResult(false);
                }
                else if (line.StartsWith(">STATE:", StringComparison.Ordinal))
                {
                    // >STATE:<time>,<state>,<desc>,...
                    var parts = line.Substring(7).Split(',');
                    string state = parts.Length > 1 ? parts[1] : "";
                    switch (state)
                    {
                        case "CONNECTING": status("VPN: menghubungkan…"); break;
                        case "AUTH": status("VPN: memeriksa kredensial…"); break;
                        case "GET_CONFIG": status("VPN: mengambil konfigurasi…"); break;
                        case "ASSIGN_IP": status("VPN: menyiapkan adapter…"); break;
                        case "ADD_ROUTES": status("VPN: menambah route target…"); break;
                        case "CONNECTED": _connected = true; status("VPN tersambung (split-tunnel)."); connectedTcs.TrySetResult(true); break;
                        case "RECONNECTING": status("VPN: menyambung ulang…"); break;
                        case "EXITING": status("VPN berhenti."); _connected = false; connectedTcs.TrySetResult(false); break;
                    }
                }
            }
        }
        catch { /* stream closed on shutdown */ }
        finally { _connected = false; }
    }

    private async Task SendAsync(string cmd)
    {
        try { if (_writer != null) await _writer.WriteLineAsync(cmd); } catch { }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try { await SendAsync("signal SIGTERM"); await Task.Delay(300); } catch { }
        try { _loop?.Cancel(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _mgmt?.Dispose(); } catch { }
        _connected = false;
    }
}
