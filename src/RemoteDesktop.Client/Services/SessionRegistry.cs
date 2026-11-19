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
    /// <summary>Show/hide this session's own top bar/toolbar. The tabbed shell hides it in fullscreen so the
    /// remote fills the whole screen (like standalone RDP), and shows its own hover tab bar instead.</summary>
    void SetChrome(bool visible);
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

    /// <summary>True when sessions live as tabs in the shell — the in-session switch buttons are then
    /// redundant (the tab strip does the switching) and are suppressed.</summary>
    public static bool TabbedShell;

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
    public static void SetPhysicalBounds(Window w, int x, int y, int cw, int ch, bool frameChanged = true)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        // SWP_FRAMECHANGED forces a non-client recalc + full repaint. It's needed after a style change
        // (fullscreen in/out), but NOT for plain move/size — doing it on every step of a maximize/drag
        // animation is what made the docked session window stutter and trail the shell.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, cw, ch,
            SWP_NOZORDER | SWP_NOACTIVATE | (frameChanged ? SWP_FRAMECHANGED : 0));
    }

    /// <summary>
    /// Keep a normal (bordered, non-maximized) window inside its monitor's WORK AREA, in physical pixels.
    /// A fixed DIP size (e.g. 1120×760) becomes 1680×1140 px at 150% scaling — taller than a 1080p screen —
    /// and CenterScreen then pushes the title bar above the top edge where it can't be grabbed.
    /// <paramref name="full"/>: also pull it fully inside horizontally (on first show); a DPI change mid-drag
    /// only fixes size and the top edge so it doesn't fight the user's drag.
    /// </summary>
    public static void FitToWorkArea(Window w, bool full)
    {
        try
        {
            if (!w.IsVisible || w.WindowState != WindowState.Normal || w.WindowStyle == WindowStyle.None) return;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var scr = System.Windows.Forms.Screen.FromHandle(hwnd);
            var wa = scr.WorkingArea;
            var (x, y, cw, ch) = GetPhysicalBounds(w);
            // Deliberately parked off-screen (headless --rdp-test) → leave it alone.
            if (!new System.Drawing.Rectangle(x, y, cw, ch).IntersectsWith(scr.Bounds)) return;
            int nw = Math.Min(cw, wa.Width), nh = Math.Min(ch, wa.Height);
            int nx = full ? Math.Clamp(x, wa.Left, wa.Right - nw) : x;
            int ny = Math.Clamp(y, wa.Top, wa.Bottom - nh);   // the title bar must always be reachable
            if (nx != x || ny != y || nw != cw || nh != ch)
                SetWindowPos(hwnd, IntPtr.Zero, nx, ny, nw, nh, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>Fill a monitor edge-to-edge in physical pixels.</summary>
    public static void FillScreen(Window w, System.Windows.Forms.Screen s)
    {
        var b = s.Bounds;   // physical bounds under PerMonitorV2
        SetPhysicalBounds(w, b.Left, b.Top, b.Width, b.Height);
    }
}
