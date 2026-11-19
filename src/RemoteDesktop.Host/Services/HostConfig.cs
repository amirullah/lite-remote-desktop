using System.Text.Json;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Host.Services;

/// <summary>Persistent host configuration. Written to %LocalAppData%\LiteRemote\host.json.</summary>
public sealed class HostConfig
{
    public int Port { get; set; } = 7443;

    /// <summary>Which auth methods this host accepts.</summary>
    public bool AllowPassword { get; set; } = true;
    public bool AllowGoogle { get; set; } = false;

    /// <summary>Argon2id hash of the access password (never the plaintext). Set via the tray UI.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Google OAuth client id this host validates id_tokens against.</summary>
    public string? GoogleClientId { get; set; }

    /// <summary>Emails permitted to connect via Google login. Empty = deny all Google logins.</summary>
    public List<string> AllowedGoogleEmails { get; set; } = new();

    /// <summary>Prefer the DXGI duplication capture backend when available.</summary>
    public bool PreferHardwareCapture { get; set; } = true;

    /// <summary>Bind only to the loopback / VPN interface instead of all interfaces.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Optional: reject clients whose source IP is outside these CIDR ranges.</summary>
    public List<string> AllowedClientCidrs { get; set; } = new();

    // ---------- ID / relay access (TeamViewer-style) ----------

    /// <summary>Relay server as "host:port". Empty = ID-based access disabled.</summary>
    public string RelayAddress { get; set; } = "";

    /// <summary>This machine's permanent 9-digit ID, generated on first run.</summary>
    public string HostId { get; set; } = "";

    /// <summary>Secret that binds <see cref="HostId"/> to this machine at the relay (anti-hijack).</summary>
    public string RelaySecret { get; set; } = "";

    /// <summary>Generate the persistent ID + secret on first use.</summary>
    public void EnsureIdentity()
    {
        if (HostId.Length == 9 && !string.IsNullOrEmpty(RelaySecret)) return;
        var digits = new char[9];
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(9);
        for (int i = 0; i < 9; i++) digits[i] = (char)('0' + bytes[i] % 10);
        if (digits[0] == '0') digits[0] = '1'; // avoid leading zero for readability
        HostId = new string(digits);
        RelaySecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        Save();
    }

    public void SetPassword(string plaintext) => PasswordHash = PasswordHasher.Hash(plaintext);
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    // ---------- persistence ----------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HostConfig Load()
    {
        AppPaths.EnsureRoot();
        try
        {
            if (File.Exists(AppPaths.HostConfig))
                return JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(AppPaths.HostConfig)) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file aside so the next Save() can't silently destroy the
            // password hash / host ID; then try the previous good version before defaults.
            var msg = $"host.json unreadable, falling back: {ex.Message}";
            System.Diagnostics.Trace.WriteLine(msg);
            RemoteDesktop.Host.Service.AgentDiag.Log(msg);
            try { File.Copy(AppPaths.HostConfig, $"{AppPaths.HostConfig}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            return TryLoadBackup() ?? new HostConfig();
        }
        return new HostConfig();
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        AppPaths.EnsureRoot();
        // Write-then-swap so a crash mid-write never leaves a truncated host.json; the previous
        // version is kept as .bak for Load() to fall back on. Lock: in-process saves can't interleave.
        var path = AppPaths.HostConfig;
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(this, Json);
        lock (SaveLock)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var w = new StreamWriter(fs))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }

    private static HostConfig? TryLoadBackup()
    {
        try
        {
            var bak = AppPaths.HostConfig + ".bak";
            return File.Exists(bak) ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(bak)) : null;
        }
        catch { return null; }
    }
}
