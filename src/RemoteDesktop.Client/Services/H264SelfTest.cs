using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        // A composite of the received frame so we can save a PNG and actually SEE what was captured
        // (e.g. the login screen). BGRA, top-down, stride = cfgW*4.
        byte[]? comp = null;

        conn.VideoConfigured += cfg =>
        {
            negotiated = cfg.Codec;
            // Keep the composite across same-size re-announces (the secure-desktop DXGI path re-announces
            // on ACCESS_LOST); only reallocate when the geometry actually changes.
            if (comp == null || cfg.Width != cfgW || cfg.Height != cfgH)
                comp = new byte[cfg.Width * cfg.Height * 4];
            cfgW = cfg.Width; cfgH = cfg.Height;
        };
        conn.StatReceived += stat => { encoderName = stat.EncoderName; lastStat = stat; };
        conn.FrameReceived += (_, _, tiles, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            int n = Interlocked.Increment(ref frames);
            if (n == 1) firstFrameTicks = now;
            else { double gap = (now - lastFrameTicks) / freq * 1000; gapSumMs += gap; gapCount++; if (gap > gapMaxMs) gapMaxMs = gap; }
            lastFrameTicks = now;

            if (tiles.Count == 0) return;
            lock (decodeLock)
            {
                try
                {
                    if (negotiated == VideoCodec.H264)
                    {
                        decoder ??= H264Decoder.TryCreate(cfgW, cfgH, 60, out _);
                        var bgra = decoder?.Decode(tiles[0].Data.ToArray(), out _);
                        if (bgra != null)
                        {
                            decodedOk++;
                            if (!IsMostlyBlack(bgra)) nonBlack++;
                            if (comp != null) BlitFull(comp, cfgW, cfgH, bgra, decoder!.Width, decoder!.Height);
                        }
                    }
                    else if (comp != null) // JPEG tiles
                    {
                        foreach (var t in tiles) BlitJpeg(comp, cfgW, cfgH, t);
                    }
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
                if (res == "keytest")
                {
                    // Type one digit into the PIN field, hold it visible while we capture, then erase it
                    // (no Enter => no failed sign-in attempt). A visible dot proves keyboard input lands
                    // on the secure desktop.
                    await Task.Delay(400);
                    conn.SendKey(new KeyEventData(0x31, 0, true, false));  // '1' down
                    conn.SendKey(new KeyEventData(0x31, 0, false, false)); // '1' up
                    for (int k = 0; k < 20 && sw.Elapsed < TimeSpan.FromSeconds(5); k++)
                    {
                        conn.RequestKeyFrame();
                        await Task.Delay(150);
                    }
                    conn.SendKey(new KeyEventData(0x08, 0, true, false));  // Backspace to clear
                    conn.SendKey(new KeyEventData(0x08, 0, false, false));
                }
                else if (res == "clicktest")
                {
                    // Safe login-screen input test: click the OTHER user tile ("untuk_remote", lower
                    // left) — switching users is a clear visible change that proves input reaches the
                    // secure desktop, without touching any PIN field.
                    ushort nx = (ushort)(110.0 / cfgW * 65535), ny = (ushort)(712.0 / cfgH * 65535);
                    for (int k = 0; k < 30 && sw.Elapsed < TimeSpan.FromSeconds(7); k++)
                    {
                        conn.SendMouseMove(new MouseMoveEvent(nx, ny));
                        if (k == 4)
                        {
                            conn.SendMouseButton(new MouseButtonEvent(MouseButton.Left, true, nx, ny));
                            await Task.Delay(30);
                            conn.SendMouseButton(new MouseButtonEvent(MouseButton.Left, false, nx, ny));
                        }
                        conn.RequestKeyFrame(); // force a full frame so the composite shows the whole screen
                        await Task.Delay(150);
                    }
                }
                else {
                const ushort VK_LWIN = 0x5B, VK_BACK = 0x08;
                // Open Start once, then type/erase continuously in its search box — a steady stream of
                // real content repaints (not gated like a 300 ms toggle), so the measured client fps
                // reflects the true capture→encode→render throughput, not our input cadence.
                conn.SendKey(new KeyEventData(VK_LWIN, 0, true, true));
                conn.SendKey(new KeyEventData(VK_LWIN, 0, false, true));
                await Task.Delay(500);
                var letters = new ushort[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48 }; // A..H
                while (sw.Elapsed < TimeSpan.FromSeconds(6))
                {
                    foreach (var vk in letters)
                    {
                        conn.SendKey(new KeyEventData(vk, 0, true, false));
                        conn.SendKey(new KeyEventData(vk, 0, false, false));
                        await Task.Delay(25);
                    }
                    for (int b = 0; b < letters.Length; b++)
                    {
                        conn.SendKey(new KeyEventData(VK_BACK, 0, true, false));
                        conn.SendKey(new KeyEventData(VK_BACK, 0, false, false));
                        await Task.Delay(25);
                    }
                }
                conn.SendKey(new KeyEventData(0x1B, 0, true, false)); // Esc — close Start
                conn.SendKey(new KeyEventData(0x1B, 0, false, false));
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
                if (comp != null && cfgW > 0)
                {
                    try
                    {
                        var bs = BitmapSource.Create(cfgW, cfgH, 96, 96, PixelFormats.Bgra32, null, comp, cfgW * 4);
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bs));
                        var png = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lastframe.png");
                        using var fs = File.Create(png); enc.Save(fs);
                        Line($"Frame terakhir disimpan: {png}");
                    }
                    catch (Exception ex) { Line($"(gagal simpan PNG: {ex.Message})"); }
                }
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

    /// <summary>Decode one JPEG tile and copy it into the composite at its (X,Y).</summary>
    private static void BlitJpeg(byte[] comp, int cw, int ch, Tile t)
    {
        using var ms = new MemoryStream(t.Data.ToArray(), writable: false);
        var dec = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource fr = dec.Frames[0];
        if (fr.Format != PixelFormats.Bgra32) fr = new FormatConvertedBitmap(fr, PixelFormats.Bgra32, null, 0);
        int tw = t.Width, th = t.Height, stride = tw * 4;
        var px = new byte[stride * th];
        fr.CopyPixels(px, stride, 0);
        for (int row = 0; row < th; row++)
        {
            int dy = t.Y + row; if (dy >= ch) break;
            int len = Math.Min(stride, (cw - t.X) * 4);
            if (len > 0) Array.Copy(px, row * stride, comp, (dy * cw + t.X) * 4, len);
        }
    }

    /// <summary>Copy a full decoded H.264 frame (may be macroblock-padded) into the composite, cropped.</summary>
    private static void BlitFull(byte[] comp, int cw, int ch, byte[] bgra, int dw, int dh)
    {
        int copyH = Math.Min(ch, dh), copyW = Math.Min(cw, dw);
        for (int y = 0; y < copyH; y++) Array.Copy(bgra, y * dw * 4, comp, y * cw * 4, copyW * 4);
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
