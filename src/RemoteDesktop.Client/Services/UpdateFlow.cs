using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// The shared "update now" UI flow (hub banner + Settings): confirm → download with progress → verify →
/// run the installer silently → exit so files can be replaced. The installer relaunches the viewer (and the
/// host, if it was running) when it finishes.
/// </summary>
public static class UpdateFlow
{
    private static int _running;

    /// <param name="status">Shows progress/errors to the user (called on the UI thread).</param>
    /// <returns>true if the installer was started and the app is shutting down.</returns>
    public static async Task<bool> RunAsync(Window? owner, UpdateInfo info, Action<string> status)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;   // double-click / banner + settings at once
        try
        {
            var ask = Loc.F("Update.Confirm", info.Version.ToString(3));
            var ok = owner != null
                ? MessageBox.Show(owner, ask, "LiteRemote", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(ask, "LiteRemote", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return false;

            var progress = new Progress<double>(p => status(Loc.F("Update.Downloading", (int)(p * 100))));
            status(Loc.F("Update.Downloading", 0));
            string path;
            try { path = await UpdateService.DownloadAsync(info, progress, CancellationToken.None); }
            catch (Exception ex)
            {
                Diag.Log("[update] download failed: " + ex);
                status(Loc.F("Update.DownloadFailed", ex.Message));
                return false;
            }

            status(Loc.T("Update.Starting"));
            bool restartHost = UpdateService.HostRunningForUser();
            using var setup = UpdateService.LaunchInstaller(path, restartHost);
            if (setup is null)
            {
                status(Loc.T("Update.Cancelled"));
                return false;
            }
            // Do NOT exit ourselves: the setup elevates itself, so we can't see the UAC answer directly.
            // If the user approves, the installer closes LiteRemote (PrepareToInstall) before replacing
            // files and reopens it when done. If the setup process ends while we're still running, the
            // install never happened (UAC declined / cancelled / failed) — keep the user's sessions alive.
            status(Loc.T("Update.WaitingInstaller"));
            await setup.WaitForExitAsync();
            int code = -1; try { code = setup.ExitCode; } catch { }
            Diag.Log("[update] setup exited while app still running, code " + code);
            status(Loc.T("Update.Cancelled"));
            return false;
        }
        catch (Exception ex)
        {
            Diag.Log("[update] failed: " + ex);
            status(Loc.F("Update.DownloadFailed", ex.Message));
            return false;
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
