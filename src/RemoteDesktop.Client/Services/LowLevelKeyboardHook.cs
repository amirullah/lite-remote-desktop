using System;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// A WH_KEYBOARD_LL hook that lets Windows shortcuts (Win, Alt+Tab, Ctrl+Esc, …) reach a remote session
/// instead of acting on the local PC. It forwards every key to <see cref="OnKey"/> and suppresses the
/// system-grabbing keys locally so they travel to the remote.
///
/// A leaked low-level hook throttles the whole OS keyboard, so the owner MUST install it only while the
/// session window is focused and uninstall it the moment focus leaves / the session ends. The hook also
/// dies automatically when the process exits, bounding the worst case.
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>Callback: (virtualKey, scanCode, extended, isDown) → true to swallow the key locally.</summary>
    public Func<int, uint, bool, bool, bool>? OnKey;

    public bool Installed => _hook != IntPtr.Zero;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_EXTENDED = 0x01;

    private IntPtr _hook;
    private readonly HookProc _proc;   // kept alive so the delegate isn't GC'd while hooked

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public LowLevelKeyboardHook() { _proc = HookProcImpl; }

    /// <summary>Install (idempotent). Must be called on the UI thread (it needs a running message loop).</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookProcImpl(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (down || up)
            {
                var d = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ext = (d.flags & LLKHF_EXTENDED) != 0;
                try
                {
                    if (OnKey != null && OnKey((int)d.vkCode, d.scanCode, ext, down))
                        return (IntPtr)1;   // swallow locally — the key is going to the remote
                }
                catch { /* never let a hook callback kill the hook chain */ }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
