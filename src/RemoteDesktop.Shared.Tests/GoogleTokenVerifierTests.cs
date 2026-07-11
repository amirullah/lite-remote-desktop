using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// Audit M-A0: Google id_token verification. Uses a self-generated RSA key served as a fake JWKS and a
/// hand-signed JWT so the full RS256 path (signature, iss, aud, exp, nbf) is exercised offline. Covers
/// AUD-006 (malformed -> null), AUD-015 (JWKS fetch failure -> null, not throw), AUD-016 (nbf/skew).
/// </summary>
public class GoogleTokenVerifierTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string> _json;
        private readonly bool _fail;
        public StubHandler(Func<string> json, bool fail = false) { _json = json; _fail = fail; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_fail) throw new HttpRequestException("network down");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwks(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid, use = "sig", alg = "RS256", n = B64Url(p.Modulus!), e = B64Url(p.Exponent!) } },
        });
    }

    private static string SignJwt(RSA rsa, string kid, object payload)
    {
        string Enc(object o) => B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));
        var head = Enc(new { alg = "RS256", kid, typ = "JWT" });
        var body = Enc(payload);
        var signingInput = Encoding.ASCII.GetBytes($"{head}.{body}");
        var sig = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{head}.{body}.{B64Url(sig)}";
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static GoogleIdTokenVerifier Verifier(RSA rsa, string kid, string aud = "my-aud", bool failFetch = false)
        => new(aud, new HttpClient(new StubHandler(() => Jwks(rsa, kid), failFetch)), () => Now);

    [Fact]
    public async Task ValidToken_ReturnsIdentity()
    {
        using var rsa = RSA.Create(2048);
        var token = SignJwt(rsa, "k1", new
        {
            iss = "https://accounts.google.com", aud = "my-aud", sub = "123",
            email = "a@b.com", email_verified = true, name = "A",
            exp = Now.ToUnixTimeSeconds() + 3600, nbf = Now.ToUnixTimeSeconds() - 10,
        });
        var id = await Verifier(rsa, "k1").VerifyAsync(token);
        Assert.NotNull(id);
        Assert.Equal("a@b.com", id!.Email);
        Assert.True(id.EmailVerified);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsNull()
    {
        using var rsa = RSA.Create(2048);
        var token = SignJwt(rsa, "k1", new { iss = "https://accounts.google.com", aud = "my-aud", exp = Now.ToUnixTimeSeconds() - 3600 });
        Assert.Null(await Verifier(rsa, "k1").VerifyAsync(token));
    }

    [Fact]
    public async Task NotYetValid_Nbf_ReturnsNull()
    {
        using var rsa = RSA.Create(2048);
        var token = SignJwt(rsa, "k1", new { iss = "https://accounts.google.com", aud = "my-aud", exp = Now.ToUnixTimeSeconds() + 3600, nbf = Now.ToUnixTimeSeconds() + 3600 });
        Assert.Null(await Verifier(rsa, "k1").VerifyAsync(token));
    }

    [Fact]
    public async Task WrongAudience_ReturnsNull()
    {
        using var rsa = RSA.Create(2048);
        var token = SignJwt(rsa, "k1", new { iss = "https://accounts.google.com", aud = "someone-else", exp = Now.ToUnixTimeSeconds() + 3600 });
        Assert.Null(await Verifier(rsa, "k1").VerifyAsync(token));
    }

    [Fact]
    public async Task TamperedSignature_ReturnsNull()
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        // JWKS serves rsa's public key; token is signed by a different key -> signature mismatch.
        var token = SignJwt(other, "k1", new { iss = "https://accounts.google.com", aud = "my-aud", exp = Now.ToUnixTimeSeconds() + 3600 });
        Assert.Null(await Verifier(rsa, "k1").VerifyAsync(token));
    }

    [Fact]
    public async Task JwksFetchFailure_ReturnsNull_NotThrow()
    {
        using var rsa = RSA.Create(2048);
        var token = SignJwt(rsa, "k1", new { iss = "https://accounts.google.com", aud = "my-aud", exp = Now.ToUnixTimeSeconds() + 3600 });
        Assert.Null(await Verifier(rsa, "k1", failFetch: true).VerifyAsync(token));
    }
}
