namespace RemoteDesktop.Shared.Models;

/// <summary>How the host should pick the encoded frame rate.</summary>
public enum FrameRateMode : byte
{
    /// <summary>Host adapts fps to available bandwidth, CPU, and how much of the screen actually changed.</summary>
    Auto = 0,
    Fixed = 1,
}

/// <summary>How the client wants the remote geometry resolved.</summary>
public enum ResolutionMode : byte
{
    /// <summary>Stream the native resolution of the chosen display.</summary>
    Native = 0,
    /// <summary>Scale the stream to a client-specified size (bandwidth saver).</summary>
    Scaled = 1,
    /// <summary>Ask the host to switch the physical display mode to match the client window.</summary>
    MatchClient = 2,
}

/// <summary>Codec used for the video payload. JPEG-tiles is the always-available baseline.</summary>
public enum VideoCodec : byte
{
    /// <summary>Dirty-tile capture, each changed tile JPEG-compressed. No native deps, works everywhere.</summary>
    JpegTiles = 0,
    /// <summary>H.264 via Media Foundation hardware encoder. Negotiated only if both peers support it.</summary>
    H264 = 1,
    /// <summary>H.265/HEVC hardware encoder. Best bandwidth, needs capable GPU on both ends.</summary>
    H265 = 2,
}

public sealed record DisplayInfo(
    int Index,
    string DeviceName,
    int X, int Y,
    int Width, int Height,
    bool IsPrimary,
    int RefreshHz);

/// <summary>Client-driven session preferences. Sent at connect and whenever the user changes a control.</summary>
public sealed record SessionSettings
{
    public FrameRateMode FrameRateMode { get; init; } = FrameRateMode.Auto;
    public int TargetFps { get; init; } = 60;          // used when FrameRateMode == Fixed
    public int MaxFps { get; init; } = 144;            // ceiling in Auto mode
    public ResolutionMode ResolutionMode { get; init; } = ResolutionMode.Native;
    public int ScaledWidth { get; init; }              // used when ResolutionMode == Scaled
    public int ScaledHeight { get; init; }
    public int DisplayIndex { get; init; } = 0;        // which monitor to stream
    public VideoCodec PreferredCodec { get; init; } = VideoCodec.H264;
    public int Quality { get; init; } = 75;            // 1..100, encoder quality hint
    public bool ClipboardSync { get; init; } = true;
    public bool BlankHostScreen { get; init; } = false;// privacy: black out the physical monitor
    public bool LockHostInput { get; init; } = false;  // block local keyboard/mouse while controlled
}

/// <summary>Geometry + codec of the stream the host is about to send. Sent before the first frame.</summary>
public sealed record VideoConfig(
    int Width, int Height,
    VideoCodec Codec,
    int TileSize);

/// <summary>Live telemetry surfaced in the client's status bar.</summary>
public sealed record SessionStat(
    int Fps,
    double MbitsPerSecond,
    int RoundTripMs,
    int EncodeMs,
    string EncoderName);

// --- Input events (normalized 0..65535 coordinate space so resolution changes never desync) ---

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

public readonly record struct MouseMoveEvent(ushort Nx, ushort Ny);
public readonly record struct MouseButtonEvent(MouseButton Button, bool Down, ushort Nx, ushort Ny);
public readonly record struct MouseWheelEvent(short DeltaX, short DeltaY, ushort Nx, ushort Ny);

/// <summary>Key event carrying the Windows virtual-key plus a scancode for layout-independent injection.</summary>
public readonly record struct KeyEventData(ushort VirtualKey, ushort ScanCode, bool Down, bool Extended);
