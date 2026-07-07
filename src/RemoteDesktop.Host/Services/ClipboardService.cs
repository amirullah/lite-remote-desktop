using System.Drawing.Imaging;
using System.Windows.Forms;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Host.Services;

/// <summary>
/// Watches the host clipboard and pushes changes to the client, and applies clipboard content
/// coming back from the client. Clipboard access is STA-only, so all reads/writes are marshalled
/// onto a dedicated STA pump thread owning a message-only window that listens for
/// WM_CLIPBOARDUPDATE. Text, images (PNG), and copied file *names* are supported.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly Thread _thread;
    private ClipboardWindow? _window;
    private readonly ManualResetEventSlim _ready = new(false);
    private ulong _lastFingerprint;

    /// <summary>Raised (on the pump thread) when the local clipboard changes.</summary>
    public event Action<ClipboardData>? ClipboardChanged;

    public ClipboardService()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "ClipboardPump" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Pump()
    {
        _window = new ClipboardWindow(OnClipboardUpdate);
        _ready.Set();
        Application.Run();
    }

    private void OnClipboardUpdate()
    {
        var data = ReadClipboard();
        if (data.Format == ClipboardFormat.Empty) return;

        var fp = ClipboardCodec.Fingerprint(data);
        if (fp == _lastFingerprint) return; // our own echo
        _lastFingerprint = fp;
        ClipboardChanged?.Invoke(data);
    }

    /// <summary>Apply clipboard content received from the client.</summary>
    public void SetClipboard(ClipboardData data)
    {
        _lastFingerprint = ClipboardCodec.Fingerprint(data); // suppress the echo we're about to cause
        _window?.Invoke(() =>
        {
            try
            {
                switch (data.Format)
                {
                    case ClipboardFormat.Text:
                        Clipboard.SetText(data.AsText());
                        break;
                    case ClipboardFormat.Png:
                        using (var ms = new MemoryStream(data.Bytes))
                            Clipboard.SetImage(System.Drawing.Image.FromStream(ms));
                        break;
                    case ClipboardFormat.FileList:
                        var coll = new System.Collections.Specialized.StringCollection();
                        coll.AddRange(data.AsFileList());
                        Clipboard.SetFileDropList(coll);
                        break;
                }
            }
            catch { /* clipboard is a shared resource; races are expected and benign */ }
        });
    }

    private ClipboardData ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
                return ClipboardData.FromText(Clipboard.GetText());

            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img is null) return ClipboardData.Empty;
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                return new ClipboardData(ClipboardFormat.Png, ms.ToArray());
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>();
                return ClipboardData.FromFileList(files);
            }
        }
        catch { }
        return ClipboardData.Empty;
    }

    public void Dispose()
    {
        _window?.Invoke(Application.ExitThread);
        _ready.Dispose();
    }

    /// <summary>Hidden message-only window that receives clipboard-change notifications.</summary>
    private sealed class ClipboardWindow : Form
    {
        private readonly Action _onUpdate;
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        public ClipboardWindow(Action onUpdate)
        {
            _onUpdate = onUpdate;
            // Never show; just need a valid HWND on the STA pump. Touching Handle realizes it.
            _ = Handle;
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try { _onUpdate(); } catch { }
            }
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void Dispose(bool disposing)
        {
            if (disposing && IsHandleCreated) RemoveClipboardFormatListener(Handle);
            base.Dispose(disposing);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}
