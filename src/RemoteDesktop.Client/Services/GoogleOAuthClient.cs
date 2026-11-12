using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Implements Google's OAuth 2.0 "loopback" flow with PKCE — the recommended pattern for native
/// desktop apps. We spin up a throwaway HTTP listener on 127.0.0.1, send the user to Google in their
/// default browser, receive the authorization code on the loopback redirect, and exchange it for an
/// <c>id_token</c>. That id_token is what we hand to the host, which verifies it offline.
///
/// PKCE protects the code exchange; the Desktop client's (non-confidential) secret is sent only if configured.
/// </summary>
public sealed class GoogleOAuthClient
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly string _clientId;
    private readonly string? _clientSecret;
    private readonly HttpClient _http;

    public GoogleOAuthClient(string clientId, HttpClient? http = null, string? clientSecret = null)
    {
        _clientId = clientId;
        _http = http ?? new HttpClient();
        // Google's token endpoint demands client_secret for "Desktop app" clients even with PKCE.
        _clientSecret = !string.IsNullOrWhiteSpace(clientSecret) ? clientSecret : ClientConfig.Shared.GoogleClientSecret;
    }

    public async Task<string?> SignInAsync(CancellationToken ct = default)
    {
        // Reserve a loopback port for the redirect.
        var listener = new HttpListener();
        int port = GetFreePort();
        string redirectUri = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            var (verifier, challenge) = CreatePkce();
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            var authUrl = $"{AuthEndpoint}?" +
                $"client_id={Uri.EscapeDataString(_clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString("openid email profile")}" +
                $"&code_challenge={challenge}&code_challenge_method=S256" +
                $"&state={state}";

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            await RespondToBrowserAsync(context.Response).ConfigureAwait(false);

            if (code is null || returnedState != state) return null;

            return await ExchangeCodeAsync(code, verifier, redirectUri, ct).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string?> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        if (!string.IsNullOrWhiteSpace(_clientSecret)) fields["client_secret"] = _clientSecret!;
        var form = new FormUrlEncodedContent(fields);

        var response = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Surface Google's reason (e.g. "client_secret is missing") instead of a silent null.
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Diag.Log($"Google token exchange failed ({(int)response.StatusCode}): {body}");
            return null;
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
        return token?.IdToken;
    }

    private static async Task RespondToBrowserAsync(HttpListenerResponse response)
    {
        const string html = "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'>" +
                            "<h2>You're signed in.</h2><p>Return to LiteRemote — you can close this tab.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static (string verifier, string challenge) CreatePkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed record TokenResponse([property: JsonPropertyName("id_token")] string IdToken);
}
