using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Client;

/// <summary>
/// Platform video sink for the viewer. The portable <see cref="ViewerSession"/> parses the wire
/// protocol and hands decoded <see cref="MessageType.VideoFrame"/>s to an implementation of this
/// (Media Foundation on Windows, MediaCodec on Android, VideoToolbox on Mac). See
/// docs/PROTOCOL-SPEC.md §9 for the tile/codec contract.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Called once before frames flow, and again if the stream geometry/codec changes.</summary>
    void Configure(int width, int height, VideoCodec codec);

    /// <summary>
    /// One received VideoFrame. For H.264/H.265, <c>tiles[0].Data</c> is a complete Annex-B access unit
    /// and <paramref name="flags"/> carries <see cref="FrameFlags.KeyFrame"/>; for <c>JpegTiles</c> each
    /// tile is a JPEG dirty-rect. Tile buffers stay valid only until this returns — copy anything kept.
    /// </summary>
    void SubmitFrame(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles);
}
