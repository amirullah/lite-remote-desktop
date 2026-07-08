using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Keeps the calling thread attached to whatever desktop currently has user input — the normal user
/// desktop, the secure Winlogon desktop (lock/login), or the UAC dimmed desktop. A process running as
/// SYSTEM on WinSta0 can, by re-attaching here, capture and inject on the login screen the way
/// TeamViewer/Avica do. In a normal user session this is a harmless no-op (it just stays on Default).
///
/// SetThreadDesktop fails if the thread owns any window or DC, so the capture code must call
/// <see cref="ReattachIfChanged"/> BEFORE it creates its GDI DCs, and recreate the capturer whenever
/// this reports a switch.
/// </summary>
internal static class DesktopFollow
{
    private static string _current = "";
    public static string CurrentDesktop => _current;

    /// <summary>Enabled only in --agent mode; a no-op otherwise so the interactive tray host is unchanged.</summary>
    public static bool Enabled;

    /// <summary>
    /// If the input desktop changed since the last call (or this is the first call), attach the current
    /// thread to it and return true — the caller should then (re)create its capture on the new desktop.
    /// </summary>
    public static bool ReattachIfChanged()
    {
        if (!Enabled) return false;
        IntPtr hDesk = OpenInputDesktop(0, true,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE | GENERIC_READ);
        if (hDesk == IntPtr.Zero) return false;

        string name = GetDesktopName(hDesk);
        if (name.Length == 0 || name == _current)
        {
            CloseDesktop(hDesk);
            return false;
        }

        // Attach this thread to the new input desktop. Keep the handle open for the thread's lifetime
        // on this desktop; the OS closes it when we switch again (we don't hold a ref we must free).
        if (SetThreadDesktop(hDesk))
        {
            _current = name;
            return true;
        }
        CloseDesktop(hDesk);
        return false;
    }

    private static string GetDesktopName(IntPtr hDesk)
    {
        var sb = new byte[256];
        if (GetUserObjectInformation(hDesk, UOI_NAME, sb, sb.Length, out int _))
            return Encoding.ASCII.GetString(sb).TrimEnd('\0');
        return "";
    }

    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_CREATEWINDOW = 0x0002,
        DESKTOP_ENUMERATE = 0x0040, DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint GENERIC_READ = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        byte[] pvInfo, int nLength, out int lpnLengthNeeded);
}
