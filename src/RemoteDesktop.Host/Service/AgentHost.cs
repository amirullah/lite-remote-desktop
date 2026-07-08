using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Host.Capture;
using RemoteDesktop.Host.Services;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Host.Service;

/// <summary>
/// The two headless roles that give LiteRemote login-screen support (like Avica/TeamViewer):
///   • <b>daemon</b> — runs as LocalSystem (started by a boot scheduled task). It keeps one
///     <b>agent</b> alive in the active physical console session, relaunching it when the console
///     session changes (logout / fast-user-switch).
///   • <b>agent</b> — the actual host server, launched by the daemon as SYSTEM on WinSta0. With
///     <see cref="DesktopFollow"/> enabled it re-attaches to the current input desktop each frame, so
///     it can capture the Winlogon/UAC secure desktop that a normal user-session host cannot.
/// </summary>
internal static class AgentHost
{
    public static void RunAgent()
    {
        DesktopFollow.Enabled = true;
        AppPaths.EnsureRoot();
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        var log = lf.CreateLogger("Agent");
        log.LogInformation("Agent starting as {User} in session {Session}",
            Environment.UserName, Process.GetCurrentProcess().SessionId);

        var config = HostConfig.Load();
        var server = new HostServer(config, log);
        try { _ = server.StartAsync(); }
        catch (Exception ex) { log.LogError(ex, "Agent server failed to start."); return; }

        // Live until the daemon kills us (session change) or the machine shuts down.
        new ManualResetEvent(false).WaitOne();
    }

    public static void RunDaemon()
    {
        AppPaths.EnsureRoot();
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        var log = lf.CreateLogger("Daemon");
        log.LogInformation("Daemon starting as {User}", Environment.UserName);

        int agentPid = 0;
        uint lastSession = 0xFFFFFFFF;
        while (true)
        {
            try
            {
                uint session = SessionLauncher.ActiveConsoleSession();
                bool needLaunch = session != 0xFFFFFFFF &&
                                  (session != lastSession || !SessionLauncher.IsAlive(agentPid));
                if (needLaunch)
                {
                    if (SessionLauncher.IsAlive(agentPid))
                    {
                        try { using var p = Process.GetProcessById(agentPid); p.Kill(); } catch { }
                    }
                    agentPid = SessionLauncher.LaunchInConsoleSession("--agent");
                    lastSession = session;
                    log.LogInformation("Launched agent pid {Pid} in console session {Session}", agentPid, session);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Daemon loop error; retrying."); }
            Thread.Sleep(3000);
        }
    }
}
