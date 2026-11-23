using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Maui.Services;

/// <summary>A newer published release that has an Android APK.</summary>
public sealed record AppUpdate(Version Version, string Tag, string FileName, string Url, long Size, string? Sha256);

/// <summary>
/// In-app updater for the Android viewer: asks the public GitHub Releases API for the latest release,
/// downloads LiteRemote-Viewer-Android-&lt;ver&gt;.apk, verifies size + SHA-256 (GitHub's asset digest),
/// then hands it to the Android package installer. No user data is sent — just a generic User-Agent.
/// </summary>
public static class AppUpdateService
{
    private const string Repo = "amirullah/lite-remote-desktop";
    private static readonly Regex ApkAsset = new(@"^LiteRemote-Viewer-Android-[\d.]+\.apk$", RegexOptions.IgnoreCase);
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LiteRemote-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Installed app version (ApplicationDisplayVersion, e.g. 1.4.50).</summary>
    public static Version CurrentVersion => ParseTag(AppInfo.Current.VersionString) ?? new Version(0, 0, 0);

    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Newer release with an APK, or null if up to date. Throws on network/API errors.</summary>
    public static async Task<AppUpdate?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cts.Token).ConfigureAwait(false);
        return Parse(doc.RootElement, CurrentVersion);
    }

    public static AppUpdate? Parse(JsonElement rel, Version current)
    {
        if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var ver = ParseTag(tag);
        if (ver is null || ver <= current || !rel.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !ApkAsset.IsMatch(name)) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                continue;
            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
            string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } dig &&
                          dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dig[7..].ToLowerInvariant() : null;
            return new AppUpdate(ver, tag!, name, url, size, sha);
        }
        return null;
    }

    /// <summary>Download into the app cache (updates/) and verify. Returns the local file path.</summary>
    public static async Task<string> DownloadAsync(AppUpdate u, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) { try { File.Delete(old); } catch { } }   // keep the cache small
        var path = Path.Combine(dir, u.FileName);
        var part = path + ".part";
        try
        {
            HttpResponseMessage resp;
            using (var hdr = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hdr.CancelAfter(TimeSpan.FromSeconds(30));
                resp = await Http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, hdr.Token).ConfigureAwait(false);
            }
            using var _r = resp;
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            using var sha = SHA256.Create();
            long got = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));   // a stalled download must not hang forever
                    int n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    got += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)got / total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            if (u.Size > 0 && got != u.Size) throw new InvalidDataException($"Ukuran unduhan tidak cocok ({got} ≠ {u.Size}).");
            if (u.Sha256 is not null && Convert.ToHexString(sha.Hash!).ToLowerInvariant() != u.Sha256)
                throw new InvalidDataException("Checksum SHA-256 tidak cocok — unduhan rusak atau dimodifikasi.");
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Open the system installer for the downloaded APK. Returns false (and opens the right Settings
    /// page) if the user still has to allow "install unknown apps" for LiteRemote.
    /// </summary>
    public static bool Install(string apkPath)
    {
#if ANDROID
        var ctx = Platform.CurrentActivity ?? (global::Android.Content.Context)global::Android.App.Application.Context;
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O &&
            !ctx.PackageManager!.CanRequestPackageInstalls())
        {
            var settings = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + ctx.PackageName));
            settings.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(settings);
            return false;
        }
        var file = new Java.IO.File(apkPath);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, ctx.PackageName + ".fileprovider", file);
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission | global::Android.Content.ActivityFlags.NewTask);
        ctx.StartActivity(intent);
        return true;
#else
        return false;
#endif
    }
}
