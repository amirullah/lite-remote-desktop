using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteDesktop.Client.Services;

/// <summary>A newer published release and its Windows setup asset.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string SetupName, string DownloadUrl, long Size, string? Sha256, string ReleaseUrl);

/// <summary>
/// In-app updater. Asks the public GitHub Releases API for the latest release, downloads its
/// LiteRemote-Setup-&lt;ver&gt;.exe, verifies size + SHA-256 (GitHub publishes an asset digest), then runs
/// the installer silently and lets the app exit so files can be replaced. The request carries no user
/// data — just a generic User-Agent, which the GitHub API requires.
/// </summary>
public static class UpdateService
{
    private const string Repo = "amirullah/lite-remote-desktop";
    private static readonly Regex SetupAsset = new(@"^LiteRemote-Setup-[\d.]+\.exe$", RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-call timeouts via tokens
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LiteRemote-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Version of the running app (Major.Minor.Build — the release tag format).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Parse "v1.4.46" / "1.4.46" → Version, or null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Latest release if it is NEWER than <paramref name="current"/> and has a Windows setup; else null.
    /// Throws on network/API errors so callers can tell "up to date" from "couldn't check".</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cts.Token).ConfigureAwait(false);
        return ParseRelease(doc.RootElement, current);
    }

    /// <summary>Pure parsing step (unit-testable without the network).</summary>
    public static UpdateInfo? ParseRelease(JsonElement rel, Version current)
    {
        if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = rel.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
        var ver = ParseTag(tag);
        if (ver is null || ver <= current) return null;
        if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !SetupAsset.IsMatch(name)) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            // Only ever download from GitHub over HTTPS.
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                continue;
            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } dig &&
                dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = dig[7..].ToLowerInvariant();
            var page = rel.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(ver, tag!, name, url, size, sha, page);
        }
        return null;
    }

    /// <summary>Download the setup to %TEMP%\LiteRemote-Update and verify it. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LiteRemote-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.SetupName);
        var part = path + ".part";
        try
        {
            // The connect + response-header phase gets its own deadline (the HttpClient has no global
            // timeout so a big download isn't cut off) — a server that never answers must not hang the UI.
            HttpResponseMessage resp;
            using (var hdr = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hdr.CancelAfter(TimeSpan.FromSeconds(30));
                resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, hdr.Token).ConfigureAwait(false);
            }
            using var _resp = resp;
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            using var sha = SHA256.Create();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                long got = 0; int n;
                while (true)
                {
                    // A stalled connection must not hang forever: 60 s without a single byte aborts.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    got += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)got / total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (info.Size > 0 && got != info.Size)
                    throw new InvalidDataException($"Ukuran unduhan tidak cocok ({got} ≠ {info.Size}).");
                if (info.Sha256 is not null)
                {
                    var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                    if (hex != info.Sha256) throw new InvalidDataException("Checksum SHA-256 tidak cocok — unduhan rusak atau dimodifikasi.");
                }
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>True if a LiteRemote Host is running in THIS user's session (it gets closed by the installer).</summary>
    public static bool HostRunningForUser()
    {
        try
        {
            int sid = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName("LiteRemoteHost").Any(p => { try { return p.SessionId == sid; } catch { return false; } });
        }
        catch { return false; }
    }

    /// <summary>Run the installer silently (UAC prompt once). Returns false if the user declined elevation.</summary>
    public static bool LaunchInstaller(string setupPath, bool restartHost)
    {
        var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /UPDATE=1" + (restartHost ? " /RESTARTHOST=1" : "");
        try
        {
            // NO Verb="runas": the setup (PrivilegesRequired=admin) elevates ITSELF, so Inno remembers the
            // non-elevated signed-in user as the "original user" and relaunches the viewer/host as that
            // user. Starting it pre-elevated made those relaunches run elevated — or, with over-the-shoulder
            // UAC, under the admin's profile with the wrong config.
            Process.Start(new ProcessStartInfo(setupPath, args) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
    }
}
