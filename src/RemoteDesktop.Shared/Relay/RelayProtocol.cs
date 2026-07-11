using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Shared.Relay;

/// <summary>
/// TeamViewer-style rendezvous: hosts keep a registration connection open to a relay server and
/// advertise a short numeric ID; viewers ask the relay for that ID and the relay splices the two
/// sockets together. The handshake below is newline-delimited JSON; once both sides are joined the
/// connection becomes a raw byte pipe carrying the normal TLS session — so the relay never sees
/// plaintext and certificate pinning still protects against a malicious relay.
/// </summary>
public sealed record RelayMsg
{
    [JsonPropertyName("op")] public string? Op { get; init; }          // register | offer | join | connect | ping | pong
    [JsonPropertyName("id")] public string? Id { get; init; }          // 9-digit host id
    [JsonPropertyName("key")] public string? Key { get; init; }        // host secret, defends the id against takeover
    [JsonPropertyName("session")] public string? Session { get; init; } // one-shot splice token
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public static class RelayProtocol
{
    public const int DefaultPort = 7500;
    private const int MaxLine = 4096;

    public static async Task SendAsync(Stream stream, RelayMsg msg, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        var buf = new byte[json.Length + 1];
        json.CopyTo(buf, 0);
        buf[^1] = (byte)'\n';
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Read one newline-terminated JSON message. Returns null on EOF or malformed input.</summary>
    public static async Task<RelayMsg?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var buf = new byte[MaxLine];
        int len = 0;
        var one = new byte[1];
        while (len < MaxLine)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return null;
            if (one[0] == (byte)'\n') break;
            buf[len++] = one[0];
        }
        try { return JsonSerializer.Deserialize<RelayMsg>(Encoding.UTF8.GetString(buf, 0, len)); }
        catch { return null; }
    }

    /// <summary>Format a 9-digit id as "123 456 789" for display.</summary>
    public static string FormatId(string id) =>
        id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    /// <summary>
    /// Keep only ASCII 0-9. <see cref="char.IsDigit(char)"/> accepts the entire Unicode "Nd" category
    /// (Arabic-Indic, Devanagari, full-width, …), which would let a mobile keyboard in another locale
    /// produce an id that never matches the 9 ASCII digits a host registered, and widens the id space
    /// an attacker can spray. (audit M-A0: AUD-009)
    /// </summary>
    public static string NormalizeId(string input) =>
        new(input.Where(c => c is >= '0' and <= '9').ToArray());
}
