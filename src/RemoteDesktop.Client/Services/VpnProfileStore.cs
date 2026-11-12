using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Imports .ovpn profiles into LiteRemote's own data folder (%LocalAppData%\LiteRemote\vpn), the way
/// OpenVPN Connect does — so a saved session keeps working after the original file in Downloads is
/// deleted, moved or re-downloaded as "name (1).ovpn".
/// </summary>
public static class VpnProfileStore
{
    public static string Dir => Path.Combine(Path.GetDirectoryName(AppPaths.ClientConfig)!, "vpn");

    // Directives that point at a SEPARATE file (inline <ca>…</ca> blocks start with '<' and don't match).
    // A profile using these depends on files next to it, so we leave it where it is instead of copying.
    private static readonly Regex ExternalRef = new(
        @"^\s*(ca|cert|key|pkcs12|tls-auth|tls-crypt|tls-crypt-v2|secret|dh|crl-verify|extra-certs|auth-user-pass|askpass)[ \t]+[^\s#;]",   // [ \t], not \s: a bare "auth-user-pass" must not match the next line
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex CopySuffix = new(@"\s*\(\d+\)$");

    public static bool IsManaged(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(Dir).TrimEnd('\\') + "\\";
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Copy <paramref name="source"/> into the store and return the stored path. Returns the
    /// source unchanged if it is already managed, references external files, or anything fails.</summary>
    public static string Import(string source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || IsManaged(source)) return source;
            var text = File.ReadAllText(source);
            if (ExternalRef.IsMatch(text)) return source;

            Directory.CreateDirectory(Dir);
            var name = CopySuffix.Replace(Path.GetFileNameWithoutExtension(source), "");
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "profile";

            var dest = Path.Combine(Dir, name + ".ovpn");
            // A different profile already uses this name → keep both (short content hash suffix).
            if (File.Exists(dest) && File.ReadAllText(dest) != text)
            {
                var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
                dest = Path.Combine(Dir, $"{name}-{h}.ovpn");
            }
            File.WriteAllText(dest, text);
            return dest;
        }
        catch (Exception ex)
        {
            Diag.Log("[vpn] profile import failed: " + ex.GetType().Name);
            return source;
        }
    }

    /// <summary>If <paramref name="path"/> is gone, look for a stored copy with the same profile name.</summary>
    public static string? FindStored(string path)
    {
        try
        {
            var name = CopySuffix.Replace(Path.GetFileNameWithoutExtension(path), "");
            var p = Path.Combine(Dir, name + ".ovpn");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>If <paramref name="path"/> is gone, find a re-downloaded copy next to it ("name (1).ovpn"),
    /// newest first — the usual reason a Downloads profile "disappears".</summary>
    public static string? FindRedownloaded(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir == null || !Directory.Exists(dir)) return null;
            var name = CopySuffix.Replace(Path.GetFileNameWithoutExtension(path), "");
            string? best = null; DateTime bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.ovpn"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (!string.Equals(CopySuffix.Replace(n, ""), name, StringComparison.OrdinalIgnoreCase)) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { best = f; bestTime = t; }
            }
            return best;
        }
        catch { return null; }
    }

    public static void DeleteAll()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }
}
