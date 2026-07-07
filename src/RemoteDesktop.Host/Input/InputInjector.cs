using System.Runtime.InteropServices;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Host.Input;

/// <summary>
/// Injects mouse/keyboard events received from the client into the host session via SendInput.
/// Client coordinates are normalized to 0..65535 over the *captured display*, so they stay correct
/// even when the client resizes its window or the stream is scaled — we map them to that monitor's
/// slice of the virtual desktop here.
/// </summary>
public sealed class InputInjector
{
    private readonly DisplayInfo _display;

    public InputInjector(DisplayInfo display) => _display = display;

    public void MouseMove(in MouseMoveEvent e) => SendMouseAbsolute(e.Nx, e.Ny, MOUSEEVENTF_MOVE, 0, 0);

    public void MouseButton(in MouseButtonEvent e)
    {
        var (flag, data) = e.Button switch
        {
            Shared.Models.MouseButton.Left => (e.Down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            Shared.Models.MouseButton.Right => (e.Down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            Shared.Models.MouseButton.Middle => (e.Down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            Shared.Models.MouseButton.X1 => (e.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, (uint)XBUTTON1),
            Shared.Models.MouseButton.X2 => (e.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, (uint)XBUTTON2),
            _ => (0u, 0u),
        };
        if (flag != 0) SendMouseAbsolute(e.Nx, e.Ny, MOUSEEVENTF_MOVE | flag, data, 0);
    }

    public void MouseWheel(in MouseWheelEvent e)
    {
        if (e.DeltaY != 0) SendMouseAbsolute(e.Nx, e.Ny, MOUSEEVENTF_MOVE | MOUSEEVENTF_WHEEL, 0, e.DeltaY);
        if (e.DeltaX != 0) SendMouseAbsolute(e.Nx, e.Ny, MOUSEEVENTF_MOVE | MOUSEEVENTF_HWHEEL, 0, e.DeltaX);
    }

    public void Key(in KeyEventData e)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki = new KEYBDINPUT
        {
            wVk = 0,
            wScan = e.ScanCode,
            dwFlags = KEYEVENTF_SCANCODE
                      | (e.Down ? 0u : KEYEVENTF_KEYUP)
                      | (e.Extended ? KEYEVENTF_EXTENDEDKEY : 0u),
        };
        // If we somehow have no scancode, fall back to the virtual key.
        if (e.ScanCode == 0)
        {
            input.U.ki.wVk = e.VirtualKey;
            input.U.ki.dwFlags = e.Down ? 0u : KEYEVENTF_KEYUP;
        }
        SendOne(input);
    }

    private void SendMouseAbsolute(ushort nx, ushort ny, uint flags, uint mouseData, int scroll)
    {
        // 1) normalized-over-display -> host pixel on that monitor
        double px = _display.X + (nx / 65535.0) * _display.Width;
        double py = _display.Y + (ny / 65535.0) * _display.Height;

        // 2) host pixel -> 0..65535 over the whole virtual desktop (what SendInput expects)
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);

        int ax = (int)Math.Round((px - vx) * 65535.0 / vw);
        int ay = (int)Math.Round((py - vy) * 65535.0 / vh);

        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT
        {
            dx = ax,
            dy = ay,
            mouseData = (flags & (MOUSEEVENTF_WHEEL | MOUSEEVENTF_HWHEEL)) != 0 ? unchecked((uint)scroll) : mouseData,
            dwFlags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        };
        SendOne(input);
    }

    private static void SendOne(INPUT input)
    {
        var arr = new[] { input };
        SendInput(1, arr, Marshal.SizeOf<INPUT>());
    }

    // ---------- P/Invoke ----------

    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
}
