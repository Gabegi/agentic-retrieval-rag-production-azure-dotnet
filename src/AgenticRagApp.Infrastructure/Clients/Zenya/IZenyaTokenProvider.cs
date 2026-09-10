namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// A Zenya access token plus the Authorization scheme to send it with. Scheme is whatever the
// token endpoint declared in `token_type` (RFC 6749 §7.1): the spec says "Bearer", the customer
// doc's sample shows "token", and rather than pick one the client echoes the tenant's answer.
public sealed record ZenyaAccessToken(string Token, string Scheme, DateTimeOffset ExpiresOn);

// Behind an interface so the secret flow implemented today (ZenyaClientSecretTokenProvider) can
// sit beside a client_assertion flow if Zenya ever registers our managed identity instead - the
// client itself does not care which leg produced the token.
public interface IZenyaTokenProvider
{
    // Returns a cached token while it is valid, otherwise acquires a fresh one. Safe to call
    // concurrently; only one acquisition is in flight at a time.
    ValueTask<ZenyaAccessToken> GetTokenAsync(CancellationToken ct = default);
}
