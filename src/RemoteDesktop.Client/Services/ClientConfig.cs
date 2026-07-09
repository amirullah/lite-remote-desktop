using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Client.Services;

/// <summary>Client-side preferences and saved connections. Stored in %LocalAppData%\LiteRemote\client.json.</summary>
public sealed class ClientConfig
{
    public List<SavedConnection> Recent { get; set; } = new();
    public SessionSettings DefaultSettings { get; set; } = new();
    public string? GoogleClientId { get; set; }

    /// <summary>Relay server ("host:port") used for ID-based connections. Shared with the host side.</summary>
    public string RelayAddress { get; set; } = "";

    /// <summary>Last .ovpn profile used by the embedded RDP "VPN + RDP" flow, so it prefills next time.</summary>
    public string? LastVpnProfile { get; set; }

    /// <summary>Optionally-remembered passwords, DPAPI-encrypted per Windows user (see <see cref="SecretStore"/>).
    /// Keys are like "vpn:&lt;profile-path&gt;" or "rdp:&lt;host&gt;". Never stored in plaintext.</summary>
    public Dictionary<string, string> Secrets { get; set; } = new();

    public string? GetSecret(string key) => Secrets.TryGetValue(key, out var v) ? SecretStore.Unprotect(v) : null;

    public void SetSecret(string key, string? plain)
    {
        if (string.IsNullOrEmpty(plain)) { Secrets.Remove(key); return; }
        var enc = SecretStore.Protect(plain);
        if (enc != null) Secrets[key] = enc;
    }

    // ---------------- unified saved sessions (all transports) + VPN profiles ----------------
    public List<SavedSession> Sessions { get; set; } = new();
    public List<VpnProfile> VpnProfiles { get; set; } = new();

    /// <summary>Pinned first, then most-recently-used — the order the Recent column shows.</summary>
    public IEnumerable<SavedSession> Ordered =>
        Sessions.OrderByDescending(s => s.Pinned).ThenByDescending(s => s.LastUsedUtc);

    public SavedSession? GetSession(string id) => Sessions.FirstOrDefault(x => x.Id == id);

    /// <summary>Insert-or-update by identity (Kind|Host|Port or ID|RelayId); newest moves to the top.</summary>
    public void UpsertSession(SavedSession s, bool touch = true)
    {
        Sessions.RemoveAll(x => x.Id == s.Id || x.IdentityKey == s.IdentityKey);
        Sessions.Insert(0, touch ? s with { LastUsedUtc = DateTime.UtcNow } : s);
        // cap the unpinned recents so the list can't grow forever
        var drop = Sessions.Where(x => !x.Pinned).Skip(20).Select(x => x.Id).ToList();
        if (drop.Count > 0)
        {
            Sessions.RemoveAll(x => drop.Contains(x.Id));
            foreach (var id in drop) Secrets.Remove("session:" + id);
        }
        Save();
    }

    public void TouchSession(string id) { var s = GetSession(id); if (s != null) UpsertSession(s); }
    public void DeleteSession(string id) { Sessions.RemoveAll(x => x.Id == id); Secrets.Remove("session:" + id); Save(); }

    public void SetPinned(string id, bool pin)
    {
        var i = Sessions.FindIndex(x => x.Id == id);
        if (i >= 0) { Sessions[i] = Sessions[i] with { Pinned = pin }; Save(); }
    }

    public void ClearSessionPassword(string id)
    {
        Secrets.Remove("session:" + id);
        var i = Sessions.FindIndex(x => x.Id == id);
        if (i >= 0) { Sessions[i] = Sessions[i] with { SavePassword = false }; Save(); }
    }

    public VpnProfile? GetVpn(string? id) => id == null ? null : VpnProfiles.FirstOrDefault(v => v.Id == id);

    public VpnProfile UpsertVpn(VpnProfile v)
    {
        var i = VpnProfiles.FindIndex(x => x.Id == v.Id ||
            string.Equals(x.OvpnPath, v.OvpnPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) { v = v with { Id = VpnProfiles[i].Id }; VpnProfiles[i] = v; } else VpnProfiles.Add(v);
        Save();
        return v;
    }

    public void DeleteVpn(string id) { VpnProfiles.RemoveAll(x => x.Id == id); Secrets.Remove("vpn:" + id); Save(); }

    /// <summary>
    /// One-time upgrade of the pre-unified store (Recent + rdp:/vpn: path-keyed secrets) into the new
    /// SavedSession/VpnProfile model with Id-anchored secrets. Runs once (guarded), never loses passwords.
    /// </summary>
    private void MigrateLegacy()
    {
        bool hasLegacy = Recent.Count > 0 ||
                         Secrets.Keys.Any(k => k.StartsWith("rdp:") || (k.StartsWith("vpn:") && k.Contains('\\')));
        if (Sessions.Count > 0 || !hasLegacy) return;

        var vpnByPath = new Dictionary<string, VpnProfile>(StringComparer.OrdinalIgnoreCase);
        VpnProfile EnsureVpn(string path)
        {
            if (vpnByPath.TryGetValue(path, out var v)) return v;
            v = new VpnProfile { OvpnPath = path, SavePassword = Secrets.ContainsKey("vpn:" + path) };
            if (Secrets.TryGetValue("vpn:" + path, out var enc)) { Secrets["vpn:" + v.Id] = enc; Secrets.Remove("vpn:" + path); }
            VpnProfiles.Add(v); vpnByPath[path] = v;
            return v;
        }

        if (!string.IsNullOrWhiteSpace(LastVpnProfile)) EnsureVpn(LastVpnProfile!);

        foreach (var c in Recent.Where(c => !string.IsNullOrWhiteSpace(c.Host)))
        {
            string? vpnId = !string.IsNullOrWhiteSpace(c.VpnProfile) ? EnsureVpn(c.VpnProfile!).Id : null;
            Sessions.Add(new SavedSession
            {
                Kind = SessionKind.LiteRemoteIp, Host = c.Host, Port = c.Port, Label = c.Label,
                Pinned = true, UseVpn = vpnId != null, VpnProfileId = vpnId,
            });
        }

        foreach (var key in Secrets.Keys.Where(k => k.StartsWith("rdp:")).ToList())
        {
            var host = key.Substring(4);
            var combo = GetSecret(key);
            string user = "", pass = "";
            if (!string.IsNullOrEmpty(combo)) { var p = combo.Split('\n'); user = p[0]; if (p.Length > 1) pass = p[1]; }
            var s = new SavedSession { Kind = SessionKind.Rdp, Host = host, Port = 3389, Username = user, Pinned = true, SavePassword = pass.Length > 0 };
            Sessions.Add(s);
            if (pass.Length > 0) SetSecret("session:" + s.Id, pass);
            Secrets.Remove(key);
        }

        LastVpnProfile = null;
        Save();
    }

    public void Remember(SavedConnection conn)
    {
        Recent.RemoveAll(c => c.Host == conn.Host && c.Port == conn.Port);
        Recent.Insert(0, conn);
        if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
        Save();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ClientConfig Load()
    {
        AppPaths.EnsureRoot();
        ClientConfig cfg;
        try
        {
            cfg = File.Exists(AppPaths.ClientConfig)
                ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(AppPaths.ClientConfig)) ?? new()
                : new();
        }
        catch { cfg = new ClientConfig(); }
        try { cfg.MigrateLegacy(); } catch { }
        return cfg;
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.ClientConfig, JsonSerializer.Serialize(this, Json));
    }
}

public sealed record SavedConnection
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 7443;
    public string? VpnProfile { get; init; }   // path to an .ovpn profile, null = direct internet
    public string Label { get; init; } = "";
}
