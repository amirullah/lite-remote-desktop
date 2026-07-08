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
/// is launched with the "runas" verb — that is the one UAC prompt per connect (M1).
/// </summary>
public sealed class SplitTunnelVpn : IAsyncDisposable
{
    private Process? _proc;
    private TcpClient? _mgmt;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _loop;
    private volatile bool _connected;
    private int _holdReleased;   // 0/1 guard so we release the hold exactly once
    private string _vpnUser = "", _vpnPass = "";

    public bool IsConnected => _connected;

    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "openvpn", "openvpn.exe");
    public static bool EngineAvailable => File.Exists(EnginePath);

    public async Task<bool> ConnectAsync(string ovpnProfile, string vpnUser, string vpnPassword,
                                         string targetIp, Action<string> status, CancellationToken ct)
    {
        if (!EngineAvailable) { status("Mesin OpenVPN belum terpasang — install ulang LiteRemote (versi ber-VPN)."); return false; }
        if (!File.Exists(ovpnProfile)) { status("Profil .ovpn tidak ditemukan: " + ovpnProfile); return false; }
        _vpnUser = vpnUser; _vpnPass = vpnPassword;

        int port = FreeTcpPort();
        string logFile = Path.Combine(Path.GetTempPath(), $"literemote-ovpn-{port}.log");

        // disable-dco → force the userspace (wintun) path; the kernel DCO driver rejects the compression
        //   directives many servers push and then fails to open the interface.
        // route-nopull + route <target> → split tunnel: only the target host goes via the VPN.
        string args =
            $"--config \"{ovpnProfile}\" " +
            $"--management 127.0.0.1 {port} --management-hold --management-query-passwords " +
            "--auth-nocache --auth-retry interact " +
            "--route-nopull " +
            $"--route {targetIp} 255.255.255.255 " +
            "--disable-dco --windows-driver wintun --script-security 1 " +
            $"--verb 4 --log \"{logFile}\"";

        Services.Diag.Log($"[vpn] launching engine on mgmt port {port}; log={logFile}");
        status("Meminta izin admin untuk menyalakan VPN…");
        try
        {
            _proc = Process.Start(new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            status("VPN dibatalkan (izin admin ditolak).");
            Services.Diag.Log("[vpn] runas failed: " + ex);
            return false;
        }

        status("Menghubungkan ke mesin VPN…");
        if (!await ConnectMgmtAsync(port, TimeSpan.FromSeconds(20), ct))
        {
            status("Mesin VPN tidak merespons. Log: " + logFile);
            return false;
        }

        _loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(status, connectedTcs, _loop.Token));

        // Enable state notifications; hold release is driven by the >HOLD notification in ReadLoopAsync.
        await SendAsync("state on");
        // Fallback: if openvpn didn't emit >HOLD (already past it), release shortly anyway.
        _ = Task.Run(async () => { try { await Task.Delay(1500, _loop.Token); await ReleaseHoldAsync(); } catch { } });

        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var reg = to.Token.Register(() => connectedTcs.TrySetResult(false));
        using var reg2 = ct.Register(() => connectedTcs.TrySetResult(false));
        _connected = await connectedTcs.Task;
        if (!_connected) status("VPN gagal terhubung (lihat detail di log).");
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
                _reader = new StreamReader(s, new UTF8Encoding(false));
                _writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                Services.Diag.Log("[vpn] management connected");
                return true;
            }
            catch { try { await Task.Delay(400, ct); } catch { return false; } }
        }
        return false;
    }

    private async Task ReadLoopAsync(Action<string> status, TaskCompletionSource<bool> connectedTcs, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await _reader!.ReadLineAsync()) != null)
            {
                Services.Diag.Log("[vpn] rx: " + line);

                if (line.StartsWith(">HOLD:", StringComparison.Ordinal))
                {
                    await ReleaseHoldAsync();
                }
                else if (line.StartsWith(">PASSWORD:Need", StringComparison.Ordinal))
                {
                    await SendAsync($"username \"Auth\" \"{Escape(_vpnUser)}\"");
                    await SendAsync($"password \"Auth\" \"{Escape(_vpnPass)}\"");
                }
                else if (line.StartsWith(">PASSWORD:Verification Failed", StringComparison.Ordinal))
                {
                    status("Login VPN ditolak (user/password myITS salah).");
                    connectedTcs.TrySetResult(false);
                }
                else if (line.StartsWith(">STATE:", StringComparison.Ordinal))
                {
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
                        case "RECONNECTING": status("VPN: menyambung ulang (cek log)…"); break;
                        case "EXITING": status("VPN berhenti (lihat log)."); _connected = false; connectedTcs.TrySetResult(false); break;
                    }
                }
            }
        }
        catch (Exception ex) { Services.Diag.Log("[vpn] read loop ended: " + ex.GetType().Name); }
        finally { _connected = false; }
    }

    private async Task ReleaseHoldAsync()
    {
        if (Interlocked.Exchange(ref _holdReleased, 1) == 0)
            await SendAsync("hold release");
    }

    private async Task SendAsync(string cmd)
    {
        try
        {
            if (_writer != null)
            {
                Services.Diag.Log("[vpn] tx: " + (cmd.StartsWith("password") ? "password \"Auth\" \"***\"" : cmd));
                await _writer.WriteLineAsync(cmd);
            }
        }
        catch (Exception ex) { Services.Diag.Log("[vpn] tx failed: " + ex.GetType().Name); }
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
