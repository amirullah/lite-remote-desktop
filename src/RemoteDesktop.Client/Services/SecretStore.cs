using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Per-user secret encryption via Windows DPAPI (CryptProtectData). Ciphertext is bound to the current
/// Windows user account, so even if the config file is copied elsewhere it can't be decrypted on another
/// account or machine. Used to optionally remember the VPN / RDP passwords.
/// </summary>
internal static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = h.AddrOfPinnedObject();
            if (!CryptProtectData(ref inBlob, "LiteRemote", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return null;
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(outBytes);
        }
        catch { return null; }
        finally { h.Free(); if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
    }

    public static string? Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        byte[] data;
        try { data = Convert.FromBase64String(b64); } catch { return null; }
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = h.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return null;
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        catch { return null; }
        finally { h.Free(); if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
    }
}
