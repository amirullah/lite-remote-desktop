using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Host.Service;

/// <summary>
/// Lets the SYSTEM login-support daemon keep LiteRemote up to date on its own, so a PC that sits locked
/// (nobody there to approve UAC) still receives fixes. Same trust model as the viewer's in-app updater:
/// latest GitHub release of this repo, setup asset only, HTTPS from github.com, and the download must
/// match the SHA-256 digest GitHub publishes for the asset (refused when no digest is published).
/// </summary>
internal static class DaemonUpdater
{
    private const string Repo = "amirullah/lite-remote-desktop";
    private static readonly Regex SetupAsset = new(@"^LiteRemote-Setup-\d+\.\d+\.\d+\.exe$", RegexOptions.IgnoreCase);

    private static long _nextCheck = Environment.TickCount64 + 2 * 60_000;   // first check 2 min after boot

    /// <summary>Call from the daemon loop; does real work at most every 30 minutes.</summary>
    public static void Tick()
    {
        long now = Environment.TickCount64;
        if (now < _nextCheck) return;
        _nextCheck = now + 30 * 60_000;
        try { CheckAndInstallAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { AgentDiag.Log("[update] check failed: " + ex.Message); }
    }

    private static async Task CheckAndInstallAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteRemote-Daemon");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var rel = doc.RootElement;
        if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return;
        if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return;
        var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var latest)) return;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        var a = typeof(DaemonUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var current = new Version(a.Major, a.Minor, Math.Max(0, a.Build));
        if (latest <= current) return;

        foreach (var asset in rel.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!SetupAsset.IsMatch(name)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return;
            var digest = asset.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                AgentDiag.Log($"[update] {name}: no SHA-256 digest published — not installing.");
                return;
            }
            string expected = digest[7..].ToLowerInvariant();

            // SYSTEM-only folder (ProgramData\LiteRemote inherits Admins/SYSTEM write).
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LiteRemote", "update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            await using (var s = await http.GetStreamAsync(uri))
            await using (var f = File.Create(path))
                await s.CopyToAsync(f);

            string actual;
            await using (var f = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (actual != expected)
            {
                AgentDiag.Log($"[update] {name}: SHA-256 mismatch — deleted, not installing.");
                try { File.Delete(path); } catch { }
                return;
            }

            AgentDiag.Log($"[update] installing v{latest.ToString(3)} (was v{current.ToString(3)})");
            // The setup kills every LiteRemoteHost (including this daemon), installs, then restarts the
            // login task — a fresh daemon on the new version. Started detached so it survives our exit.
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DAEMONUPDATE=1")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir });
            return;
        }
    }
}
