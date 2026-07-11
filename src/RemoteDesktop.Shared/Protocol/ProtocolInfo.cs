namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Protocol versioning for cross-client compatibility (Windows/Android/Mac). The version is exchanged
/// inside the existing auth handshake (see <c>AuthRequestData.ProtocolVersion</c> /
/// <c>AuthResponseData.ProtocolVersion</c>) — a peer that predates versioning simply omits the field,
/// which deserializes to <see cref="Current"/> and is treated as v1, so the current apps stay
/// interoperable. (audit M-A0: AUD-010; see docs/PROTOCOL-SPEC.md §3.)
/// </summary>
public static class ProtocolInfo
{
    /// <summary>The wire-protocol version this build speaks.</summary>
    public const int Current = 1;

    /// <summary>The oldest peer version this build still accepts. Bump only on a breaking change.</summary>
    public const int MinSupported = 1;

    /// <summary>True if a peer advertising <paramref name="peerVersion"/> is compatible with this build.</summary>
    public static bool IsCompatible(int peerVersion) => peerVersion >= MinSupported;
}
