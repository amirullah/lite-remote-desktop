using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Maui.Services;

/// <summary>
/// Validates and sanitizes a campus .ovpn profile (port of the Windows client's VpnProfileStore rules):
/// everything must be inline (no external cert/key files), only "dev tun" works on Android, and
/// directives that run scripts or touch the management interface are removed.
/// </summary>
public static class OvpnProfile
{
    public const int MaxBytes = 256 * 1024;

    // Directives that point at a SEPARATE file (inline <ca>…</ca> blocks start with '<' and don't match).
    private static readonly Regex ExternalRef = new(
        @"^\s*(ca|cert|key|pkcs12|tls-auth|tls-crypt|tls-crypt-v2|secret|dh|crl-verify|extra-certs|askpass)[ \t]+[^\s#;]",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Lines dropped entirely: scripts/plugins, our own management/logging, Windows-only and full-tunnel bits.
    private static readonly HashSet<string> Strip = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "script-security", "plugin", "route-up", "route-pre-down", "ipchange", "tls-verify",
        "learn-address", "client-connect", "client-disconnect", "auth-user-pass-verify", "down-pre", "up-delay",
        "management", "management-client", "management-hold", "management-query-passwords",
        "management-query-proxy", "management-query-remote", "management-signal", "management-forget-disconnect",
        "management-up-down", "management-client-auth", "management-external-key", "management-external-cert",
        "machine-readable-output", "log", "log-append", "status", "writepid", "daemon", "cd", "chroot",
        "user", "group", "iproute", "dev-node", "redirect-gateway", "redirect-private", "block-outside-dns",
        "register-dns", "route-method", "win-sys", "ip-win32", "service", "tap-sleep", "show-net-up",
        "dhcp-renew", "dhcp-release", "pause-exit", "auth-retry", "connect-retry-max",
    };

    /// <summary>Returns an Indonesian error, or null when the profile is usable. <paramref name="clean"/>
    /// is the sanitized text to store.</summary>
    public static string? Validate(string? text, out string clean)
    {
        clean = "";
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) return "File .ovpn kosong.";
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return "File .ovpn terlalu besar (maks 256 KB).";
        if (!Regex.IsMatch(text, @"^\s*remote\s+\S+", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            return "File .ovpn tidak berisi baris 'remote' (alamat server VPN).";
        if (Regex.IsMatch(text, @"^\s*dev\s+tap", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            return "Profil memakai 'dev tap' yang tidak didukung Android (hanya 'dev tun').";
        if (ExternalRef.IsMatch(text))
            return "Profil merujuk file sertifikat/kunci terpisah. Minta file .ovpn dengan sertifikat tertanam (<ca>…</ca>).";
        if (!text.Contains("<ca>", StringComparison.OrdinalIgnoreCase))
            return "Sertifikat CA tidak tertanam di file .ovpn (blok <ca>…</ca> wajib ada).";

        var sb = new StringBuilder();
        bool inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            var t = line.Trim();
            if (inBlock)
            {
                sb.Append(line).Append('\n');
                if (t.StartsWith("</", StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            if (t.StartsWith('<') && !t.StartsWith("</", StringComparison.Ordinal))
            {
                inBlock = !t.Contains("</", StringComparison.Ordinal);
                sb.Append(line).Append('\n');
                continue;
            }
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') { sb.Append(line).Append('\n'); continue; }
            var word = t.Split(new[] { ' ', '\t' }, 2)[0].TrimStart('-');
            if (Strip.Contains(word)) { sb.Append("# [LiteRemote] dibuang: ").Append(word).Append('\n'); continue; }
            if (word.Equals("auth-user-pass", StringComparison.OrdinalIgnoreCase)) { sb.Append("auth-user-pass\n"); continue; }
            sb.Append(line).Append('\n');
        }
        clean = sb.ToString();
        return null;
    }

    public static bool NeedsUserPass(string profile) =>
        Regex.IsMatch(profile, @"^\s*auth-user-pass\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
