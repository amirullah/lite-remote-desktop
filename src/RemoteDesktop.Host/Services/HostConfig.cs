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
        catch { }
        return new HostConfig();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.HostConfig, JsonSerializer.Serialize(this, Json));
    }
}
