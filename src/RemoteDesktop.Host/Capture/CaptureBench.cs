using System.Diagnostics;
using RemoteDesktop.Host.Capture;

namespace RemoteDesktop.Host;

/// <summary>
/// Standalone capture benchmark: <c>LiteRemoteHost --bench-capture</c>. Measures how long it takes
/// to grab the primary screen with each backend, at native resolution and at a couple of reduced
/// source-scale targets. No network, no viewer — it isolates the one number that dominates a VM
/// host's frame time (screen readback), so you can see on the machine itself which backend and which
/// resolution is fastest. Attach a console (run it from PowerShell/cmd) to see the output.
/// </summary>
internal static class CaptureBench
{
    public static void Run()
    {
        // Ensure there is a console to print to even though this is a WinExe.
        AttachConsole(-1);

        var displays = GdiScreenCapture.EnumerateDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        Console.WriteLine();
        Console.WriteLine($"LiteRemote capture benchmark — display {primary.Width}x{primary.Height}");
        Console.WriteLine("Each row: average / best / worst milliseconds per grab over 60 frames.");
        Console.WriteLine(new string('-', 64));

        BenchGdi("GDI native", primary, 0, 0);
        BenchGdi("GDI -> 1280x720", primary, 1280, 720);
        BenchGdi("GDI -> 960x540", primary, 960, 540);

#if ENABLE_DXGI
        try
        {
            using var dxgi = new DesktopDuplicationCapture(primary);
            Bench("DXGI native", dxgi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DXGI native      : unavailable ({ex.GetType().Name})");
        }
#endif

        Console.WriteLine(new string('-', 64));
        Console.WriteLine("Lower is better. In a VM, 'GDI -> smaller' should be markedly faster than");
        Console.WriteLine("native — that is the source-scale capture doing less GPU readback.");
        Console.WriteLine("Tip: if native GDI is very slow (>60ms), turn OFF 'Accelerate 3D graphics'");
        Console.WriteLine("in the VM's Display settings — it usually cuts this several-fold.");
    }

    private static void BenchGdi(string label, Shared.Models.DisplayInfo display, int tw, int th)
    {
        using var cap = new GdiScreenCapture(display);
        cap.SetTargetSize(tw, th);
        Bench(label, cap);
    }

    private static void Bench(string label, IScreenCapture cap)
    {
        // Warm up (first grabs allocate buffers / initialise the pipeline).
        for (int i = 0; i < 5; i++) cap.Capture(100);

        double sum = 0, best = double.MaxValue, worst = 0;
        int n = 60;
        var sw = new Stopwatch();
        for (int i = 0; i < n; i++)
        {
            sw.Restart();
            cap.Capture(100);
            double ms = sw.Elapsed.TotalMilliseconds;
            sum += ms;
            best = Math.Min(best, ms);
            worst = Math.Max(worst, ms);
        }
        Console.WriteLine($"{label,-17}: {sum / n,6:F1} / {best,6:F1} / {worst,6:F1} ms");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
