using System.Buffers;
using System.Diagnostics;
using System.Text;
using RemoteDesktop.Media;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Headless end-to-end check for the H.264 streaming path: connects to a running host, forces
/// <see cref="VideoCodec.H264"/>, and verifies the host negotiates H.264 and the frames decode to
/// non-black images through the real <see cref="RemoteConnection"/> + <see cref="H264Decoder"/> — the
/// exact code the viewer uses. Invoked with <c>LiteRemote --selftest-h264 &lt;host&gt; &lt;port&gt; &lt;password&gt;</c>;
/// results are written to <c>%USERPROFILE%\h264-selftest.txt</c> since this is a GUI-subsystem exe.
/// </summary>
internal static class H264SelfTest
{
    public static async Task<int> RunAsync(string host, int port, string password)
    {
        var log = new StringBuilder();
        void Line(string s) { log.AppendLine(s); }

        Line($"H.264 end-to-end self-test -> {host}:{port}");
        Line(new string('-', 56));

        var pins = new PinStore(AppPaths.PinStore);
        await using var conn = new RemoteConnection(pins);
        conn.ConfirmFingerprint = (_, _) => true; // loopback test: trust on first use

        VideoCodec negotiated = VideoCodec.JpegTiles;
        int cfgW = 0, cfgH = 0;
        int frames = 0, decodedOk = 0, nonBlack = 0;
        string encoderName = "";
        H264Decoder? decoder = null;
        var decodeLock = new object();

        conn.VideoConfigured += cfg =>
        {
            negotiated = cfg.Codec; cfgW = cfg.Width; cfgH = cfg.Height;
        };
        conn.StatReceived += stat => encoderName = stat.EncoderName;
        conn.FrameReceived += (_, _, tiles, _) =>
        {
            Interlocked.Increment(ref frames);
            if (negotiated != VideoCodec.H264 || tiles.Count == 0) return;
            lock (decodeLock)
            {
                try
                {
                    decoder ??= H264Decoder.TryCreate(cfgW, cfgH, 60, out _);
                    var bgra = decoder?.Decode(tiles[0].Data.ToArray(), out _);
                    if (bgra != null) { decodedOk++; if (!IsMostlyBlack(bgra)) nonBlack++; }
                }
                catch { /* counted as not-decoded */ }
            }
        };

        var settings = new SessionSettings
        {
            FrameRateMode = FrameRateMode.Fixed,
            TargetFps = 60,
            MaxFps = 60,
            ResolutionMode = ResolutionMode.Native,
            PreferredCodec = VideoCodec.H264,   // force the path under test
            Quality = 75,
            ClipboardSync = false,
        };

        int exit = 1;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool ok = await conn.ConnectAsync(host, port, new PasswordCredential(password), settings, ct: cts.Token);
            if (!ok)
            {
                Line($"KONEKSI GAGAL: state={conn.State}");
            }
            else
            {
                Line("Terhubung. Mengumpulkan frame selama ~5 detik…");
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);

                Line($"Codec dinegosiasi : {negotiated}");
                Line($"Geometri          : {cfgW}x{cfgH}");
                Line($"Encoder (host)    : {encoderName}");
                Line($"Frame diterima    : {frames}");
                Line($"Frame ter-decode  : {decodedOk}  (non-hitam: {nonBlack})");
                Line(new string('-', 56));
                bool pass = negotiated == VideoCodec.H264 && decodedOk > 0 && nonBlack > 0;
                Line(pass
                    ? "HASIL: LULUS — H.264 end-to-end berfungsi (decode gambar valid)."
                    : "HASIL: GAGAL — periksa negosiasi/encode/decode.");
                exit = pass ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Line($"ERROR: {ex.Message}");
        }
        finally
        {
            decoder?.Dispose();
        }

        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "h264-selftest.txt");
            await System.IO.File.WriteAllTextAsync(path, log.ToString());
        }
        catch { }
        return exit;
    }

    private static bool IsMostlyBlack(byte[] bgra)
    {
        long nb = 0; int n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4 * 257)
        {
            n++;
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) nb++;
        }
        return n == 0 || nb * 100 / n < 2;
    }
}
