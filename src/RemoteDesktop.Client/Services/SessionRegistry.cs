using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteDesktop.Client.Services;

/// <summary>A remote-session window that can present itself fullscreen on a chosen monitor. Implemented by
/// both RdpWindow and SessionWindow so the switcher can flip between them seamlessly.</summary>
public interface ISessionWindow
{
    bool IsFullscreen { get; }
    System.Windows.Forms.Screen CurrentScreen { get; }
    /// <summary>Enter/leave fullscreen; when entering, cover <paramref name="onScreen"/> (or the current one).</summary>
    void SetFullscreen(bool on, System.Windows.Forms.Screen? onScreen = null);
}

/// <summary>
/// Process-wide registry of open remote-session windows (Windows RDP + the LiteRemote protocol) so any
/// session can offer a one-click switch to the others. Windows register when they open and unregister on
/// close; <see cref="Changed"/> lets each session's toolbar rebuild its switch buttons live. Everything
/// runs on the single WPF UI thread, so no locking is needed.
/// </summary>
public static class SessionRegistry
{
    public sealed record Entry(Window Window, Func<string> Label, string Kind);

    private static readonly List<Entry> _entries = new();

    /// <summary>Raised whenever a session opens or closes.</summary>
    public static event Action? Changed;

    public static void Register(Window w, Func<string> label, string kind)
    {
        if (_entries.Any(e => e.Window == w)) return;
        _entries.Add(new Entry(w, label, kind));
        Raise();
    }

    public static void Unregister(Window w)
    {
        if (_entries.RemoveAll(e => e.Window == w) > 0) Raise();
    }

    /// <summary>Every live session except <paramref name="self"/>, in the order they were opened.</summary>
    public static IReadOnlyList<Entry> Others(Window self) => _entries.Where(e => e.Window != self).ToList();

    /// <summary>A currently-fullscreen session (and its monitor), if any, other than <paramref name="exclude"/>.
    /// Used so a NEW session can join the fullscreen "workspace" on the same screen.</summary>
    public static (Window Window, System.Windows.Forms.Screen Screen)? ActiveFullscreen(Window? exclude = null)
    {
        foreach (var e in _entries)
            if (e.Window != exclude && e.Window is ISessionWindow s && s.IsFullscreen)
                return (e.Window, s.CurrentScreen);
        return null;
    }

    /// <summary>Switch to <paramref name="target"/>. If the CALLER is fullscreen, hand the fullscreen over to
    /// the target on the same monitor — the caller steps back to a window so only ONE session is fullscreen at
    /// a time (otherwise the caller's top-most hover-bar keeps covering the target). Pressing a switch button
    /// therefore instantly shows the other session's screen.</summary>
    public static void SwitchTo(ISessionWindow caller, Window target)
    {
        System.Windows.Forms.Screen? scr = caller.IsFullscreen ? caller.CurrentScreen : null;
        if (scr != null) caller.SetFullscreen(false);   // one session fullscreen at a time — step back
        Activate(target);
        if (scr != null && target is ISessionWindow t) t.SetFullscreen(true, scr);
    }

    /// <summary>Bring a session window to the foreground (restoring it if minimized).</summary>
    public static void Activate(Window w)
    {
        try
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            // Nudge above the caller (which may be a borderless-fullscreen window) without leaving it pinned.
            w.Topmost = true; w.Topmost = false;
            w.Focus();
        }
        catch { }
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }

    // ---- physical-pixel window placement (DPI-robust across mixed per-monitor scaling) ----

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    /// <summary>The window's current bounds in PHYSICAL device pixels (independent of DPI context).</summary>
    public static (int x, int y, int w, int h) GetPhysicalBounds(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        GetWindowRect(hwnd, out var r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Move/size a window using PHYSICAL pixels (Win32 SetWindowPos). This app is PerMonitorV2, so
    /// WPF's Left/Width DIP math misplaces a window when it moves between monitors of different scale (e.g.
    /// 100% ↔ 150%); positioning in device pixels is correct on every monitor.</summary>
    public static void SetPhysicalBounds(Window w, int x, int y, int cw, int ch)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, cw, ch, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Fill a monitor edge-to-edge in physical pixels.</summary>
    public static void FillScreen(Window w, System.Windows.Forms.Screen s)
    {
        var b = s.Bounds;   // physical bounds under PerMonitorV2
        SetPhysicalBounds(w, b.Left, b.Top, b.Width, b.Height);
    }
}
