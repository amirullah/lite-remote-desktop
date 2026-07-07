namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Every framed message on the wire starts with a single <see cref="MessageType"/> byte.
/// Keeping the surface small keeps the hot path (video) branch-predictable and cheap.
/// </summary>
public enum MessageType : byte
{
    // --- Handshake / session ---
    Hello = 1,              // client -> host: version + capabilities
    HelloAck = 2,           // host   -> client: accepted capabilities + display list
    AuthRequest = 3,        // host   -> client: challenge (salt/nonce) or "oauth required"
    AuthResponse = 4,       // client -> host: password proof or OAuth id_token
    AuthResult = 5,         // host   -> client: ok / denied (+ reason)

    // --- Video ---
    VideoConfig = 10,       // host -> client: geometry/codec of the stream about to start
    VideoFrame = 11,        // host -> client: one (possibly partial/tile) frame
    KeyFrameRequest = 12,   // client -> host: please resend a full frame

    // --- Input ---
    MouseMove = 20,
    MouseButton = 21,
    MouseWheel = 22,
    KeyEvent = 23,

    // --- Clipboard ---
    ClipboardUpdate = 30,   // either direction: new clipboard content available
    ClipboardRequest = 31,  // pull large payload on demand

    // --- Settings / control ---
    SettingsUpdate = 40,    // client -> host: fps/resolution/display/quality change
    DisplayList = 41,       // host -> client: monitors changed
    Stat = 42,              // host -> client: bandwidth / fps / latency telemetry

    // --- Keepalive ---
    Ping = 90,
    Pong = 91,
    Bye = 99,
}
