using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Host.Service;

/// <summary>
/// Launches a child process as LocalSystem inside the active physical console session, on WinSta0.
/// This is how a session-0 service/daemon puts an agent onto the interactive desktop so it can see and
/// drive the login/lock/UAC secure desktops (the same trick TeamViewer/Avica use). We duplicate the
/// token of <c>winlogon.exe</c> in the console session — it is LocalSystem and already bound to that
/// session — and CreateProcessAsUser with it.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>Launch <c>LiteRemoteHost &lt;args&gt;</c> as SYSTEM in the current console session. Returns the PID or 0.</summary>
    public static int LaunchInConsoleSession(string args)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return 0; // no console session attached

        IntPtr winlogonToken = GetWinlogonToken(session);
        if (winlogonToken == IntPtr.Zero) return 0;

        IntPtr dupToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(winlogonToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out dupToken))
                return 0;

            CreateEnvironmentBlock(out env, dupToken, false);

            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\Default" };
            string cmd = $"\"{exe}\" {args}";

            bool ok = CreateProcessAsUser(dupToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, null, ref si, out var pi);
            if (!ok) return 0;

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return (int)pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            CloseHandle(winlogonToken);
        }
    }

    /// <summary>The physical console session id, or 0xFFFFFFFF when none is attached.</summary>
    public static uint ActiveConsoleSession() => WTSGetActiveConsoleSessionId();

    /// <summary>Is a process with this PID still alive?</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static IntPtr GetWinlogonToken(uint session)
    {
        foreach (var p in Process.GetProcessesByName("winlogon"))
        {
            try
            {
                if ((uint)p.SessionId != session) continue;
                IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, p.Id);
                if (hProc == IntPtr.Zero) continue;
                try
                {
                    if (OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out IntPtr tok))
                        return tok;
                }
                finally { CloseHandle(hProc); }
            }
            catch { /* try next */ }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    // ---- interop ----
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ASSIGN_PRIMARY = 0x0001,
        TOKEN_ALL_ACCESS = 0xF01FF, PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr hProc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, string? curDir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);
}
