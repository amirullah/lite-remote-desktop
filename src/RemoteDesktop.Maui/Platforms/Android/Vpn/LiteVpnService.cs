using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;

namespace RemoteDesktop.Maui.Vpn;

/// <summary>What one tunnel needs.</summary>
internal sealed record TunnelRequest(string Profile, string User, string Password, IReadOnlyList<string> TargetIps);

/// <summary>
/// The VpnService that owns the tun interface for the campus VPN. OpenVPN runs as a separate process
/// and asks us (management NEED-OK) to protect its socket and to open the tun; we build the tun with
/// only split routes and allow only LiteRemote itself through it.
/// </summary>
[Service(Name = "com.literemote.viewer.LiteVpnService",
         Permission = "android.permission.BIND_VPN_SERVICE",
         Exported = false,
         ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class LiteVpnService : VpnService
{
    private const string Tag = "LiteVpn";
    private const string ChannelId = "campus-vpn";
    private const int NotificationId = 0x4C56;

    private static TaskCompletionSource<LiteVpnService>? _started;
    internal static LiteVpnService? Current { get; private set; }

    private readonly object _gate = new();
    private OpenVpnProcess? _process;
    private ManagementClient? _mgmt;
    private ParcelFileDescriptor? _tun;
    private TunnelRequest? _request;
    private readonly VpnRoutePolicy _policy = new();
    private TaskCompletionSource? _connected;
    private string? _configPath;
    private volatile bool _stopping;

    /// <summary>True while OpenVPN reports CONNECTED and the tun is open.</summary>
    internal bool IsConnected { get; private set; }

    /// <summary>Raised (any thread) when an established tunnel goes down.</summary>
    internal static event Action? TunnelLost;

    // ---------------- service lifecycle ----------------

    internal static Task<LiteVpnService> StartAsync(CancellationToken ct)
    {
        if (Current is { } c) return Task.FromResult(c);
        var tcs = new TaskCompletionSource<LiteVpnService>(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = tcs;
        var ctx = Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(LiteVpnService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
        ct.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        GoForeground();
        Current = this;
        _started?.TrySetResult(this);
        return StartCommandResult.NotSticky;
    }

    private void GoForeground()
    {
        try
        {
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && nm is not null)
                nm.CreateNotificationChannel(new NotificationChannel(ChannelId, "VPN kampus", NotificationImportance.Low));

            var open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
            var pi = open is null ? null : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var n = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("VPN kampus aktif")!
                .SetContentText("LiteRemote tersambung ke jaringan kampus. Hanya LiteRemote yang lewat VPN.")!
                .SetSmallIcon(global::Android.Resource.Drawable.IcLockLock)!
                .SetOngoing(true)!
                .SetContentIntent(pi)!
                .Build()!;
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, n, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, n);
        }
        catch (Exception ex)
        {
            // A VpnService stays bound by the system even without foreground status, so keep going.
            Android.Util.Log.Warn(Tag, "foreground failed: " + ex.Message);
        }
    }

    public override void OnRevoke()
    {
        // The user started another VPN or revoked ours in Settings.
        Android.Util.Log.Info(Tag, "VPN permission revoked");
        _ = StopTunnelAsync(stopService: true);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        _ = StopTunnelAsync(stopService: false);
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    // ---------------- tunnel ----------------

    /// <summary>Start OpenVPN and complete when it reports CONNECTED. Throws an Indonesian message on failure.</summary>
    internal async Task StartTunnelAsync(TunnelRequest req, CancellationToken ct)
    {
        await StopTunnelAsync(stopService: false);
        _stopping = false;
        _request = req;
        _policy.Reset();
        _policy.TargetIps.Clear();
        _policy.TargetIps.AddRange(req.TargetIps);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connected = tcs;

        var dir = FilesDir!.AbsolutePath;
        var sock = Path.Combine(dir, "ovpn-mgmt.sock");
        try { File.Delete(sock); } catch { }
        _configPath = Path.Combine(dir, "campus.ovpn.conf");
        File.WriteAllText(_configPath, BuildConfig(req.Profile, sock, CacheDir!.AbsolutePath));

        var proc = OpenVpnProcess.Start(_configPath, dir);
        lock (_gate) _process = proc;
        try
        {
            var mgmt = await ManagementClient.ConnectAsync(sock, () => proc.IsAlive, ct);
            lock (_gate) _mgmt = mgmt;
            mgmt.Line += OnLine;
            mgmt.Closed += () => OnEngineGone("mesin VPN berhenti");
            mgmt.Start();
            mgmt.Send("state on");
            mgmt.Send("hold release");   // harmless if not yet holding; >HOLD is answered too

            using (ct.Register(() => tcs.TrySetCanceled()))
                await tcs.Task;
            // Config holds inline keys: don't leave it on disk longer than needed.
            try { File.Delete(_configPath); } catch { }
        }
        catch (System.OperationCanceledException)
        {
            var tail = proc.Tail(3);
            await StopTunnelAsync(stopService: false);
            throw new TimeoutException("VPN kampus tidak tersambung dalam batas waktu." + (tail.Length > 0 ? "\n" + tail : ""));
        }
        catch
        {
            await StopTunnelAsync(stopService: false);
            throw;
        }
    }

    private static string BuildConfig(string profile, string sock, string tmp)
    {
        var sb = new System.Text.StringBuilder(profile.TrimEnd()).Append("\n\n# ---- LiteRemote ----\n");
        sb.Append("management ").Append(sock).Append(" unix\n");
        sb.Append("management-query-passwords\n");
        sb.Append("management-hold\n");
        sb.Append("tmp-dir ").Append(tmp).Append('\n');
        if (Regex.IsMatch(profile, @"^\s*(client|pull)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            sb.Append("pull-filter ignore \"redirect-gateway\"\n");
            sb.Append("pull-filter ignore \"block-outside-dns\"\n");
        }
        if (!Regex.IsMatch(profile, @"^\s*verb\s", RegexOptions.Multiline)) sb.Append("verb 3\n");
        return sb.ToString();
    }

    private static readonly Regex NeedOk = new(@"^>NEED-OK:Need '([^']+)' (?:confirmation|ok) MSG:(.*)$");

    private void OnLine(string line)
    {
        var mgmt = _mgmt;
        if (mgmt is null) return;
        if (line.StartsWith(">HOLD:", StringComparison.Ordinal)) { mgmt.Send("hold release"); return; }
        if (line.StartsWith(">PASSWORD:", StringComparison.Ordinal)) { OnPassword(mgmt, line[10..]); return; }
        if (line.StartsWith(">STATE:", StringComparison.Ordinal)) { OnState(line[7..]); return; }
        if (line.StartsWith(">FATAL:", StringComparison.Ordinal)) { Fail("VPN kampus gagal: " + line[7..]); return; }
        var m = NeedOk.Match(line);
        if (m.Success) { OnNeedOk(mgmt, m.Groups[1].Value, m.Groups[2].Value.Trim()); return; }
        if (line.StartsWith(">LOG:", StringComparison.Ordinal) || line.StartsWith(">INFO:", StringComparison.Ordinal))
            Android.Util.Log.Debug(Tag, line);
    }

    private void OnPassword(ManagementClient mgmt, string msg)
    {
        var req = _request;
        if (req is null) return;
        if (msg.StartsWith("Verification Failed", StringComparison.Ordinal))
        {
            Fail("Login VPN kampus ditolak (username atau password VPN salah).");
            return;
        }
        if (msg.Contains("'Auth'", StringComparison.Ordinal) && msg.Contains("username/password", StringComparison.Ordinal))
        {
            if (req.User.Length == 0 || req.Password.Length == 0)
            {
                Fail("Server VPN kampus meminta username dan password. Isi di pengaturan sesi.");
                return;
            }
            mgmt.Send("username 'Auth' " + ManagementClient.Quote(req.User));
            mgmt.Send("password 'Auth' " + ManagementClient.Quote(req.Password));
            return;
        }
        if (msg.Contains("'Private Key'", StringComparison.Ordinal))
        {
            Fail("Kunci privat di profil VPN memakai password — belum didukung di Android.");
            return;
        }
        if (msg.Contains("'HTTP Proxy'", StringComparison.Ordinal))
        {
            Fail("Profil VPN memakai proxy HTTP berpassword — belum didukung.");
            return;
        }
        Fail("Server VPN meminta data yang belum didukung (" + msg + ").");
    }

    private void OnState(string body)
    {
        // timestamp,STATE,detail,local-ip,remote-ip,...
        var p = body.Split(',');
        var state = p.Length > 1 ? p[1] : "";
        var detail = p.Length > 2 ? p[2] : "";
        Android.Util.Log.Info(Tag, "state " + state + " " + detail);
        switch (state)
        {
            case "CONNECTED":
                if (detail.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) || _tun is not null)
                {
                    IsConnected = true;
                    _connected?.TrySetResult();
                }
                else Fail("VPN tersambung tetapi sebagian gagal (" + detail + ").");
                break;
            case "RECONNECTING":
                if (IsConnected) { IsConnected = false; TunnelLost?.Invoke(); }
                if (detail.Contains("auth-failure", StringComparison.OrdinalIgnoreCase))
                    Fail("Login VPN kampus ditolak (username atau password VPN salah).");
                break;
            case "EXITING":
                Fail(detail.Contains("auth-failure", StringComparison.OrdinalIgnoreCase)
                    ? "Login VPN kampus ditolak (username atau password VPN salah)."
                    : "VPN kampus berhenti (" + detail + ").");
                break;
        }
    }

    private void OnNeedOk(ManagementClient mgmt, string what, string extra)
    {
        switch (what)
        {
            case "PROTECTFD":
            {
                // OpenVPN's server socket must bypass our own tun, or it would loop into itself.
                var fd = mgmt.TakeFd();
                if (fd is not null)
                {
                    try
                    {
                        using var dup = ParcelFileDescriptor.Dup(fd);
                        if (dup is null || !Protect(dup.Fd)) Android.Util.Log.Warn(Tag, "protect() failed");
                    }
                    catch (Exception ex) { Android.Util.Log.Warn(Tag, "protect: " + ex.Message); }
                    try { Android.Systems.Os.Close(fd); } catch { }
                }
                else Android.Util.Log.Warn(Tag, "PROTECTFD without fd");
                mgmt.Send("needok 'PROTECTFD' ok");
                break;
            }
            case "IFCONFIG": _policy.OnIfconfig(extra); mgmt.Send("needok 'IFCONFIG' ok"); break;
            case "ROUTE": _policy.OnRoute(extra); mgmt.Send("needok 'ROUTE' ok"); break;
            case "DNSSERVER": _policy.OnDns(extra); mgmt.Send("needok 'DNSSERVER' ok"); break;
            case "DNSDOMAIN": _policy.OnDomain(extra); mgmt.Send("needok 'DNSDOMAIN' ok"); break;
            case "PERSIST_TUN_ACTION":
                // Always rebuild the tun with the fresh settings (open the new one before closing the old).
                mgmt.Send("needok 'PERSIST_TUN_ACTION' OPEN_BEFORE_CLOSE");
                break;
            case "OPENTUN":
                OpenTun(mgmt);
                break;
            default:
                // IFCONFIG6 / ROUTE6 / DNS6SERVER / HTTPPROXY …: acknowledged, not used (IPv4 split only).
                mgmt.Send($"needok '{what}' ok");
                break;
        }
    }

    private void OpenTun(ManagementClient mgmt)
    {
        try
        {
            if (_policy.Address is null) { mgmt.Send("needok 'OPENTUN' cancel"); Fail("Server VPN tidak memberi alamat IPv4."); return; }
            var b = new Builder(this);
            b.SetSession("VPN kampus (LiteRemote)");
            b.AddAddress(_policy.Address, _policy.AddressPrefix);
            b.SetMtu(_policy.Mtu);
            foreach (var (net, prefix) in _policy.AllRoutes())
            {
                try { b.AddRoute(net, prefix); }
                catch (Exception ex) { Android.Util.Log.Warn(Tag, $"route {net}/{prefix}: {ex.Message}"); }
            }
            foreach (var dns in _policy.Dns) try { b.AddDnsServer(dns); } catch { }
            foreach (var d in _policy.Domains) try { b.AddSearchDomain(d); } catch { }
            b.AddAllowedApplication(PackageName!);   // per-app: only LiteRemote uses the tunnel

            var tun = b.Establish();
            if (tun is null)
            {
                mgmt.Send("needok 'OPENTUN' cancel");
                Fail("Izin VPN Android dicabut. Coba sambung lagi dan izinkan permintaan VPN.");
                return;
            }
            mgmt.SendWithFd("needok 'OPENTUN' ok", tun.FileDescriptor!);
            ParcelFileDescriptor? old;
            lock (_gate) { old = _tun; _tun = tun; }
            try { old?.Close(); } catch { }
            _policy.Routes.Clear();   // next IFCONFIG/ROUTE round (after a reconnect) starts fresh
            _policy.Dns.Clear();
            _policy.Domains.Clear();
        }
        catch (Exception ex)
        {
            mgmt.Send("needok 'OPENTUN' cancel");
            Fail("Gagal membuat antarmuka VPN: " + ex.Message);
        }
    }

    private void Fail(string message)
    {
        Android.Util.Log.Warn(Tag, message);
        var tcs = _connected;
        if (tcs is not null && !tcs.Task.IsCompleted)
        {
            tcs.TrySetException(new InvalidOperationException(message));
            _ = StopTunnelAsync(stopService: false);
        }
        else if (IsConnected)
        {
            IsConnected = false;
            TunnelLost?.Invoke();
        }
    }

    private void OnEngineGone(string why)
    {
        if (_stopping) return;
        var tail = _process?.Tail(3) ?? "";
        Fail("VPN kampus terputus: " + why + (tail.Length > 0 ? "\n" + tail : ""));
        _ = StopTunnelAsync(stopService: false);
    }

    /// <summary>SIGTERM OpenVPN, close the tun. With <paramref name="stopService"/> also leave foreground and stop.</summary>
    internal async Task StopTunnelAsync(bool stopService)
    {
        _stopping = true;
        bool wasUp = IsConnected;
        IsConnected = false;
        ManagementClient? mgmt; OpenVpnProcess? proc; ParcelFileDescriptor? tun;
        lock (_gate) { mgmt = _mgmt; proc = _process; tun = _tun; _mgmt = null; _process = null; _tun = null; }
        _connected?.TrySetException(new System.OperationCanceledException("VPN dihentikan."));
        if (mgmt is not null) { mgmt.Send("signal SIGTERM"); }
        if (proc is not null) { await proc.StopAsync(); proc.Dispose(); }
        mgmt?.Dispose();
        try { tun?.Close(); } catch { }
        if (_configPath is not null) try { File.Delete(_configPath); } catch { }
        if (wasUp) TunnelLost?.Invoke();
        if (stopService)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
                else StopForeground(true);
            }
            catch { }
            StopSelf();
            if (Current == this) Current = null;
        }
    }
}
