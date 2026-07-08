using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Keeps the calling thread attached to whatever desktop currently has user input — the normal user
/// desktop, the secure Winlogon desktop (lock/login), or the UAC dimmed desktop. A process running as
/// SYSTEM on WinSta0 can, by re-attaching here, capture and inject on the login screen the way
/// TeamViewer/Avica do. In a normal user session this is a harmless no-op.
///
/// SetThreadDesktop FAILS if the thread owns any window or DC, so the sequence must be:
///   1. <see cref="PendingChanged"/> — detect a switch (no side effect on the thread).
///   2. dispose the current capturer (closing its DCs).
///   3. <see cref="AttachPending"/> — now SetThreadDesktop succeeds.
///   4. recreate the capturer on the new desktop.
/// </summary>
internal static class DesktopFollow
{
    private static string _current = "";
    private static string _pendingName = "";
    private static IntPtr _currentHandle = IntPtr.Zero;
    private static IntPtr _pendingHandle = IntPtr.Zero;

    /// <summary>Enabled only in --agent mode; a no-op otherwise so the interactive tray host is unchanged.</summary>
    public static bool Enabled;

    public static string CurrentDesktop => _current;

    /// <summary>
    /// True when the input desktop differs from the one this thread is on. Opens (but does not yet
    /// switch to) the new desktop; call <see cref="AttachPending"/> after closing the current DCs.
    /// </summary>
    public static bool PendingChanged()
    {
        if (!Enabled) return false;
        IntPtr h = OpenInputDesktop(0, true,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE | GENERIC_READ);
        if (h == IntPtr.Zero) return false;

        string name = GetDesktopName(h);
        if (name.Length == 0 || name == _current)
        {
            CloseDesktop(h);
            return false;
        }
        if (_pendingHandle != IntPtr.Zero) CloseDesktop(_pendingHandle);
        _pendingHandle = h;
        _pendingName = name;
        return true;
    }

    /// <summary>Attach this thread to the pending input desktop. Call only after DCs/windows are closed.</summary>
    public static void AttachPending()
    {
        if (_pendingHandle == IntPtr.Zero) return;
        if (SetThreadDesktop(_pendingHandle))
        {
            if (_currentHandle != IntPtr.Zero) CloseDesktop(_currentHandle);
            _currentHandle = _pendingHandle;
            _current = _pendingName;
        }
        else
        {
            CloseDesktop(_pendingHandle);
        }
        _pendingHandle = IntPtr.Zero;
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
