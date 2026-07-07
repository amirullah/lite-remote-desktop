using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Abstraction over the two capture backends. The session picks Desktop Duplication when the GPU
/// supports it (fast, dirty-rect aware, near-zero CPU) and transparently falls back to GDI BitBlt
/// (universal, works over RDP / in VMs / on the secure desktop with a helper).
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>Enumerate the monitors currently attached to the host.</summary>
    static abstract IReadOnlyList<DisplayInfo> EnumerateDisplays();

    /// <summary>The display this instance is bound to.</summary>
    DisplayInfo Display { get; }

    /// <summary>Human-readable backend name for telemetry ("DXGI-Duplication", "GDI-BitBlt").</summary>
    string BackendName { get; }

    /// <summary>
    /// Grab the next frame. Blocks up to <paramref name="timeoutMs"/> for a new one.
    /// Returns null if nothing changed within the timeout (caller should not re-encode).
    /// </summary>
    CapturedFrame? Capture(int timeoutMs);
}
