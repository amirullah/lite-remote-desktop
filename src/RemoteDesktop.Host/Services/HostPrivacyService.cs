using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Implements the two privacy toggles the client can request:
///   • <b>Lock host input</b> — block the physical keyboard/mouse at the host so a bystander can't
///     interfere while you're controlling it (injected remote input still works).
///   • <b>Blank host screen</b> — cover every monitor with a black, click-through-proof overlay so
///     nobody standing at the host can see what you're doing.
///
/// The overlay lives on its own STA message pump because WinForms windows need one.
/// </summary>
public sealed class HostPrivacyService : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly List<Form> _overlays = new();
    private Form? _anchor;   // hidden window on the pump thread, used as the Invoke target
    private bool _inputLocked;

    public HostPrivacyService()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "PrivacyOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Pump()
    {
        _anchor = new Form();
        _anchor.CreateControl();              // realize a handle without ever showing it
        _ = _anchor.Handle;
        _ready.Set();
        Application.Run();                     // process Invoke posts + overlay message loops
    }

    public void Apply(bool lockInput, bool blankScreen)
    {
        SetInputLocked(lockInput);
        SetScreenBlank(blankScreen);
    }

    private void SetInputLocked(bool locked)
    {
        if (locked == _inputLocked) return;
        _inputLocked = locked;
        // BlockInput blocks physical input while our SendInput injection keeps working. It may fail
        // without sufficient privileges — that's acceptable; we simply don't lock in that case.
        BlockInput(locked);
    }

    private void SetScreenBlank(bool blank)
    {
        InvokeOnPump(() =>
        {
            if (blank && _overlays.Count == 0)
            {
                foreach (var screen in Screen.AllScreens)
                {
                    var form = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.Manual,
                        Bounds = screen.Bounds,
                        BackColor = Color.Black,
                        TopMost = true,
                        ShowInTaskbar = false,
                        Cursor = Cursors.Default,
                    };
                    form.Show();
                    _overlays.Add(form);
                }
            }
            else if (!blank && _overlays.Count > 0)
            {
                foreach (var f in _overlays) f.Close();
                _overlays.Clear();
            }
        });
    }

    private void InvokeOnPump(Action action)
    {
        if (_anchor is { IsHandleCreated: true }) _anchor.Invoke(action);
    }

    public void Dispose()
    {
        try
        {
            SetInputLocked(false);
            SetScreenBlank(false);
            Application.ExitThread();
        }
        catch { }
        _ready.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BlockInput(bool fBlockIt);
}
