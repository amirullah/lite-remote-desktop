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
    public static async Task<int> RunAsync(string host, int port, string password, string codec = "auto", string res = "")
    {
        var wantCodec = codec.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
            ? VideoCodec.JpegTiles : VideoCodec.H264;
        int scaledW = 0, scaledH = 0;
        if (res.Contains('x'))
        {
            var parts = res.Split('x');
            int.TryParse(parts[0], out scaledW);
            int.TryParse(parts[1], out scaledH);
        }
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
        SessionStat? lastStat = null;
        H264Decoder? decoder = null;
        var decodeLock = new object();

        // Client-side arrival timing — this is what "lambat" actually is: how often a fresh frame
        // reaches the viewer and how much the interval jitters.
        long firstFrameTicks = 0, lastFrameTicks = 0;
        double gapSumMs = 0, gapMaxMs = 0; int gapCount = 0;
        double freq = Stopwatch.Frequency;

        conn.VideoConfigured += cfg =>
        {
            negotiated = cfg.Codec; cfgW = cfg.Width; cfgH = cfg.Height;
        };
        conn.StatReceived += stat => { encoderName = stat.EncoderName; lastStat = stat; };
        conn.FrameReceived += (_, _, tiles, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            int n = Interlocked.Increment(ref frames);
            if (n == 1) firstFrameTicks = now;
            else { double gap = (now - lastFrameTicks) / freq * 1000; gapSumMs += gap; gapCount++; if (gap > gapMaxMs) gapMaxMs = gap; }
            lastFrameTicks = now;

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
            ResolutionMode = scaledW > 0 ? ResolutionMode.Scaled : ResolutionMode.Native,
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            PreferredCodec = wantCodec,   // auto/h264 -> H.264 (host may fall back); jpeg -> forced JPEG
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
                Line("Terhubung. Menggerakkan kursor host untuk memaksa perubahan layar, ukur ~6 detik…");
                var sw = Stopwatch.StartNew();
                int i = 0;
                const ushort VK_LWIN = 0x5B;
                while (sw.Elapsed < TimeSpan.FromSeconds(6))
                {
                    // Toggle the Start menu (Win key) — a guaranteed full-screen repaint on any Windows
                    // host, so we get representative capture/encode numbers. Also proves whether input
                    // injection reaches the host at all (no repaint => input isn't landing).
                    i++;
                    conn.SendKey(new KeyEventData(VK_LWIN, 0, true, true));
                    conn.SendKey(new KeyEventData(VK_LWIN, 0, false, true));
                    await Task.Delay(300);
                }

                Line($"Codec dinegosiasi : {negotiated}");
                Line($"Geometri          : {cfgW}x{cfgH}");
                Line($"Frame diterima    : {frames}  (decode H264 ok: {decodedOk}, non-hitam: {nonBlack})");
                double clientFps = gapCount > 0 ? 1000.0 / (gapSumMs / gapCount) : 0;
                Line($"Antar-frame klien : {(gapCount > 0 ? (gapSumMs / gapCount).ToString("F1") : "?")} ms rata2 / {gapMaxMs:F0} ms maks  -> ~{clientFps:F0} fps tiba");
                if (lastStat is { } s)
                {
                    Line(new string('-', 56));
                    Line("Laporan HOST (pengukuran host sendiri):");
                    Line($"  fps={s.Fps}  bitrate={s.MbitsPerSecond:F1} Mbit/s");
                    Line($"  capture={s.RoundTripMs} ms  encode={s.EncodeMs} ms  -> {s.EncoderName}");
                    Line($"  => komponen dominan: {(s.RoundTripMs >= s.EncodeMs ? $"CAPTURE ({s.RoundTripMs} ms)" : $"ENCODE ({s.EncodeMs} ms)")}");
                }
                else Line("(host belum mengirim statistik — layar host mungkin diam)");
                Line(new string('-', 56));
                Line("CATATAN: gerakkan sesuatu di layar HOST saat uji agar frame mengalir; layar diam = sedikit frame.");
                exit = frames > 0 ? 0 : 2;
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
