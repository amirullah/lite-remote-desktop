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

    /// <summary>
    /// True when login-screen support is installed (the SYSTEM daemon owns the port and serves clients).
    /// The interactive tray host uses this to step aside instead of fighting over port 7443.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn {TaskName}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void Install(string? userRoot = null)
    {
        Console.WriteLine("Installing LiteRemote login-screen support…");
        CopyConfigToSystemProfile(userRoot);

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
        // Stop only the SYSTEM daemon/agent — the user's tray (which may have asked for this) keeps running
        // and takes the port back.
        Run("taskkill", "/F /FI \"IMAGENAME eq LiteRemoteHost.exe\" /FI \"USERNAME eq NT AUTHORITY\\SYSTEM\"");
        Console.WriteLine("Done.");
    }

    /// <summary>
    /// Push the user's current password/relay/cert to the SYSTEM agent and restart it so the change
    /// takes effect (the agent reads its own copy). Requires elevation, like Install.
    /// </summary>
    public static void SyncConfig(string? userRoot = null)
    {
        CopyConfigToSystemProfile(userRoot);
        Run("schtasks", $"/end /tn {TaskName}");
        // The daemon (session 0) and its agent (console session) both run as SYSTEM; the user's tray and
        // this elevated helper do not, so filtering by user stops exactly the old SYSTEM copies.
        Run("taskkill", "/F /FI \"IMAGENAME eq LiteRemoteHost.exe\" /FI \"USERNAME eq NT AUTHORITY\\SYSTEM\"");
        Thread.Sleep(700);
        Run("schtasks", $"/run /tn {TaskName}");
    }

    /// <param name="userRoot">The interactive user's LiteRemote config folder. Passed explicitly by the
    /// tray/installer: an elevated process may run under a different (admin) account whose
    /// LocalApplicationData is not the one holding the user's password and certificate.</param>
    private static void CopyConfigToSystemProfile(string? userRoot)
    {
        // LocalSystem's LocalApplicationData.
        string sysRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\config\systemprofile\AppData\Local\LiteRemote");
        userRoot = string.IsNullOrWhiteSpace(userRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteRemote")
            : userRoot.Trim().TrimEnd('\\');
        try
        {
            Directory.CreateDirectory(sysRoot);
            string json = Path.Combine(userRoot, "host.json");
            if (File.Exists(json)) File.Copy(json, Path.Combine(sysRoot, "host.json"), overwrite: true);

            // host.pfx is DPAPI-protected for the USER; SYSTEM can't decrypt that and would silently mint a
            // NEW certificate — every pinned viewer then refuses the host as "key changed". Hand SYSTEM the
            // decrypted PFX instead: systemprofile is SYSTEM/Administrators-only, and on first load the
            // agent re-protects it with its own DPAPI key (CertificateManager's legacy-plaintext upgrade).
            string pfx = Path.Combine(userRoot, "host.pfx");
            if (File.Exists(pfx))
            {
                var raw = File.ReadAllBytes(pfx);
                byte[] plain;
                try
                {
                    plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        raw, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                }
                catch { plain = raw; }   // already plaintext (legacy) or another account's file: copy as-is
                File.WriteAllBytes(Path.Combine(sysRoot, "host.pfx"), plain);
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
