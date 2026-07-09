using System;

namespace RemoteDesktop.Client.Services;

/// <summary>Which transport a saved session uses.</summary>
public enum SessionKind : byte { LiteRemoteIp = 0, LiteRemoteId = 1, Rdp = 2 }

/// <summary>How a LiteRemote-protocol session authenticates (ignored for RDP).</summary>
public enum ProtocolAuth : byte { Password = 0, Google = 1 }

/// <summary>
/// A saved OpenVPN profile the user can attach to any session. The password (if remembered) lives in
/// <see cref="ClientConfig.Secrets"/> under key <c>vpn:&lt;Id&gt;</c> — never in this record.
/// </summary>
public sealed record VpnProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = "";
    public string OvpnPath { get; init; } = "";
    public string Username { get; init; } = "";
    public bool SavePassword { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label)
        ? System.IO.Path.GetFileNameWithoutExtension(OvpnPath) : Label;
}

/// <summary>
/// One saved/recent connection, spanning all three transports. Only the fields the <see cref="Kind"/>
/// needs are populated. Secrets are never stored here — only the DPAPI key anchor <see cref="Id"/>
/// (password ref = <c>session:&lt;Id&gt;</c>), so the record is safe to serialize in cleartext.
/// </summary>
public sealed record SavedSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public SessionKind Kind { get; init; }
    public string Label { get; init; } = "";

    // addressing
    public string Host { get; init; } = "";     // LiteRemoteIp / Rdp
    public int Port { get; init; }              // 7443 (Lite) / 3389 (RDP)
    public string RelayId { get; init; } = "";  // LiteRemoteId (9 digits)

    // credentials (non-secret parts only)
    public string Username { get; init; } = ""; // RDP user
    public ProtocolAuth Auth { get; init; }     // Lite* only
    public bool SavePassword { get; init; }

    // optional VPN
    public bool UseVpn { get; init; }
    public string? VpnProfileId { get; init; }

    // bookkeeping
    public bool Pinned { get; init; }
    public DateTime LastUsedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Dedupe key — two sessions with the same identity collapse to the newest.</summary>
    public string IdentityKey => Kind == SessionKind.LiteRemoteId
        ? $"ID|{RelayId}"
        : $"{(byte)Kind}|{Host.Trim().ToLowerInvariant()}|{Port}";

    public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label
        : Kind == SessionKind.LiteRemoteId ? RelayId
        : Port is 0 or 3389 or 7443 ? Host : $"{Host}:{Port}";

    public string KindLabel => Kind switch
    {
        SessionKind.LiteRemoteIp => "LiteRemote",
        SessionKind.LiteRemoteId => "ID",
        SessionKind.Rdp => "RDP",
        _ => "?",
    };
}
