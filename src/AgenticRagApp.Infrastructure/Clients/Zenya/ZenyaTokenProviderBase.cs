using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Shared half of both token providers: the POST to Zenya's /oauth/token, the response mapping,
// and the cache. Subclasses only decide which credential goes into the form body - a
// `client_secret` (ZenyaClientSecretTokenProvider) or an Entra-issued `client_assertion`
// (ZenyaClientAssertionTokenProvider). Zenya's swagger declares the endpoint takes one or the
// other (D173 §1 "Authentication").
//
// Caches the Zenya token until expires_in minus ZenyaOptions.TokenRefreshSkew. expires_in was
// measured at 600s (D155 §2), and a full corpus crawl outlives that, so refresh is a requirement
// of the client, not a nicety (D175 A4).
public abstract class ZenyaTokenProviderBase : IZenyaTokenProvider
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ZenyaAccessToken? _cached;

    protected ZenyaOptions Options { get; }

    protected ZenyaTokenProviderBase(HttpClient http, ZenyaOptions options, ILogger logger, TimeProvider? time)
    {
        _http   = http;
        Options = options;
        _logger = logger;
        _time   = time ?? TimeProvider.System;
    }

    public async ValueTask<ZenyaAccessToken> GetTokenAsync(CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached is not null && !IsExpiring(cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: a concurrent caller may have refreshed while we waited.
            cached = _cached;
            if (cached is not null && !IsExpiring(cached)) return cached;

            var fresh = await AcquireAsync(ct);
            _cached = fresh;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The credential-specific form fields for /oauth/token, beside grant_type and client_id.
    protected abstract Task<IReadOnlyDictionary<string, string>> CredentialFieldsAsync(CancellationToken ct);

    // Named in log lines and exception text so a refusal says which mode was in play.
    protected abstract string ModeName { get; }

    private bool IsExpiring(ZenyaAccessToken token) =>
        _time.GetUtcNow() >= token.ExpiresOn - Options.TokenRefreshSkew;

    private async Task<ZenyaAccessToken> AcquireAsync(CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"]  = Options.ClientId,
        };
        foreach (var (key, value) in await CredentialFieldsAsync(ct))
            fields[key] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Options.BaseUrl, "oauth/token"))
        {
            // FormUrlEncodedContent url-encodes the values - a secret may carry form-unsafe
            // characters, and a JWT carries '.' and '_' which are safe but this costs nothing.
            Content = new FormUrlEncodedContent(fields),
        };
        ZenyaClient.AddCommonHeaders(request);

        var requestedAt = _time.GetUtcNow();
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ZenyaClient.TryReadProblemAsync(response, ct);
            var title = problem?.Title ?? problem?.ErrorDescription;
            _logger.LogError("Zenya token request ({Mode}) refused: HTTP {Status} '{Title}' for client_id {ClientId}",
                ModeName, (int)response.StatusCode, title, Options.ClientId);
            throw new ZenyaAuthenticationException(response.StatusCode, title,
                $"Zenya token endpoint refused client_id '{Options.ClientId}' ({ModeName}) with HTTP {(int)response.StatusCode}" +
                (title is null ? "." : $": '{title}'.") +
                RefusalHint(title));
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                   ?? throw new ZenyaAuthenticationException(response.StatusCode, null,
                       "Zenya token endpoint returned 200 with an empty body.");
        if (string.IsNullOrEmpty(body.AccessToken))
            throw new ZenyaAuthenticationException(response.StatusCode, null,
                "Zenya token endpoint returned 200 without an access_token.");

        // token_type is what the Authorization header must carry; expires_in is authoritative
        // over the "two weeks" in Zenya's prose docs (D155 §2, "Token lifetimes").
        var scheme    = string.IsNullOrWhiteSpace(body.TokenType) ? "Bearer" : body.TokenType.Trim();
        var expiresOn = requestedAt.AddSeconds(body.ExpiresIn ?? 0);

        _logger.LogInformation("Zenya token acquired ({Mode}): token_type={Scheme} expires_in={ExpiresIn}s",
            ModeName, scheme, body.ExpiresIn);

        return new ZenyaAccessToken(body.AccessToken, scheme, expiresOn);
    }

    // Zenya's one informative refusal, measured 2026-08-28 and eight times on 2026-09-10: the
    // registration's authentication type does not match what was sent.
    protected virtual string RefusalHint(string? title) =>
        string.Equals(title, "invalid client assertion", StringComparison.OrdinalIgnoreCase)
            ? " Zenya found the registration but rejected the credential for its authentication type - see D175 for the type/mode matrix."
            : string.Empty;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);
}
