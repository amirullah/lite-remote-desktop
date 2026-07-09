using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace RemoteDesktop.Client.Services;

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
}
