using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Shared.Security;

/// <summary>Validated claims pulled out of a Google-issued OpenID Connect id_token.</summary>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string Name);

/// <summary>
/// Verifies Google OIDC <c>id_token</c>s offline against Google's published RSA signing keys
/// (JWKS). Used on the host so "Login with Google" doesn't require the host to hold any secret —
/// it only needs to trust Google's signature and match the email against its allow-list.
///
/// Checks performed: RS256 signature, <c>iss</c>, <c>aud</c> (your OAuth client id), and <c>exp</c>.
/// </summary>
public sealed class GoogleIdTokenVerifier
{
    private const string JwksUri = "https://www.googleapis.com/oauth2/v3/certs";
    private const int ClockSkewSeconds = 60; // tolerate small clock differences on exp/nbf (audit AUD-016)
    private static readonly string[] ValidIssuers = { "https://accounts.google.com", "accounts.google.com" };

    private readonly HttpClient _http;
    private readonly string _audience; // your Google OAuth client id
    private readonly Func<DateTimeOffset> _now;

    // Volatile: RefreshKeysAsync swaps the whole dictionary reference atomically, so concurrent
    // VerifyAsync reads always see a complete key set (audit AUD-015).
    private volatile Dictionary<string, RSA> _keys = new();
    private DateTimeOffset _keysFetchedAt = DateTimeOffset.MinValue;

    public GoogleIdTokenVerifier(string audience, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _audience = audience;
        _http = http ?? new HttpClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;

        // A malformed token (bad base64, e.g. length %4==1, or non-JSON segments) must be a clean
        // "not verified" (null), never an escaping FormatException/JsonException. (audit M-A0: AUD-006)
        JwtHeader? header;
        byte[] signature;
        try
        {
            header = JsonSerializer.Deserialize<JwtHeader>(Base64Url.Decode(parts[0]));
            signature = Base64Url.Decode(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
        if (header?.Kid is null || header.Alg != "RS256") return null;

        var rsa = await GetKeyAsync(header.Kid, ct).ConfigureAwait(false);
        if (rsa is null) return null;

        // Signature is over the raw "header.payload" ASCII bytes.
        var signingInput = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return null;

        JwtPayload? payload;
        try { payload = JsonSerializer.Deserialize<JwtPayload>(Base64Url.Decode(parts[1])); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
        if (payload is null) return null;

        if (!ValidIssuers.Contains(payload.Iss)) return null;
        if (payload.Aud != _audience) return null;

        long now = _now().ToUnixTimeSeconds();
        if (now - ClockSkewSeconds >= payload.Exp) return null;                       // expired
        if (payload.Nbf > 0 && now + ClockSkewSeconds < payload.Nbf) return null;      // not yet valid (AUD-016)

        return new GoogleIdentity(payload.Sub ?? "", payload.Email ?? "", payload.EmailVerified, payload.Name ?? "");
    }

    private async Task<RSA?> GetKeyAsync(string kid, CancellationToken ct)
    {
        if (!_keys.ContainsKey(kid) || _now() - _keysFetchedAt > TimeSpan.FromHours(6))
            await RefreshKeysAsync(ct).ConfigureAwait(false);
        return _keys.GetValueOrDefault(kid);
    }

    private async Task RefreshKeysAsync(CancellationToken ct)
    {
        // A transient JWKS fetch/parse failure must NOT throw out of VerifyAsync (that would turn a
        // network blip into a crashed auth) nor wipe a good key set — keep what we have. (audit AUD-015)
        Jwks? jwks;
        try { jwks = await _http.GetFromJsonAsync<Jwks>(JwksUri, ct).ConfigureAwait(false); }
        catch { return; }
        if (jwks?.Keys is null) return;

        var map = new Dictionary<string, RSA>();
        foreach (var k in jwks.Keys)
        {
            if (k.Kty != "RSA" || k.N is null || k.E is null || k.Kid is null) continue;
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64Url.Decode(k.N),
                Exponent = Base64Url.Decode(k.E),
            });
            map[k.Kid] = rsa;
        }
        _keys = map;
        _keysFetchedAt = _now();
    }

    private sealed record JwtHeader([property: JsonPropertyName("alg")] string Alg,
                                    [property: JsonPropertyName("kid")] string Kid);

    private sealed record JwtPayload
    {
        [JsonPropertyName("iss")] public string? Iss { get; init; }
        [JsonPropertyName("aud")] public string? Aud { get; init; }
        [JsonPropertyName("sub")] public string? Sub { get; init; }
        [JsonPropertyName("email")] public string? Email { get; init; }
        [JsonPropertyName("email_verified")] public bool EmailVerified { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("exp")] public long Exp { get; init; }
        [JsonPropertyName("nbf")] public long Nbf { get; init; }
    }

    private sealed record Jwks([property: JsonPropertyName("keys")] List<JwkKey> Keys);
    private sealed record JwkKey
    {
        [JsonPropertyName("kty")] public string? Kty { get; init; }
        [JsonPropertyName("kid")] public string? Kid { get; init; }
        [JsonPropertyName("n")] public string? N { get; init; }
        [JsonPropertyName("e")] public string? E { get; init; }
    }
}

internal static class Base64Url
{
    public static byte[] Decode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
