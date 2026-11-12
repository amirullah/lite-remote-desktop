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
    /// <summary>Secret of the Google "Desktop app" OAuth client. Google requires it at the token endpoint even
    /// with PKCE, and documents it as non-confidential for installed apps.</summary>
    public string? GoogleClientSecret { get; set; }

    /// <summary>UI theme preference: "system" | "dark" | "light".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>UI language preference: "id" | "en".</summary>
    public string Language { get; set; } = "id";

    /// <summary>Ask for confirmation before deleting a saved session from the Recent list.</summary>
    public bool ConfirmDelete { get; set; } = true;

    /// <summary>When a session is already fullscreen, open a NEW session fullscreen on the same monitor
    /// (joins the "workspace"); switch between them with the toolbar buttons. Off = each opens separately.</summary>
    public bool JoinActiveFullscreen { get; set; } = true;

    /// <summary>Relay server ("host:port") used for ID-based connections. Shared with the host side.</summary>
    public string RelayAddress { get; set; } = "";

    /// <summary>Last .ovpn profile used by the embedded RDP "VPN + RDP" flow, so it prefills next time.</summary>
    public string? LastVpnProfile { get; set; }

    /// <summary>Optionally-remembered passwords, DPAPI-encrypted per Windows user (see <see cref="SecretStore"/>).
    /// Keys are like "vpn:&lt;profile-path&gt;" or "rdp:&lt;host&gt;". Never stored in plaintext.</summary>
    public Dictionary<string, string> Secrets { get; set; } = new();

    /// <summary>Secret-store key for RDP account creds — host AND port, so same-host different-port targets
    /// (e.g. 10.28.76.92:13389 vs :3389) keep SEPARATE user+password and never overwrite one another.</summary>
    public static string RdpKey(string host, int port) => $"rdp:{host}:{(port == 0 ? 3389 : port)}";

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
        // A same-identity row with a DIFFERENT Id would leave its session:<id> secret orphaned (unreachable
        // by DeleteSession/ClearSessionPassword). Drop those secrets as we replace the row.
        foreach (var stale in Sessions.Where(x => x.Id != s.Id && x.IdentityKey == s.IdentityKey).ToList())
            Secrets.Remove("session:" + stale.Id);
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

    /// <summary>Wipe every saved session and VPN profile, plus their remembered passwords (session:/vpn:/rdp:).
    /// Used by Settings → "Delete all saved sessions". Irreversible.</summary>
    public void ClearAllSessions()
    {
        Sessions.Clear();
        VpnProfiles.Clear();
        Recent.Clear();
        foreach (var key in Secrets.Keys
                     .Where(k => k.StartsWith("session:") || k.StartsWith("vpn:") || k.StartsWith("rdp:"))
                     .ToList())
            Secrets.Remove(key);
        VpnProfileStore.DeleteAll();   // imported .ovpn copies go with the profiles
        Save();
    }

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
        if (i >= 0)
        {
            // A profile is SHARED by every session using that .ovpn — merge, never blank it out:
            // an empty username from one form must not erase the user another session relies on.
            var old = VpnProfiles[i];
            v = v with
            {
                Id = old.Id,
                Username = string.IsNullOrWhiteSpace(v.Username) ? old.Username : v.Username,
                SavePassword = v.SavePassword || Secrets.ContainsKey("vpn:" + v.OvpnPath),
            };
            VpnProfiles[i] = v;
        }
        else VpnProfiles.Add(v);
        Save();
        return v;
    }

    /// <summary>True if any session OTHER than <paramref name="exceptSessionId"/> uses the VPN profile at
    /// <paramref name="ovpnPath"/> — its remembered password must then survive this session's changes.</summary>
    public bool VpnUsedByOthers(string? ovpnPath, string? exceptSessionId)
    {
        if (string.IsNullOrWhiteSpace(ovpnPath)) return false;
        var ids = VpnProfiles.Where(v => string.Equals(v.OvpnPath, ovpnPath, StringComparison.OrdinalIgnoreCase))
                             .Select(v => v.Id).ToHashSet();
        return Sessions.Any(s => s.Id != exceptSessionId && s.UseVpn && s.VpnProfileId != null && ids.Contains(s.VpnProfileId));
    }

    /// <summary>Remember/forget the VPN password for a (shared) profile on behalf of one session:
    /// save → store it (an empty box keeps the existing one); don't save → forget it ONLY if no other
    /// session uses this profile. One session's choice never wipes another session's VPN password.</summary>
    public void SetVpnSecret(string ovpnPath, string? password, bool save, string? exceptSessionId)
    {
        if (string.IsNullOrWhiteSpace(ovpnPath)) return;
        if (save) { if (!string.IsNullOrEmpty(password)) SetSecret("vpn:" + ovpnPath, password); }
        else if (!VpnUsedByOthers(ovpnPath, exceptSessionId)) SetSecret("vpn:" + ovpnPath, null);
    }

    /// <summary>This session stopped using <paramref name="ovpnPath"/>: drop its password only if orphaned.</summary>
    public void ReleaseVpnSecret(string? ovpnPath, string? exceptSessionId)
    {
        if (!string.IsNullOrWhiteSpace(ovpnPath) && !VpnUsedByOthers(ovpnPath, exceptSessionId))
            SetSecret("vpn:" + ovpnPath, null);
    }

    public void DeleteVpn(string id)
    {
        // VPN passwords are keyed by the profile PATH (how RdpWindow/SessionEditWindow store them), not Id.
        var v = VpnProfiles.FirstOrDefault(x => x.Id == id);
        if (v != null) Secrets.Remove("vpn:" + v.OvpnPath);
        VpnProfiles.RemoveAll(x => x.Id == id);
        Save();
    }

    /// <summary>
    /// One-time upgrade of the pre-unified store (Recent + rdp:/vpn: path-keyed secrets) into the new
    /// SavedSession/VpnProfile model with Id-anchored secrets. Runs once (guarded), never loses passwords.
    /// </summary>
    private void MigrateLegacy()
    {
        bool hasLegacy = Recent.Count > 0 ||
                         Secrets.Keys.Any(k => IsLegacyRdpKey(k) || (k.StartsWith("vpn:") && k.Contains('\\')));
        if (Sessions.Count > 0 || !hasLegacy) return;

        // NOTE: we do NOT re-key the existing rdp:<host> / vpn:<path> secrets — RdpWindow still loads
        // passwords by those keys. Migration only creates the Recent/Saved *entries* on top of them.
        var vpnByPath = new Dictionary<string, VpnProfile>(StringComparer.OrdinalIgnoreCase);
        VpnProfile EnsureVpn(string path)
        {
            if (vpnByPath.TryGetValue(path, out var v)) return v;
            v = new VpnProfile { OvpnPath = path, SavePassword = Secrets.ContainsKey("vpn:" + path) };
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

        foreach (var key in Secrets.Keys.Where(IsLegacyRdpKey).ToList())
        {
            var host = key.Substring(4);
            var combo = GetSecret(key);
            string user = "";
            if (!string.IsNullOrEmpty(combo)) user = combo.Split('\n')[0];
            Sessions.Add(new SavedSession { Kind = SessionKind.Rdp, Host = host, Port = 3389, Username = user, Pinned = true, SavePassword = true });
        }

        Save();
    }

    /// <summary>Old "rdp:&lt;host&gt;" key. Current <see cref="RdpKey"/> keys ("rdp:&lt;host&gt;:&lt;port&gt;") are not
    /// legacy — treating them so would resurrect deleted sessions with "host:port" as the host.</summary>
    private static bool IsLegacyRdpKey(string k) =>
        k.StartsWith("rdp:") && !System.Text.RegularExpressions.Regex.IsMatch(k, @"^rdp:.+:\d+$");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static ClientConfig? _shared;

    /// <summary>
    /// Process-wide config instance shared by every window. WPF is single-threaded, so all mutations
    /// happen on the UI thread against ONE in-memory object — this is what keeps concurrent sessions
    /// from each persisting a stale full-file snapshot and clobbering one another's saved rows/secrets.
    /// </summary>
    public static ClientConfig Shared => _shared ??= Load();

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
        catch (Exception ex)
        {
            // Keep the unreadable file aside so the next Save() can't silently destroy every session/secret.
            Diag.Log($"client.json unreadable, starting fresh: {ex.Message}");
            try { File.Copy(AppPaths.ClientConfig, $"{AppPaths.ClientConfig}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            cfg = TryLoadBackup() ?? new ClientConfig();
        }
        try { cfg.MigrateLegacy(); } catch { }
        try { cfg.ImportVpnProfilesIntoStore(); } catch { }
        return cfg;
    }

    /// <summary>
    /// Move every saved VPN profile that still points at an outside file (e.g. Downloads) into
    /// <see cref="VpnProfileStore"/>, re-keying its remembered password (secrets are keyed by path).
    /// A profile whose outside file is already gone is re-pointed to a stored copy of the same name if any.
    /// </summary>
    private void ImportVpnProfilesIntoStore()
    {
        bool changed = false;
        string? Repoint(string old)
        {
            if (string.IsNullOrWhiteSpace(old) || VpnProfileStore.IsManaged(old)) return null;
            string? dest;
            if (File.Exists(old)) dest = VpnProfileStore.Import(old);
            else
            {
                dest = VpnProfileStore.FindStored(old);
                if (dest == null && VpnProfileStore.FindRedownloaded(old) is { } again)
                    dest = VpnProfileStore.Import(again);
            }
            return dest != null && !string.Equals(dest, old, StringComparison.OrdinalIgnoreCase) ? dest : null;
        }
        void MoveSecret(string from, string to)
        {
            if (Secrets.TryGetValue("vpn:" + from, out var enc))
            {
                Secrets.Remove("vpn:" + from);
                Secrets.TryAdd("vpn:" + to, enc);
            }
        }

        for (int i = 0; i < VpnProfiles.Count; i++)
        {
            var old = VpnProfiles[i].OvpnPath;
            var dest = Repoint(old);
            if (dest == null) continue;
            MoveSecret(old, dest);
            if (string.Equals(LastVpnProfile, old, StringComparison.OrdinalIgnoreCase)) LastVpnProfile = dest;
            // Two saved entries may now resolve to the same stored file — keep ids intact, just repoint.
            VpnProfiles[i] = VpnProfiles[i] with { OvpnPath = dest };
            changed = true;
        }
        if (LastVpnProfile != null && Repoint(LastVpnProfile) is { } lastDest)
        {
            MoveSecret(LastVpnProfile, lastDest);
            LastVpnProfile = lastDest; changed = true;
        }
        if (changed) Save();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        // Write-then-swap so a crash mid-write never leaves a truncated client.json; the previous
        // version is kept as .bak for Load() to fall back on.
        var path = AppPaths.ClientConfig;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    private static ClientConfig? TryLoadBackup()
    {
        try
        {
            var bak = AppPaths.ClientConfig + ".bak";
            return File.Exists(bak) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(bak)) : null;
        }
        catch { return null; }
    }
}

public sealed record SavedConnection
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 7443;
    public string? VpnProfile { get; init; }   // path to an .ovpn profile, null = direct internet
    public string Label { get; init; } = "";
}
