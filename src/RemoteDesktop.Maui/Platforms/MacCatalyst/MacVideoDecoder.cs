using System.Buffers.Binary;
using AVFoundation;
using CoreMedia;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Maui;

/// <summary>
/// H.264 decoder for Mac Catalyst. Feeds each VideoFrame's complete Annex-B access unit
/// (docs/PROTOCOL-SPEC.md §9) to an <see cref="AVSampleBufferDisplayLayer"/>, which decodes and renders
/// via VideoToolbox internally. The keyframe carries SPS/PPS inline, from which we build the
/// <see cref="CMVideoFormatDescription"/>; access units are converted Annex-B → AVCC (length-prefixed).
///
/// NOTE: written without a Mac to run it — CI (macOS) verifies COMPILE; runtime decode needs a real Mac.
/// </summary>
public sealed class MacVideoDecoder : IVideoDecoder
{
    private readonly AVSampleBufferDisplayLayer _layer;
    private readonly object _gate = new();
    private CMVideoFormatDescription? _format;
    private long _pts;

    public MacVideoDecoder(AVSampleBufferDisplayLayer layer) => _layer = layer;

    public void Configure(int width, int height, VideoCodec codec)
    {
        // Geometry comes from the SPS in the keyframe; nothing to pre-configure for AVSampleBufferDisplayLayer.
    }

    public void SubmitFrame(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;
        var annexB = tiles[0].Data.ToArray();

        lock (_gate)
        {
            try
            {
                if (flags.HasFlag(FrameFlags.KeyFrame))
                {
                    var (sps, pps) = ExtractSpsPps(annexB);
                    if (sps is not null && pps is not null)
                    {
                        _format?.Dispose();
                        _format = CMVideoFormatDescription.FromH264ParameterSets(
                            new List<byte[]> { sps, pps }, 4, out var fErr);
                        if (fErr != CMFormatDescriptionError.None) _format = null;
                    }
                }
                if (_format is null) return; // wait for the first keyframe

                var avcc = AnnexBToAvcc(annexB);
                if (avcc.Length == 0) return;

                using var block = CMBlockBuffer.FromMemoryBlock(avcc, 0, CMBlockBufferFlags.AssureMemoryNow, out var bErr);
                if (bErr != CMBlockBufferError.None || block is null) return;

                var timing = new CMSampleTimingInfo
                {
                    PresentationTimeStamp = new CMTime(_pts++, 1000),
                    DecodeTimeStamp = CMTime.Invalid,
                    Duration = CMTime.Invalid,
                };

                using var sample = CMSampleBuffer.CreateReady(block, _format, 1,
                    new[] { timing }, new nuint[] { (nuint)avcc.Length }, out var sErr);
                if (sErr != CMSampleBufferError.None || sample is null) return;

                _layer.Enqueue(sample);
            }
            catch
            {
                // Transient decode/enqueue hiccup — drop this frame; the next keyframe recovers.
            }
        }
    }

    // --- Annex-B helpers (portable) ---

    /// <summary>Enumerate NAL units (payload after each 00 00 01 / 00 00 00 01 start code).</summary>
    private static IEnumerable<(int start, int length)> Nals(byte[] d)
    {
        int i = 0, n = d.Length;
        while (i + 3 < n)
        {
            // find start code
            int sc = -1, scLen = 0;
            for (int j = i; j + 3 < n; j++)
            {
                if (d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 1) { sc = j; scLen = 3; break; }
                if (j + 4 <= n && d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 0 && d[j + 3] == 1) { sc = j; scLen = 4; break; }
            }
            if (sc < 0) yield break;
            int payload = sc + scLen;
            // find next start code
            int next = n;
            for (int j = payload; j + 3 <= n; j++)
            {
                if (d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 1) { next = j; break; }
                if (j + 4 <= n && d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 0 && d[j + 3] == 1) { next = j; break; }
            }
            if (next > payload) yield return (payload, next - payload);
            i = next;
        }
    }

    private static (byte[]? sps, byte[]? pps) ExtractSpsPps(byte[] d)
    {
        byte[]? sps = null, pps = null;
        foreach (var (start, len) in Nals(d))
        {
            int type = d[start] & 0x1F;
            if (type == 7 && sps is null) sps = d[start..(start + len)];
            else if (type == 8 && pps is null) pps = d[start..(start + len)];
        }
        return (sps, pps);
    }

    /// <summary>Convert Annex-B (start codes) to AVCC (4-byte big-endian length prefixes), keeping only
    /// the VCL/other NALs the display layer needs (SPS/PPS live in the format description).</summary>
    private static byte[] AnnexBToAvcc(byte[] d)
    {
        using var ms = new MemoryStream(d.Length + 16);
        Span<byte> lenBuf = stackalloc byte[4];
        foreach (var (start, len) in Nals(d))
        {
            int type = d[start] & 0x1F;
            if (type == 7 || type == 8) continue; // SPS/PPS are in the format description
            BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)len);
            ms.Write(lenBuf);
            ms.Write(d, start, len);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _layer.FlushAndRemoveImage(); } catch { }
            _format?.Dispose();
            _format = null;
        }
    }
}
