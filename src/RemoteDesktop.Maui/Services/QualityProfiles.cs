using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Maui.Services;

/// <summary>Streaming profiles the phone asks the host for (shared by the home list and the edit form).</summary>
public static class QualityProfiles
{
    public static string Normalize(string? q) => q is "saver" or "sharp" ? q : "balanced";

    public static string Label(string? q) => Normalize(q) switch
    {
        "saver" => "Hemat data",
        "sharp" => "Tajam",
        _ => "Seimbang",
    };

    /// <summary>
    /// A phone doesn't need desktop defaults (up to 144 fps native): capping fps is the biggest win for
    /// bandwidth, decode load and battery, while native resolution keeps text crisp when zoomed.
    /// </summary>
    public static SessionSettings BuildSettings(string? quality) => Normalize(quality) switch
    {
        "saver" => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 24, TargetFps = 24,
            ResolutionMode = ResolutionMode.Scaled, ScaledWidth = 1280, ScaledHeight = 720, Quality = 50,
        },
        "sharp" => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 60, TargetFps = 60, Quality = 80,
        },
        _ => new SessionSettings
        {
            PreferredCodec = VideoCodec.H264, FrameRateMode = FrameRateMode.Auto, MaxFps = 30, TargetFps = 30, Quality = 65,
        },
    };
}
