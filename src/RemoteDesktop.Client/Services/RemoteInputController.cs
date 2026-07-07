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
    private Window? _window;
    private bool _enabled;

    public RemoteInputController(Image surface, RemoteConnection connection)
    {
        _surface = surface;
        _connection = connection;
    }

    /// <summary>Enable/disable forwarding (e.g. off while a settings flyout is open).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Attach(Window window)
    {
        _window = window;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseDown += OnMouseDown;
        _surface.MouseUp += OnMouseUp;
        _surface.MouseWheel += OnMouseWheel;
        _surface.Focusable = true;

        // Key events are captured at the window level so modifiers work even if focus drifts.
        window.PreviewKeyDown += OnKeyDown;
        window.PreviewKeyUp += OnKeyUp;
    }

    /// <summary>Unhook everything — called when the session ends so keystrokes reach the connect form again.</summary>
    public void Detach()
    {
        _enabled = false;
        _surface.MouseMove -= OnMouseMove;
        _surface.MouseDown -= OnMouseDown;
        _surface.MouseUp -= OnMouseUp;
        _surface.MouseWheel -= OnMouseWheel;
        if (_window != null)
        {
            _window.PreviewKeyDown -= OnKeyDown;
            _window.PreviewKeyUp -= OnKeyUp;
            _window = null;
        }
    }

    // ---------- mouse ----------

    private bool TryNormalize(MouseEventArgs e, out ushort nx, out ushort ny)
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
        if (x < 0 || x > 1 || y < 0 || y > 1) return false;

        nx = (ushort)Math.Round(x * 65535);
        ny = (ushort)Math.Round(y * 65535);
        return true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_enabled) return;
        if (TryNormalize(e, out var nx, out var ny))
            _connection.SendMouseMove(new MouseMoveEvent(nx, ny));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled) return;
        _surface.Focus();
        _surface.CaptureMouse();
        if (TryNormalize(e, out var nx, out var ny) && Map(e.ChangedButton, out var btn))
            _connection.SendMouseButton(new MouseButtonEvent(btn, true, nx, ny));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled) return;
        _surface.ReleaseMouseCapture();
        if (TryNormalize(e, out var nx, out var ny) && Map(e.ChangedButton, out var btn))
            _connection.SendMouseButton(new MouseButtonEvent(btn, false, nx, ny));
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
        SendKey(e, down: true);
        e.Handled = true; // don't let the key drive local WPF navigation
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_enabled) return;
        SendKey(e, down: false);
        e.Handled = true;
    }

    private void SendKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        _connection.SendKey(new KeyEventData((ushort)vk, (ushort)scan, down, IsExtended(key)));
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
