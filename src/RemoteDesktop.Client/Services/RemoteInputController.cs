using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteDesktop.Shared.Models;
using WpfMouseButton = System.Windows.Input.MouseButton;
using ProtoMouseButton = RemoteDesktop.Shared.Models.MouseButton;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Translates WPF pointer/keyboard input over the remote <see cref="Image"/> into normalized
/// protocol events. Coordinates are normalized to 0..65535 against the *remote surface* (accounting
/// for Uniform letterboxing), so they stay correct no matter how the client window is sized or the
/// stream is scaled — the host maps them back to real pixels on its side.
/// </summary>
public sealed class RemoteInputController
{
    private readonly Image _surface;
    private readonly RemoteConnection _connection;
    private readonly LowLevelKeyboardHook _hook;
    private Window? _window;
    private bool _enabled;
    private bool _windowActive;
    private bool _keyboardPassthrough = true;

    // What we told the host is currently held, so focus loss / detach / lost capture can release it —
    // otherwise the key-up or button-up goes to another local window and the remote stays stuck.
    private readonly Dictionary<(ushort Vk, bool Ext), ushort> _heldKeys = new();
    private readonly HashSet<ProtoMouseButton> _heldButtons = new();
    private ushort _lastX, _lastY;

    public RemoteInputController(Image surface, RemoteConnection connection)
    {
        _surface = surface;
        _connection = connection;
        _hook = new LowLevelKeyboardHook { OnKey = OnHookKey };
    }

    /// <summary>Enable/disable forwarding (e.g. off while a settings flyout is open).</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; UpdateHook(); }
    }

    /// <summary>
    /// When true (default), Windows shortcuts (Win, Alt+Tab, Ctrl+Esc, …) are intercepted while the
    /// session window is focused and forwarded to the remote instead of acting on the local PC.
    /// </summary>
    public bool KeyboardPassthrough
    {
        get => _keyboardPassthrough;
        set { _keyboardPassthrough = value; UpdateHook(); }
    }

    public void Attach(Window window)
    {
        _window = window;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseDown += OnMouseDown;
        _surface.MouseUp += OnMouseUp;
        _surface.MouseWheel += OnMouseWheel;
        _surface.LostMouseCapture += OnLostMouseCapture;
        _surface.Focusable = true;

        // Key events are captured at the window level so modifiers work even if focus drifts.
        window.PreviewKeyDown += OnKeyDown;
        window.PreviewKeyUp += OnKeyUp;

        // The low-level hook (for Win/Alt+Tab/Ctrl+Esc) must live ONLY while this window is focused,
        // or it would swallow the whole OS keyboard. Track activation to gate it.
        window.Activated += OnWindowActivated;
        window.Deactivated += OnWindowDeactivated;
        _windowActive = window.IsActive;
        UpdateHook();
    }

    /// <summary>Unhook everything — called when the session ends so keystrokes reach the connect form again.</summary>
    public void Detach()
    {
        ReleaseAllHeld();
        _enabled = false;
        _hook.Uninstall();
        _surface.MouseMove -= OnMouseMove;
        _surface.MouseDown -= OnMouseDown;
        _surface.MouseUp -= OnMouseUp;
        _surface.MouseWheel -= OnMouseWheel;
        _surface.LostMouseCapture -= OnLostMouseCapture;
        if (_window != null)
        {
            _window.PreviewKeyDown -= OnKeyDown;
            _window.PreviewKeyUp -= OnKeyUp;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
        _windowActive = false;
    }

    private void OnWindowActivated(object? sender, EventArgs e) { _windowActive = true; UpdateHook(); }
    private void OnWindowDeactivated(object? sender, EventArgs e) { _windowActive = false; UpdateHook(); ReleaseAllHeld(); }

    /// <summary>Send up events for every key/button the host still believes is down.</summary>
    private void ReleaseAllHeld()
    {
        try
        {
            foreach (var btn in _heldButtons)
                _connection.SendMouseButton(new MouseButtonEvent(btn, false, _lastX, _lastY));
            foreach (var ((vk, ext), scan) in _heldKeys)
                _connection.SendKey(new KeyEventData(vk, scan, false, ext));
        }
        catch { /* connection already gone — host releases on its own at session end */ }
        _heldButtons.Clear();
        _heldKeys.Clear();
    }

    /// <summary>Install the keyboard hook only when we are enabled, want passthrough, and are focused.</summary>
    private void UpdateHook()
    {
        if (_enabled && _keyboardPassthrough && _windowActive)
            _hook.Install();
        else
            _hook.Uninstall();
    }

    /// <summary>
    /// Called by the low-level hook for every key while the session is focused. Forwards to the remote
    /// and returns true for the keys Windows would otherwise consume locally (Win, Alt+Tab, Ctrl+Esc…).
    /// </summary>
    private bool OnHookKey(int vk, uint scan, bool ext, bool down)
    {
        if (!_enabled) return false;
        SendKeyRaw(vk, scan, ext, down);
        // Swallow the system-grabbing keys so they travel to the remote instead of the local desktop.
        // 0x5B/0x5C = L/R Win, 0x09 = Tab, 0x1B = Esc, 0x5D = Apps/Menu.
        return vk is 0x5B or 0x5C or 0x09 or 0x1B or 0x5D;
    }

    // ---------- mouse ----------

    /// <param name="clamp">Clamp out-of-picture positions to the edge instead of failing — used while
    /// the mouse is captured so drags past the edge still move and, crucially, still release.</param>
    private bool TryNormalize(MouseEventArgs e, out ushort nx, out ushort ny, bool clamp = false)
    {
        nx = ny = 0;
        double surfW = _surface.Source?.Width ?? 0, surfH = _surface.Source?.Height ?? 0;
        if (surfW <= 0 || surfH <= 0) return false;

        var pos = e.GetPosition(_surface);
        double elemW = _surface.ActualWidth, elemH = _surface.ActualHeight;
        if (elemW <= 0 || elemH <= 0) return false;

        // Uniform-fit content rectangle inside the element (letterbox math).
        double scale = Math.Min(elemW / surfW, elemH / surfH);
        double contentW = surfW * scale, contentH = surfH * scale;
        double offX = (elemW - contentW) / 2, offY = (elemH - contentH) / 2;

        double x = (pos.X - offX) / contentW;
        double y = (pos.Y - offY) / contentH;
        if (clamp)
        {
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
        }
        else if (x < 0 || x > 1 || y < 0 || y > 1) return false;

        nx = (ushort)Math.Round(x * 65535);
        ny = (ushort)Math.Round(y * 65535);
        _lastX = nx; _lastY = ny;
        return true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_enabled) return;
        if (TryNormalize(e, out var nx, out var ny, clamp: _surface.IsMouseCaptured))
            _connection.SendMouseMove(new MouseMoveEvent(nx, ny));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled) return;
        _surface.Focus();
        _surface.CaptureMouse();
        if (TryNormalize(e, out var nx, out var ny) && Map(e.ChangedButton, out var btn))
        {
            _heldButtons.Add(btn);
            _connection.SendMouseButton(new MouseButtonEvent(btn, true, nx, ny));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled) return;
        // Always release a button whose down we sent, clamping the position — a release over the
        // letterbox or outside the window must not leave the remote button held.
        if (Map(e.ChangedButton, out var btn) && _heldButtons.Remove(btn))
        {
            if (!TryNormalize(e, out var nx, out var ny, clamp: true)) { nx = _lastX; ny = _lastY; }
            _connection.SendMouseButton(new MouseButtonEvent(btn, false, nx, ny));
        }
        // Only drop capture once no button is still down, else a second held button loses its up.
        if (_heldButtons.Count == 0) _surface.ReleaseMouseCapture();
    }

    // Capture taken away (Alt+Tab, popup, window hidden) while a button is down: the up will never
    // reach us, so release it on the remote now.
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        try
        {
            foreach (var btn in _heldButtons)
                _connection.SendMouseButton(new MouseButtonEvent(btn, false, _lastX, _lastY));
        }
        catch { /* connection gone */ }
        _heldButtons.Clear();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_enabled) return;
        if (TryNormalize(e, out var nx, out var ny))
            _connection.SendMouseWheel(new MouseWheelEvent(0, (short)e.Delta, nx, ny));
    }

    private static bool Map(WpfMouseButton b, out ProtoMouseButton mapped)
    {
        mapped = b switch
        {
            WpfMouseButton.Left => ProtoMouseButton.Left,
            WpfMouseButton.Right => ProtoMouseButton.Right,
            WpfMouseButton.Middle => ProtoMouseButton.Middle,
            WpfMouseButton.XButton1 => ProtoMouseButton.X1,
            WpfMouseButton.XButton2 => ProtoMouseButton.X2,
            _ => (ProtoMouseButton)255,
        };
        return (byte)mapped != 255;
    }

    // ---------- keyboard ----------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_enabled) return;
        // When the low-level hook is live it already forwarded this key — just swallow it locally so
        // WPF navigation doesn't also react, and don't send it twice.
        if (_hook.Installed) { e.Handled = true; return; }
        SendKey(e, down: true);
        e.Handled = true; // don't let the key drive local WPF navigation
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_enabled) return;
        if (_hook.Installed) { e.Handled = true; return; }
        SendKey(e, down: false);
        e.Handled = true;
    }

    private void SendKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        SendTrackedKey((ushort)vk, (ushort)scan, down, IsExtended(key));
    }

    private void SendTrackedKey(ushort vk, ushort scan, bool down, bool ext)
    {
        if (down) _heldKeys[(vk, ext)] = scan;
        else _heldKeys.Remove((vk, ext));
        _connection.SendKey(new KeyEventData(vk, scan, down, ext));
    }

    /// <summary>Forward a key straight from the low-level hook (already has vk/scan/extended).</summary>
    private void SendKeyRaw(int vk, uint scan, bool ext, bool down)
    {
        if (vk == 0) return;
        if (scan == 0) scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        SendTrackedKey((ushort)vk, (ushort)scan, down, ext);
    }

    private static bool IsExtended(Key key) => key switch
    {
        Key.Left or Key.Right or Key.Up or Key.Down or
        Key.Home or Key.End or Key.PageUp or Key.PageDown or
        Key.Insert or Key.Delete or Key.RightCtrl or Key.RightAlt or
        Key.NumLock or Key.PrintScreen or Key.Apps => true,
        _ => false,
    };

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
