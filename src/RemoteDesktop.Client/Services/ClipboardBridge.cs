using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Two-way clipboard sync on the client. Hooks the WPF window's HWND to receive WM_CLIPBOARDUPDATE
/// (so anything the user copies locally can be pushed to the host) and applies clipboard content
/// coming from the host. Runs entirely on the UI (STA) thread, which is where WPF's clipboard lives.
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly HwndSource _source;
    private ulong _lastFingerprint;

    public event Action<ClipboardData>? ClipboardChanged;

    public ClipboardBridge(Window window)
    {
        _source = (HwndSource)PresentationSource.FromVisual(window)!;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { OnLocalClipboardChanged(); } catch { }
        }
        return IntPtr.Zero;
    }

    private void OnLocalClipboardChanged()
    {
        var data = ReadClipboard();
        if (data.Format == ClipboardFormat.Empty) return;
        var fp = ClipboardCodec.Fingerprint(data);
        if (fp == _lastFingerprint) return; // suppress our own echo
        _lastFingerprint = fp;
        ClipboardChanged?.Invoke(data);
    }

    public void SetClipboard(ClipboardData data)
    {
        _lastFingerprint = ClipboardCodec.Fingerprint(data);
        try
        {
            switch (data.Format)
            {
                case ClipboardFormat.Text:
                    Clipboard.SetText(data.AsText());
                    break;
                case ClipboardFormat.Png:
                    using (var ms = new MemoryStream(data.Bytes))
                    {
                        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        Clipboard.SetImage(decoder.Frames[0]);
                    }
                    break;
                case ClipboardFormat.FileList:
                    var coll = new System.Collections.Specialized.StringCollection();
                    coll.AddRange(data.AsFileList());
                    Clipboard.SetFileDropList(coll);
                    break;
            }
        }
        catch { /* clipboard contention is expected; ignore */ }
    }

    private static ClipboardData ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
                return ClipboardData.FromText(Clipboard.GetText());

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img is null) return ClipboardData.Empty;
                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(img));
                encoder.Save(ms);
                return new ClipboardData(ClipboardFormat.Png, ms.ToArray());
            }

            if (Clipboard.ContainsFileDropList())
                return ClipboardData.FromFileList(Clipboard.GetFileDropList().Cast<string>());
        }
        catch { }
        return ClipboardData.Empty;
    }

    public void Dispose()
    {
        try
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
