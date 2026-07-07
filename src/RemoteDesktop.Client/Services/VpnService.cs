using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Brings up an OpenVPN tunnel scoped so that <b>only the remote-desktop connection</b> uses it —
/// the rest of the machine keeps its normal internet. It does this without a kernel driver by:
///
///   1. launching <c>openvpn.exe</c> with <c>--route-nopull</c> (ignore server-pushed routes) and
///      a single host route for the remote host through the tunnel;
///   2. discovering the tunnel adapter's local IPv4;
///   3. handing that IP back so <see cref="RemoteConnection"/> binds its socket to it.
///
/// Because only our socket sources from the tunnel IP and only the host's IP is routed through it,
/// no other application's traffic is affected. Full app-scoped filtering (all of the app's traffic,
/// regardless of destination) would need a WFP callout driver — see docs/VPN.md.
/// </summary>
public sealed class VpnService : IAsyncDisposable
{
    private Process? _process;
    private readonly string _openVpnExe;

    public IPAddress? TunnelAddress { get; private set; }
    public bool IsUp => TunnelAddress != null;

    public VpnService(string? openVpnExePath = null)
    {
        _openVpnExe = openVpnExePath ?? "openvpn"; // rely on PATH / bundled binary by default
    }

    /// <summary>
    /// Start the tunnel for <paramref name="remoteHost"/> using <paramref name="ovpnProfilePath"/>.
    /// Returns the tunnel-local IPv4 to bind the connection socket to, or throws on failure.
    /// </summary>
    public async Task<IPAddress> StartAsync(string ovpnProfilePath, string remoteHost, CancellationToken ct = default)
    {
        if (!File.Exists(ovpnProfilePath))
            throw new FileNotFoundException("OpenVPN profile not found.", ovpnProfilePath);

        var hostIp = await ResolveAsync(remoteHost, ct).ConfigureAwait(false);

        // Snapshot existing tunnel-capable adapters so we can detect the new one.
        var before = SnapshotTunnelAdapters();

        var psi = new ProcessStartInfo
        {
            FileName = _openVpnExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(ovpnProfilePath);
        // Don't let the server rewrite our default route…
        psi.ArgumentList.Add("--route-nopull");
        // …instead route only the remote host through the tunnel.
        psi.ArgumentList.Add("--route");
        psi.ArgumentList.Add(hostIp.ToString());
        psi.ArgumentList.Add("255.255.255.255");
        psi.ArgumentList.Add("vpn_gateway");

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OpenVPN.");

        // Poll for the tunnel adapter to come up and acquire an address (up to 30s).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"OpenVPN exited early (code {_process.ExitCode}).");

            var addr = FindNewTunnelAddress(before);
            if (addr != null)
            {
                TunnelAddress = addr;
                return addr;
            }
            await Task.Delay(500, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("VPN tunnel did not come up within 30 seconds.");
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private static HashSet<string> SnapshotTunnelAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsTunnelAdapter)
            .Select(n => n.Id)
            .ToHashSet();

    private static IPAddress? FindNewTunnelAddress(HashSet<string> before)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsTunnelAdapter(nic) || nic.OperationalStatus != OperationalStatus.Up) continue;
            // Prefer a freshly-appeared adapter, but also accept one that just came up.
            var v4 = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (v4 != null && (!before.Contains(nic.Id) || nic.OperationalStatus == OperationalStatus.Up))
                return v4.Address;
        }
        return null;
    }

    private static bool IsTunnelAdapter(NetworkInterface nic)
    {
        var name = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return name.Contains("tap") || name.Contains("wintun") || name.Contains("openvpn")
               || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch { }
        _process?.Dispose();
        TunnelAddress = null;
    }
}
