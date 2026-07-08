using System.Diagnostics;

namespace RemoteDesktop.Host.Service;

/// <summary>
/// Installs/uninstalls "login-screen support": a boot scheduled task that runs <c>--daemon</c> as
/// LocalSystem, which in turn keeps a SYSTEM agent on the interactive desktop. Requires elevation.
///
/// The SYSTEM agent reads config from LocalSystem's profile, so install copies the interactive user's
/// <c>host.json</c> + <c>host.pfx</c> there — that keeps the SAME access password and TLS certificate
/// (hence the same fingerprint the viewer already pinned), so nothing about connecting changes.
/// </summary>
internal static class LoginSupport
{
    private const string TaskName = "LiteRemoteLogin";

    public static void Install()
    {
        Console.WriteLine("Installing LiteRemote login-screen support…");
        CopyConfigToSystemProfile();

        string exe = Environment.ProcessPath!;
        // Boot task, LocalSystem, highest privileges — the daemon needs SYSTEM to reach the console
        // session's secure desktop.
        Run("schtasks", $"/create /tn {TaskName} /tr \"\\\"{exe}\\\" --daemon\" /sc onstart /ru SYSTEM /rl highest /f");
        Run("schtasks", $"/run /tn {TaskName}");
        Console.WriteLine("Done. The host now runs as a SYSTEM agent and can show the Windows login screen.");
        Console.WriteLine("Stop the tray host if it is running — the agent is the host now (same port).");
    }

    public static void Uninstall()
    {
        Console.WriteLine("Removing LiteRemote login-screen support…");
        Run("schtasks", $"/delete /tn {TaskName} /f");
        foreach (var n in new[] { "LiteRemoteHost" })
            foreach (var p in Process.GetProcessesByName(n))
                try { if (p.Id != Environment.ProcessId) p.Kill(); } catch { }
        Console.WriteLine("Done.");
    }

    private static void CopyConfigToSystemProfile()
    {
        // LocalSystem's LocalApplicationData.
        string sysRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\config\systemprofile\AppData\Local\LiteRemote");
        string userRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteRemote");
        try
        {
            Directory.CreateDirectory(sysRoot);
            foreach (var f in new[] { "host.json", "host.pfx" })
            {
                string src = Path.Combine(userRoot, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(sysRoot, f), overwrite: true);
            }
            Console.WriteLine($"Copied config/cert to SYSTEM profile: {sysRoot}");
        }
        catch (Exception ex) { Console.WriteLine($"WARNING: could not copy config to SYSTEM profile: {ex.Message}"); }
    }

    private static void Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(15000);
            var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (o.Length > 0) Console.WriteLine($"  {file}: {o}");
        }
        catch (Exception ex) { Console.WriteLine($"  {file} failed: {ex.Message}"); }
    }
}
