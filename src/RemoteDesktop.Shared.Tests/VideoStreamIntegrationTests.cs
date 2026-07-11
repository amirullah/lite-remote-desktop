using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RemoteDesktop.Shared.Client;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Net;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// M-A2: the portable viewer video pipeline, proven runnable end-to-end (minus the native pixel
/// decode). A loopback host authenticates, then streams VideoConfig + two VideoFrames; the client's
/// ViewerSession must configure the decoder and hand it the exact Annex-B access units with the right
/// keyframe flags. This is everything the Android/Mac decoder plugs into via IVideoDecoder.
/// </summary>
public class VideoStreamIntegrationTests
{
    private sealed class FakeDecoder : IVideoDecoder
    {
        public (int w, int h, VideoCodec c)? Config;
        public readonly List<(uint id, FrameFlags flags, byte[] data)> Frames = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expect;
        public Task Ready => _ready.Task;
        public FakeDecoder(int expect) => _expect = expect;

        public void Configure(int width, int height, VideoCodec codec) => Config = (width, height, codec);

        public void SubmitFrame(uint frameId, FrameFlags flags, IReadOnlyList<Tile> tiles)
        {
            Frames.Add((frameId, flags, tiles.Count > 0 ? tiles[0].Data.ToArray() : Array.Empty<byte>()));
            if (Frames.Count >= _expect) _ready.TrySetResult();
        }

        public void Dispose() { }
    }

    private static readonly byte[] Keyframe =
        { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, 0x11, 0x22 };
    private static readonly byte[] DeltaFrame = { 0, 0, 0, 1, 0x41, 0x9A, 0xBB };

    [Fact]
    public async Task ViewerSession_ReceivesConfigAndAnnexBFrames_OverLoopback()
    {
        var certPath = Path.Combine(Path.GetTempPath(), "literemote-vs-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            var cert = CertificateManager.GetOrCreateHostCertificate(certPath);
            var pin = CertificateManager.PublicKeyFingerprint(cert);
            var passwordHash = PasswordHasher.Hash("pw");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var hostTask = Task.Run(async () =>
            {
                using var htcp = await listener.AcceptTcpClientAsync();
                var ssl = new SslStream(htcp.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                await using var ch = new MessageChannel(ssl);

                await ch.SendAsync(AuthProtocol.Request(new AuthRequestData(AuthMethod.Password, "nonce")));
                var e = ch.Inbound.ReadAllAsync().GetAsyncEnumerator();
                while (await e.MoveNextAsync())
                {
                    if (e.Current.Type != MessageType.AuthResponse) continue;
                    var ok = PasswordHasher.Verify(AuthProtocol.ReadResponse(e.Current.Span).Secret, passwordHash);
                    await ch.SendAsync(AuthProtocol.Result(new AuthResultData(ok, "ok", "t")));
                    if (!ok) return;
                    break;
                }

                // Stream one keyframe + one delta.
                await ch.SendAsync(PayloadCodec.VideoConfigMsg(new VideoConfig(320, 240, VideoCodec.H264, 64)));
                await ch.SendAsync(VideoFrameCodec.Encode(1, FrameFlags.KeyFrame,
                    new List<Tile> { new(0, 0, 320, 240, Keyframe) }));
                await ch.SendAsync(VideoFrameCodec.Encode(2, FrameFlags.None,
                    new List<Tile> { new(0, 0, 320, 240, DeltaFrame) }));

                // Keep the channel alive (write loop flushes the frames) until the client disconnects.
                while (await e.MoveNextAsync()) { }
            });

            var fake = new FakeDecoder(expect: 2);
            using var cts = new CancellationTokenSource();

            var clientTask = Task.Run(async () =>
            {
                using var ctcp = new TcpClient();
                await ctcp.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(ctcp.GetStream(), false,
                    (_, c, _, _) => CertificateManager.PublicKeyFingerprint(new X509Certificate2(c!)) == pin);
                await ssl.AuthenticateAsClientAsync("RemoteDesktopHost");
                await using var ch = new MessageChannel(ssl);

                bool authed = false;
                await foreach (var m in ch.Inbound.ReadAllAsync(cts.Token))
                {
                    if (m.Type == MessageType.AuthRequest)
                        await ch.SendAsync(AuthProtocol.Response(new AuthResponseData(AuthMethod.Password, "pw")), cts.Token);
                    else if (m.Type == MessageType.AuthResult)
                    {
                        authed = AuthProtocol.ReadResult(m.Span).Ok;
                        break;
                    }
                }
                if (!authed) return;

                var session = new ViewerSession(ch, fake);
                try { await session.RunAsync(new SessionSettings { PreferredCodec = VideoCodec.H264 }, cts.Token); }
                catch (OperationCanceledException) { }
            });

            await fake.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            cts.Cancel(); // stop the session -> dispose channel -> host sees EOF
            try { await clientTask.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
            try { await hostTask.WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            listener.Stop();

            Assert.Equal((320, 240, VideoCodec.H264), fake.Config);
            Assert.Equal(2, fake.Frames.Count);
            Assert.Equal(FrameFlags.KeyFrame, fake.Frames[0].flags);
            Assert.Equal(Keyframe, fake.Frames[0].data);
            Assert.Equal(FrameFlags.None, fake.Frames[1].flags);
            Assert.Equal(DeltaFrame, fake.Frames[1].data);
        }
        finally
        {
            try { File.Delete(certPath); } catch { }
        }
    }
}
