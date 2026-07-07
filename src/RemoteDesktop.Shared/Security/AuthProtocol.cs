using System.Text.Json;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Security;

[Flags]
public enum AuthMethod : byte
{
    None = 0,
    Password = 1,
    Google = 2,
}

/// <summary>Host -> client: which methods are accepted plus a per-session anti-replay nonce.</summary>
public sealed record AuthRequestData(AuthMethod Methods, string Nonce);

/// <summary>Client -> host: the chosen method and its proof (password text or Google id_token).</summary>
public sealed record AuthResponseData(AuthMethod Method, string Secret);

/// <summary>Host -> client: verdict plus an opaque resumable session token on success.</summary>
public sealed record AuthResultData(bool Ok, string Reason, string SessionToken);

/// <summary>
/// Auth payloads are rare (once per session) and human-readable matters for debugging, so these
/// use JSON — unlike the hot input/video paths. The traffic is inside TLS regardless.
/// </summary>
public static class AuthProtocol
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Message Request(AuthRequestData d) => Wrap(MessageType.AuthRequest, d);
    public static Message Response(AuthResponseData d) => Wrap(MessageType.AuthResponse, d);
    public static Message Result(AuthResultData d) => Wrap(MessageType.AuthResult, d);

    public static AuthRequestData ReadRequest(ReadOnlySpan<byte> p) => Unwrap<AuthRequestData>(p);
    public static AuthResponseData ReadResponse(ReadOnlySpan<byte> p) => Unwrap<AuthResponseData>(p);
    public static AuthResultData ReadResult(ReadOnlySpan<byte> p) => Unwrap<AuthResultData>(p);

    private static Message Wrap<T>(MessageType type, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return new Message(type, bytes);
    }

    private static T Unwrap<T>(ReadOnlySpan<byte> p) =>
        JsonSerializer.Deserialize<T>(p, Json)!;
}
