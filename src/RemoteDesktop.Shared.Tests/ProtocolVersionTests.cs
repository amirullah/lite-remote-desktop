using System.Text;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>Audit M-A0 / M-A1: negosiasi versi protokol lewat handshake auth (AUD-010).</summary>
public class ProtocolVersionTests
{
    [Fact]
    public void AuthResponse_CarriesCurrentVersion_ByDefault()
    {
        var msg = AuthProtocol.Response(new AuthResponseData(AuthMethod.Password, "pw"));
        var back = AuthProtocol.ReadResponse(msg.Span);
        Assert.Equal(ProtocolInfo.Current, back.ProtocolVersion);
    }

    [Fact]
    public void AuthRequest_CarriesCurrentVersion_ByDefault()
    {
        var msg = AuthProtocol.Request(new AuthRequestData(AuthMethod.Password | AuthMethod.Google, "nonce"));
        var back = AuthProtocol.ReadRequest(msg.Span);
        Assert.Equal(ProtocolInfo.Current, back.ProtocolVersion);
    }

    [Fact]
    public void LegacyPeerJson_WithoutVersion_DefaultsToV1()
    {
        // A pre-versioning peer never serializes "protocolVersion"; it must read back as v1.
        var legacy = Encoding.UTF8.GetBytes("{\"method\":1,\"secret\":\"pw\"}");
        var back = AuthProtocol.ReadResponse(legacy);
        Assert.Equal(1, back.ProtocolVersion);
        Assert.Equal(AuthMethod.Password, back.Method);
        Assert.Equal("pw", back.Secret);
    }

    [Fact]
    public void ExplicitFutureVersion_IsPreserved()
    {
        var future = Encoding.UTF8.GetBytes("{\"method\":1,\"secret\":\"pw\",\"protocolVersion\":99}");
        var back = AuthProtocol.ReadResponse(future);
        Assert.Equal(99, back.ProtocolVersion);
    }

    [Fact]
    public void Compatibility_GateMatchesMinSupported()
    {
        Assert.True(ProtocolInfo.IsCompatible(ProtocolInfo.Current));
        Assert.True(ProtocolInfo.IsCompatible(ProtocolInfo.MinSupported));
        Assert.False(ProtocolInfo.IsCompatible(ProtocolInfo.MinSupported - 1));
    }
}
