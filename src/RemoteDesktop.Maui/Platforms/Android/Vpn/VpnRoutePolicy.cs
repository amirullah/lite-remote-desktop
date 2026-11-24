using System.Net;
using System.Net.Sockets;

namespace RemoteDesktop.Maui.Vpn;

/// <summary>
/// Split-tunnel policy (port of the Windows SplitTunnelVpn idea): only the host subnet(s) the server
/// pushes plus a /32 to the session's target go through the tunnel. A default route, or anything as
/// wide as one (prefix shorter than /8, e.g. redirect-gateway def1's 0/1 + 128/1), is NEVER added.
/// </summary>
internal sealed class VpnRoutePolicy
{
    public const int MinPrefix = 8;

    public string? Address { get; private set; }
    public int AddressPrefix { get; private set; } = 32;
    public string? PeerAddress { get; private set; }
    public int Mtu { get; private set; } = 1500;
    public List<(string Net, int Prefix)> Routes { get; } = new();
    public List<string> Dns { get; } = new();
    public List<string> Domains { get; } = new();
    public List<string> TargetIps { get; } = new();

    public void Reset()
    {
        Address = PeerAddress = null; AddressPrefix = 32; Mtu = 1500;
        Routes.Clear(); Dns.Clear(); Domains.Clear();
    }

    /// <summary>IFCONFIG extra: "local netmask-or-peer mtu topology", e.g. "10.8.0.6 255.255.255.0 1500 subnet"
    /// or "10.8.0.6 10.8.0.5 1500 net30".</summary>
    public void OnIfconfig(string extra)
    {
        var p = extra.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !IsV4(p[0])) return;
        Address = p[0];
        if (p.Length >= 3 && int.TryParse(p[2], out var mtu) && mtu is >= 576 and <= 9000) Mtu = mtu;
        var topology = p.Length >= 4 ? p[3] : "";
        if (topology.Equals("subnet", StringComparison.OrdinalIgnoreCase) && TryMaskPrefix(p[1], out var prefix))
            AddressPrefix = prefix;
        else if (TryMaskPrefix(p[1], out var pf) && p[1].StartsWith("255.", StringComparison.Ordinal))
            AddressPrefix = pf;
        else
        {
            // net30 / p2p: point-to-point peer; reach it through a host route.
            AddressPrefix = 32;
            if (IsV4(p[1])) { PeerAddress = p[1]; AddRoute(p[1], 32); }
        }
    }

    /// <summary>ROUTE extra: "network netmask gateway [metric dev]".</summary>
    public void OnRoute(string extra)
    {
        var p = extra.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !IsV4(p[0]) || !TryMaskPrefix(p[1], out var prefix)) return;
        AddRoute(p[0], prefix);
    }

    public void OnDns(string extra)
    {
        var ip = extra.Trim();
        if (IsV4(ip) && !Dns.Contains(ip)) { Dns.Add(ip); AddRoute(ip, 32); }
    }

    public void OnDomain(string extra)
    {
        var d = extra.Trim();
        if (d.Length > 0 && !Domains.Contains(d)) Domains.Add(d);
    }

    public bool AddRoute(string ip, int prefix)
    {
        if (prefix < MinPrefix || prefix > 32 || !IsV4(ip)) return false;    // never a default/near-default route
        var net = Network(ip, prefix);
        if (!Routes.Contains((net, prefix))) Routes.Add((net, prefix));
        return true;
    }

    /// <summary>All routes for the tun: pushed subnets + DNS + /32 per target IP.</summary>
    public IEnumerable<(string Net, int Prefix)> AllRoutes()
    {
        var all = new List<(string, int)>(Routes);
        foreach (var t in TargetIps) if (IsV4(t) && !all.Contains((t, 32))) all.Add((t, 32));
        return all;
    }

    public static bool IsV4(string s) =>
        IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork;

    public static bool TryMaskPrefix(string mask, out int prefix)
    {
        prefix = 0;
        if (!IPAddress.TryParse(mask, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        // must be contiguous ones
        int n = 0;
        while (n < 32 && (v & (1u << (31 - n))) != 0) n++;
        if (n < 32 && (v << n) != 0) return false;
        prefix = n;
        return true;
    }

    private static string Network(string ip, int prefix)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        v &= mask;
        return $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
    }
}
