using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktop.Host.Service;

/// <summary>
/// Tiny file logger for the SYSTEM agent (whose console/ILogger output goes nowhere visible). Writes
/// to <c>%ProgramData%\LiteRemote\agent.log</c> so it can be read back over SSH while debugging the
/// login/secure-desktop path. Enabled only in --agent mode.
/// </summary>
internal static class AgentDiag
{
    public static bool Enabled;
    private static readonly object _lock = new();
    private static readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LiteRemote", "agent.log");

    public static void Log(string msg)
    {
        if (!Enabled) return;
        try
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                System.IO.File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Name of the desktop the CURRENT thread is bound to (to verify SetThreadDesktop worked).</summary>
    public static string ThreadDesktopName()
    {
        try
        {
            IntPtr h = GetThreadDesktop(GetCurrentThreadId());
            if (h == IntPtr.Zero) return "?";
            var sb = new byte[256];
            if (GetUserObjectInformation(h, 2 /*UOI_NAME*/, sb, sb.Length, out _))
                return Encoding.ASCII.GetString(sb).TrimEnd('\0');
        }
        catch { }
        return "?";
    }

    /// <summary>Milliseconds since the last input event in this session — a cheap way to confirm that an
    /// injected SendInput actually registered with the system (drops to ~0 right after a real event).</summary>
    public static long LastInputAgeMs()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii)) return (long)(GetTickCount() - lii.dwTime);
        }
        catch { }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] private static extern bool GetUserObjectInformation(IntPtr h, int idx, byte[] info, int len, out int need);
}
