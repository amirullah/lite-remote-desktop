using System.Text;

namespace RemoteDesktop.Maui.Vpn;

/// <summary>
/// Runs the bundled OpenVPN 2.6 binary (libovpnexec.so, built from the ics-openvpn fork). It must be
/// executed from nativeLibraryDir: Android 10+ forbids exec from writable app dirs (W^X), and the
/// file is named lib*.so so the installer extracts it there (extractNativeLibs=true).
/// </summary>
internal sealed class OpenVpnProcess : IDisposable
{
    public const string BinaryName = "libovpnexec.so";

    private readonly Java.Lang.Process _process;
    private readonly LinkedList<string> _log = new();

    public static string NativeDir => Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!;
    public static string BinaryPath => Path.Combine(NativeDir, BinaryName);
    public static bool BinaryAvailable => File.Exists(BinaryPath);

    private OpenVpnProcess(Java.Lang.Process p)
    {
        _process = p;
        new Thread(PumpOutput) { IsBackground = true, Name = "ovpn-out" }.Start();
    }

    public static OpenVpnProcess Start(string configPath, string workDir)
    {
        if (!BinaryAvailable)
            throw new InvalidOperationException(
                "Mesin VPN kampus belum disertakan di aplikasi ini (libovpnexec.so tidak ditemukan). " +
                "Pasang versi LiteRemote Android yang mendukung VPN kampus.");
        var pb = new Java.Lang.ProcessBuilder(BinaryPath, "--config", configPath);
        pb.Directory(new Java.IO.File(workDir));
        pb.RedirectErrorStream(true);
        var env = pb.Environment();
        if (env is not null) env["LD_LIBRARY_PATH"] = NativeDir;
        var p = pb.Start() ?? throw new InvalidOperationException("Gagal menjalankan mesin VPN.");
        return new OpenVpnProcess(p);
    }

    public bool IsAlive
    {
        get
        {
            try { _process.ExitValue(); return false; }
            catch (Java.Lang.IllegalThreadStateException) { return true; }
            catch { return false; }
        }
    }

    /// <summary>Last lines of OpenVPN's stdout/stderr, for error messages and diagnostics.</summary>
    public string Tail(int lines = 6)
    {
        lock (_log) return string.Join("\n", _log.Skip(Math.Max(0, _log.Count - lines)));
    }

    private void PumpOutput()
    {
        try
        {
            using var reader = new StreamReader(_process.InputStream!, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                Android.Util.Log.Debug("LiteVpn", line);
                lock (_log) { _log.AddLast(line); if (_log.Count > 60) _log.RemoveFirst(); }
            }
        }
        catch { }
    }

    /// <summary>Wait up to <paramref name="ms"/> for a graceful exit (after "signal SIGTERM"), then kill.</summary>
    public async Task StopAsync(int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (IsAlive && DateTime.UtcNow < until) await Task.Delay(100);
        if (IsAlive) try { _process.Destroy(); } catch { }
    }

    public void Dispose()
    {
        try { if (IsAlive) _process.Destroy(); } catch { }
    }
}
