using System.Net;

namespace RemoteDesktop.Maui.Vpn;

/// <summary>
/// Public entry point for the campus VPN: bring the tunnel up for one session (idempotent: an already
/// connected tunnel with the same profile/user/target is reused) and tear it down when the session ends.
/// </summary>
public static class CampusVpn
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _key;

    public static bool IsEngineAvailable => OpenVpnProcess.BinaryAvailable;

    public static bool IsUp => LiteVpnService.Current?.IsConnected == true;

    /// <summary>Completes when OpenVPN reports CONNECTED. Throws with an Indonesian message on auth
    /// failure, missing engine, refused permission or the 45 s timeout.</summary>
    public static async Task ConnectAsync(string profileText, string user, string password, string targetHost, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var key = $"{profileText.GetHashCode()}|{user}|{password.GetHashCode()}|{targetHost}";
            if (IsUp && key == _key) return;

            if (!OpenVpnProcess.BinaryAvailable)
                throw new InvalidOperationException(
                    "Mesin VPN kampus belum disertakan di aplikasi ini (libovpnexec.so tidak ditemukan). " +
                    "Pasang versi LiteRemote Android yang mendukung VPN kampus.");

            if (!await MainActivity.RequestVpnConsentAsync())
                throw new InvalidOperationException("Izin VPN ditolak. Izinkan permintaan koneksi VPN agar LiteRemote bisa masuk ke jaringan kampus.");
            await MainActivity.RequestNotificationPermissionAsync();

            // Resolve the target BEFORE the tunnel exists (campus names may not resolve later; IP literals are instant).
            var ips = await ResolveAsync(targetHost, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            LiteVpnService svc;
            try { svc = await LiteVpnService.StartAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Layanan VPN Android tidak mulai."); }

            _key = null;
            try
            {
                await svc.StartTunnelAsync(new TunnelRequest(profileText, user, password, ips), timeout.Token);
            }
            catch (TimeoutException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            _key = key;
        }
        finally { Gate.Release(); }
    }

    public static async Task DisconnectAsync()
    {
        await Gate.WaitAsync();
        try
        {
            _key = null;
            if (LiteVpnService.Current is { } svc) await svc.StopTunnelAsync(stopService: true);
        }
        finally { Gate.Release(); }
    }

    private static async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return Array.Empty<string>();
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? new[] { ip.ToString() } : Array.Empty<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            return addrs.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.ToString()).Distinct().ToList();
        }
        catch { return Array.Empty<string>(); }   // pushed routes (or campus DNS) may still cover it
    }
}
