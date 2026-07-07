using System.Net;
using System.Net.Sockets;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Optional source-IP allow-listing. Combined with a VPN-only bind address this is a cheap,
/// robust way to ensure only machines on the VPN subnet can even attempt to authenticate.
/// </summary>
public static class CidrMatcher
{
    /// <summary>Allowed when the list is empty (no restriction) or the address matches any CIDR.</summary>
    public static bool IsAllowed(IPAddress address, IReadOnlyList<string> cidrs)
    {
        if (cidrs is null || cidrs.Count == 0) return true;
        foreach (var cidr in cidrs)
            if (Matches(address, cidr)) return true;
        return false;
    }

    private static bool Matches(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network)) return false;
        if (!int.TryParse(parts[1], out int prefix)) return false;
        if (network.AddressFamily != address.AddressFamily) return false;

        var netBytes = network.GetAddressBytes();
        var addrBytes = address.GetAddressBytes();
        if (netBytes.Length != addrBytes.Length) return false;

        int fullBytes = prefix / 8;
        int remainingBits = prefix % 8;

        for (int i = 0; i < fullBytes; i++)
            if (netBytes[i] != addrBytes[i]) return false;

        if (remainingBits > 0)
        {
            int mask = 0xFF << (8 - remainingBits) & 0xFF;
            if ((netBytes[fullBytes] & mask) != (addrBytes[fullBytes] & mask)) return false;
        }
        return true;
    }
}
