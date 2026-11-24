using System.Text.Json;
using RemoteDesktop.Shared.Relay;

namespace RemoteDesktop.Maui.Services;

/// <summary>One saved connection. Never contains the password (that lives in SecureStorage "pw-&lt;Id&gt;").</summary>
public sealed class SavedSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "addr";          // "addr" | "id"
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7443;
    public string PartnerId { get; set; } = "";
    public string Relay { get; set; } = "";
    public string Quality { get; set; } = "balanced";
    public bool RememberPassword { get; set; }
    public DateTime LastUsed { get; set; }

    /// <summary>Optional campus VPN: sanitized .ovpn text (inline certs) and its username. The VPN
    /// password lives in SecureStorage "vpnpw-&lt;Id&gt;".</summary>
    public string? OvpnProfile { get; set; }
    public string? OvpnName { get; set; }
    public string VpnUser { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVpn => !string.IsNullOrWhiteSpace(OvpnProfile);

    public bool IsIdMode => Mode == "id";

    /// <summary>Connection target (host:port or formatted ID).</summary>
    public string Target => IsIdMode ? RelayProtocol.FormatId(RelayProtocol.NormalizeId(PartnerId ?? "")) : $"{Host}:{Port}";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Target : Name.Trim();

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (IsIdMode)
            {
                if (!string.IsNullOrWhiteSpace(Name)) parts.Add("ID " + Target);
                parts.Add(string.IsNullOrWhiteSpace(Relay) ? "via relay" : "relay " + Relay);
            }
            else parts.Add(string.IsNullOrWhiteSpace(Name) ? "Alamat IP" : Target);
            parts.Add(QualityProfiles.Label(Quality));
            if (RememberPassword) parts.Add("password tersimpan");
            if (HasVpn) parts.Add("lewat VPN kampus");
            return string.Join(" · ", parts);
        }
    }

    public string Badge => IsIdMode ? "ID" : "IP";
}

/// <summary>Saved sessions: JSON list in Preferences, passwords per session in SecureStorage ("pw-&lt;id&gt;").</summary>
public static class SessionStore
{
    private const string KSessions = "sessions-v1";
    private static string SecretKey(string id) => "pw-" + id;
    private static string VpnSecretKey(string id) => "vpnpw-" + id;

    public static List<SavedSession> Load()
    {
        try
        {
            var json = Preferences.Get(KSessions, "");
            if (json.Length == 0) return new();
            var list = JsonSerializer.Deserialize<List<SavedSession>>(json) ?? new();
            return list.OrderByDescending(s => s.LastUsed).ToList();
        }
        catch { return new(); }
    }

    private static void SaveAll(List<SavedSession> list) =>
        Preferences.Set(KSessions, JsonSerializer.Serialize(list));

    /// <summary>Insert or replace by Id only — never touches other sessions.</summary>
    public static void Upsert(SavedSession s)
    {
        var list = Load();
        int i = list.FindIndex(x => x.Id == s.Id);
        if (i >= 0) list[i] = s; else list.Add(s);
        SaveAll(list);
    }

    public static void Delete(string id)
    {
        var list = Load();
        list.RemoveAll(x => x.Id == id);
        SaveAll(list);
        try { SecureStorage.Default.Remove(SecretKey(id)); } catch { }
        try { SecureStorage.Default.Remove(VpnSecretKey(id)); } catch { }
    }

    public static void Touch(string id)
    {
        var list = Load();
        var s = list.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        s.LastUsed = DateTime.UtcNow;
        SaveAll(list);
    }

    public static async Task<string?> GetPasswordAsync(string id)
    {
        try { return await SecureStorage.Default.GetAsync(SecretKey(id)); } catch { return null; }
    }

    public static async Task SetPasswordAsync(string id, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password)) SecureStorage.Default.Remove(SecretKey(id));
            else await SecureStorage.Default.SetAsync(SecretKey(id), password);
        }
        catch { }   // broken keystore: just don't remember
    }

    public static async Task<string?> GetVpnPasswordAsync(string id)
    {
        try { return await SecureStorage.Default.GetAsync(VpnSecretKey(id)); } catch { return null; }
    }

    public static async Task SetVpnPasswordAsync(string id, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password)) SecureStorage.Default.Remove(VpnSecretKey(id));
            else await SecureStorage.Default.SetAsync(VpnSecretKey(id), password);
        }
        catch { }
    }

    /// <summary>For a session with a campus VPN: the step SessionPage runs before every (re)connect —
    /// brings the tunnel up (no-op when already up). null when the session has no VPN.</summary>
    public static Func<Action<string>, CancellationToken, Task>? BuildPrepare(SavedSession s)
    {
        if (!s.HasVpn) return null;
        var profile = s.OvpnProfile!;
        var user = s.VpnUser ?? "";
        var id = s.Id;
        var target = s.IsIdMode ? "" : (s.Host ?? "").Trim();
        return async (status, ct) =>
        {
#if ANDROID
            if (RemoteDesktop.Maui.Vpn.CampusVpn.IsUp) { await RemoteDesktop.Maui.Vpn.CampusVpn.ConnectAsync(profile, user, await GetVpnPasswordAsync(id) ?? "", target, ct); return; }
            status("Menyalakan VPN kampus…");
            await RemoteDesktop.Maui.Vpn.CampusVpn.ConnectAsync(profile, user, await GetVpnPasswordAsync(id) ?? "", target, ct);
            status("VPN kampus tersambung ✓ — menghubungkan ke host…");
#else
            await Task.CompletedTask;
            throw new PlatformNotSupportedException("VPN kampus hanya tersedia di Android.");
#endif
        };
    }

    public static Action? BuildOnClosed(SavedSession s)
    {
#if ANDROID
        if (s.HasVpn) return () => _ = RemoteDesktop.Maui.Vpn.CampusVpn.DisconnectAsync();
#endif
        return null;
    }

    /// <summary>One-time move of the old single-form settings (before 1.4.52) into a saved session.</summary>
    public static async Task MigrateLegacyAsync()
    {
        if (Preferences.ContainsKey(KSessions)) return;
        var host = Preferences.Get("host", "").Trim();
        var id = Preferences.Get("id", "").Trim();
        var mode = Preferences.Get("mode", "addr") == "id" ? "id" : "addr";
        var list = new List<SavedSession>();
        if ((mode == "addr" && host.Length > 0) || (mode == "id" && id.Length > 0))
        {
            int.TryParse(Preferences.Get("port", "7443"), out var port);
            var s = new SavedSession
            {
                Mode = mode,
                Host = host,
                Port = port is >= 1 and <= 65535 ? port : 7443,
                PartnerId = RelayProtocol.NormalizeId(id),
                Relay = Preferences.Get("relay", "").Trim(),
                Quality = QualityProfiles.Normalize(Preferences.Get("quality", "balanced")),
                RememberPassword = Preferences.Get("remember", false),
                LastUsed = DateTime.UtcNow,
            };
            if (s.RememberPassword)
            {
                string? pw = null;
                try { pw = await SecureStorage.Default.GetAsync("host-password"); } catch { }
                if (!string.IsNullOrEmpty(pw)) await SetPasswordAsync(s.Id, pw);
                else s.RememberPassword = false;
            }
            list.Add(s);
        }
        SaveAll(list);
        try { SecureStorage.Default.Remove("host-password"); } catch { }
    }

    /// <summary>Validates a session and builds the connect delegate. Returns an Indonesian error, or null when OK.</summary>
    public static string? TryBuildConnect(SavedSession s, string password,
        out Func<CancellationToken, Task<ConnectOutcome>>? connect, out string title)
    {
        connect = null; title = "";
        if (s.IsIdMode)
        {
            var id = RelayProtocol.NormalizeId(s.PartnerId ?? "");
            if (id.Length != 9) return "ID harus 9 digit.";
            var (relayHost, relayPort) = SplitHostPort(s.Relay, RelayProtocol.DefaultPort);
            if (relayHost.Length == 0) return "Isi alamat server relay.";
            if (password.Length == 0) return "Isi password akses host.";
            connect = ct => ViewerConnection.ConnectViaRelayAsync(relayHost, relayPort, id, password, ct);
            title = RelayProtocol.FormatId(id);
        }
        else
        {
            var host = (s.Host ?? "").Trim();
            if (host.Length == 0) return "Isi alamat host.";
            if (password.Length == 0) return "Isi password akses host.";
            var port = s.Port is >= 1 and <= 65535 ? s.Port : 7443;
            connect = ct => ViewerConnection.ConnectAsync(host, port, password, ct);
            title = $"{host}:{port}";
        }
        if (!string.IsNullOrWhiteSpace(s.Name)) title = s.Name.Trim();
        return null;
    }

    public static (string host, int port) SplitHostPort(string? value, int defaultPort)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return ("", defaultPort);
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            return (text[..colon], p);
        return (text, defaultPort);
    }
}

/// <summary>One-time heads-up before the first campus-VPN connect.</summary>
public static class VpnNotice
{
    private const string Key = "vpn-one-at-a-time-warned";

    public static async Task WarnOnceAsync(Page page, SavedSession s)
    {
        if (!s.HasVpn || Preferences.Get(Key, false)) return;
        Preferences.Set(Key, true);
        await page.DisplayAlert("VPN kampus",
            "Android hanya mengizinkan satu VPN aktif. Saat VPN kampus LiteRemote menyala, VPN lain " +
            "(mis. Tailscale) akan dijeda dan perlu dinyalakan lagi setelah sesi selesai.\n\n" +
            "Hanya LiteRemote yang lewat VPN kampus; aplikasi lain tetap memakai internet biasa.", "Mengerti");
    }
}
