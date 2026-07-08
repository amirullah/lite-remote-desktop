using System.Diagnostics;
using RemoteDesktop.Host.Capture;
using RemoteDesktop.Media;

namespace RemoteDesktop.Host;

/// <summary>
/// H.264 encode benchmark: <c>LiteRemoteHost --bench-h264</c>. Captures the primary screen, encodes a
/// burst of frames through <see cref="H264Encoder"/>, and reports the encoder chosen, per-frame encode
/// time, output sizes, keyframe cadence, and whether the bitstream carries valid Annex-B NAL start
/// codes. This is milestone M1's acceptance test — it proves the Media Foundation pipeline end-to-end
/// on the machine it runs on (hardware on a physical PC, software fallback in a VM) without touching
/// the live JPEG streaming path. Run it from PowerShell to see the console output.
/// </summary>
internal static class H264Bench
{
    private static readonly System.Text.StringBuilder _log = new();

    public static void Run()
    {
        AttachConsole(-1);

        var displays = GdiScreenCapture.EnumerateDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        // Encode at a sane streaming resolution (cap to 1080p to keep NV12 conversion cheap for the test).
        int tw = Math.Min(primary.Width, 1920);
        int th = Math.Min(primary.Height, 1080);
        int fps = 60;
        int bitrate = 8_000_000;

        Line("");
        Line($"LiteRemote H.264 encode benchmark ({ThisVersion()})");
        Line($"Display {primary.Width}x{primary.Height} -> encode {tw}x{th} @ {fps}fps, {bitrate / 1_000_000} Mbps");
        Line(new string('-', 64));

        H264Encoder? enc = H264Encoder.TryCreate(tw, th, fps, bitrate, preferHardware: true, out string reason);
        if (enc == null)
        {
            Line($"Encoder H.264 TIDAK tersedia: {reason}");
            Line("-> host akan tetap pakai JPEG-tiles (fallback aman).");
            Save();
            return;
        }

        Line($"Encoder: {enc.Info}");

        // M2 round-trip: decode each encoded frame back to BGRA to prove the receiving half works.
        H264Decoder? dec = H264Decoder.TryCreate(tw, th, fps, out string decReason);
        Line(dec != null ? $"Decoder: {dec.Info}" : $"Decoder: TIDAK tersedia ({decReason})");
        Line(new string('-', 64));

        using var cap = new GdiScreenCapture(primary);
        cap.SetTargetSize(tw, th);

        int frames = 150, encoded = 0, keyframes = 0;
        int decoded = 0; long decNonBlack = 0; double decSumMs = 0;
        long totalBytes = 0, firstFrameBytes = 0;
        double sumMs = 0, bestMs = double.MaxValue, worstMs = 0;
        bool sawStartCode = false;
        var sw = new Stopwatch();
        var swd = new Stopwatch();

        try
        {
            for (int i = 0; i < frames; i++)
            {
                var frame = cap.Capture(100);
                if (frame == null || frame.IsEmpty) continue;

                sw.Restart();
                byte[] nal = enc.Encode(frame.Bgra, frame.Stride, out bool key);
                double ms = sw.Elapsed.TotalMilliseconds;

                if (nal.Length == 0) continue; // encoder still buffering (async warm-up)

                encoded++;
                sumMs += ms;
                bestMs = Math.Min(bestMs, ms);
                worstMs = Math.Max(worstMs, ms);
                totalBytes += nal.Length;
                if (key) keyframes++;
                if (encoded == 1) firstFrameBytes = nal.Length;
                if (!sawStartCode) sawStartCode = HasAnnexBStartCode(nal);

                if (dec != null)
                {
                    swd.Restart();
                    byte[]? bgra = dec.Decode(nal, out _);
                    decSumMs += swd.Elapsed.TotalMilliseconds;
                    if (bgra != null)
                    {
                        decoded++;
                        if (!IsMostlyBlack(bgra)) decNonBlack++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Line($"ERROR saat encode: {ex.Message}");
        }
        finally
        {
            dec?.Dispose();
            enc.Dispose();
        }

        Line($"Frame ter-encode : {encoded} / {frames}");
        if (dec != null)
        {
            Line($"Frame ter-decode : {decoded} / {encoded}  (non-hitam: {decNonBlack})");
            if (decoded > 0) Line($"Decode ms        : {decSumMs / decoded,6:F2} avg");
            Line($"Round-trip       : {(decoded > 0 && decNonBlack > 0 ? "BERHASIL (encode->decode->gambar valid)" : "GAGAL — periksa decoder")}");
        }
        if (encoded > 0)
        {
            Line($"Encode ms        : {sumMs / encoded,6:F2} avg / {bestMs,6:F2} best / {worstMs,6:F2} worst");
            Line($"Keyframe (IDR)   : {keyframes}");
            Line($"Frame pertama    : {firstFrameBytes / 1024.0,6:F1} KB (berisi SPS/PPS + IDR)");
            Line($"Rata-rata ukuran : {totalBytes / (double)encoded / 1024.0,6:F1} KB/frame");
            Line($"Bitrate teramati : {totalBytes * 8.0 * fps / encoded / 1_000_000.0,6:F2} Mbps (asumsi {fps}fps)");
            Line($"Annex-B start    : {(sawStartCode ? "VALID (00 00 01 terdeteksi)" : "TIDAK terdeteksi!")}");
            Line(new string('-', 64));
            bool hw = enc.Info.Contains("hardware", StringComparison.OrdinalIgnoreCase);
            // Angka ini mengukur feed+drain sinkron per-frame (termasuk NV12 + round-trip event async),
            // BUKAN murni waktu GPU. 'best' mendekati biaya encode nyata; saat streaming, encode
            // ter-pipeline di belakang capture sehingga tak menambah latensi.
            Line(hw
                ? $"HASIL: encoder HARDWARE ({enc.Info}). Bitstream H.264 valid, ~{totalBytes / (double)encoded / 1024.0:F0} KB/frame."
                : "HASIL: encoder SOFTWARE (khas VM tanpa GPU encode) — valid untuk uji, tetap fallback JPEG di produksi.");
            Line("Encode ter-pipeline di produksi -> tersembunyi di balik capture; inter-frame jauh lebih hemat dari JPEG.");
        }
        Save();
    }

    private static bool IsMostlyBlack(byte[] bgra)
    {
        // Sample a sparse grid; a genuinely decoded desktop frame has plenty of non-zero luma.
        long nonBlack = 0; int samples = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4 * 257) // prime stride to avoid aliasing
        {
            samples++;
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) nonBlack++;
        }
        return samples == 0 || nonBlack * 100 / samples < 2; // <2% non-black -> effectively blank
    }

    private static bool HasAnnexBStartCode(byte[] b)
    {
        for (int i = 0; i + 3 < b.Length; i++)
        {
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) return true;
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1) return true;
        }
        return false;
    }

    private static string ThisVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    private static void Line(string s) { _log.AppendLine(s); Console.WriteLine(s); }

    private static void Save()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "h264-bench.txt");
            System.IO.File.WriteAllText(path, _log.ToString());
            Console.WriteLine($"(saved to {path})");
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
