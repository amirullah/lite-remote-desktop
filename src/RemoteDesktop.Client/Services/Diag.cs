using System;
using System.IO;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Tiny always-on file logger for diagnosing issues that happen in the GUI where a dialog/pop-up may
/// itself be unavailable. Writes to %LOCALAPPDATA%\LiteRemote\popup-debug.log and never throws.
/// </summary>
internal static class Diag
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiteRemote", "popup-debug.log");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break the app */ }
    }
}
