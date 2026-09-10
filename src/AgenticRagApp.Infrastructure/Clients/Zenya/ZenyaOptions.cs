using Microsoft.Extensions.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Connection settings for one Zenya tenant. Read from the keys the sync pipeline maps out of the
// `zenya-<env>` variable group (.pipelines/base/zenya-document-sync.yml), so the pipeline and the
// hosts never disagree on names:
//   ZENYA_BASE_URL       https://<tenant>.zenya.work[/api]   (per-tenant, no global host - D155 §1)
//   ZENYA_CLIENT_ID      Zenya app-registration id of OUR registration
//   and exactly one of the two credential modes:
//   ZENYA_ENTRA_SCOPE    client_assertion mode (Zenya's chosen route, D175 2026-09-10): the scope
//                        to request from Entra for our own Azure identity; the resulting token is
//                        posted to Zenya as client_assertion. No secret anywhere.
//   ZENYA_CLIENT_SECRET  client_secret mode: the registration's own secret. Kept for a
//                        secret-type registration, should one ever exist.
// Values are trimmed: the first pipeline run (2026-09-10) failed on a pasted leading space.
public sealed class ZenyaOptions
{
    public const string BaseUrlKey      = "ZENYA_BASE_URL";
    public const string ClientIdKey     = "ZENYA_CLIENT_ID";
    public const string ClientSecretKey = "ZENYA_CLIENT_SECRET";
    public const string EntraScopeKey   = "ZENYA_ENTRA_SCOPE";

    public required Uri BaseUrl { get; init; }
    public required string ClientId { get; init; }

    // Exactly one of these is set; see UsesClientAssertion.
    public string? ClientSecret { get; init; }
    public string? EntraScope { get; init; }

    public bool UsesClientAssertion => !string.IsNullOrWhiteSpace(EntraScope);

    // The token endpoint returns expires_in (600s measured, D155 §2). A token is treated as
    // expired this long before Zenya would, so a request in flight at the boundary does not go
    // out with a token that dies on the wire. One minute of a ten-minute token; pending
    // measurement of real behaviour in A8.
    public TimeSpan TokenRefreshSkew { get; init; } = TimeSpan.FromMinutes(1);

    // 429 handling. Zenya documents neither the limits nor the retry headers (D155 §5), so the
    // handler honours Retry-After when present and otherwise backs off exponentially from
    // RetryBaseDelay, up to MaxRetries attempts. Defaults are a starting point to be tuned
    // from the first full run's 429 count (A8), not a measured optimum.
    public int MaxRetries { get; init; } = 5;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public static ZenyaOptions FromConfiguration(IConfiguration configuration)
    {
        static string? Get(IConfiguration c, string key) =>
            string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

        var missing = new[] { BaseUrlKey, ClientIdKey }.Where(k => Get(configuration, k) is null).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required Zenya setting(s): {string.Join(", ", missing)}. " +
                "In ADO these come from the zenya-<env> variable group; locally from environment variables.");

        var secret = Get(configuration, ClientSecretKey);
        var scope  = Get(configuration, EntraScopeKey);
        if (secret is null && scope is null)
            throw new InvalidOperationException(
                $"Set exactly one of {EntraScopeKey} (client_assertion via our Azure identity) or {ClientSecretKey} (secret-type registration); neither is set.");
        if (secret is not null && scope is not null)
            throw new InvalidOperationException(
                $"Set exactly one of {EntraScopeKey} or {ClientSecretKey}; both are set, and the two modes are mutually exclusive.");

        // Accept the site (https://tenant.zenya.work) or the API root (.../api); every endpoint
        // lives under /api, so normalise to the API root with a trailing slash so relative paths
        // ("oauth/token", "documents") resolve beneath it rather than replacing it.
        var baseUrl = Get(configuration, BaseUrlKey)!.TrimEnd('/');
        if (!baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) baseUrl += "/api";
        if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute URL: '{baseUrl}'.");

        return new ZenyaOptions
        {
            BaseUrl      = uri,
            ClientId     = Get(configuration, ClientIdKey)!,
            ClientSecret = secret,
            EntraScope   = scope,
        };
    }
}
