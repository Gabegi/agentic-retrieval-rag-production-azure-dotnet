using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// OAuth2 client-credentials with the registration's own secret (D155 §2 option A):
//
//   POST {base}/oauth/token   application/x-www-form-urlencoded   x-api-version: 5
//   grant_type=client_credentials&client_id=<zenya app reg id>&client_secret=<secret>
//
// Requires a registration of authentication_type `client_secret` (Zenya UI: "Clientgeheim").
// Against a `client_assertion` registration (RefSync, and the route Zenya chose for us on
// 2026-09-10) Zenya answers 401 "invalid client assertion". Kept because it is Zenya's
// documented default and costs nothing; ZenyaClientAssertionTokenProvider is the active mode.
public sealed class ZenyaClientSecretTokenProvider : ZenyaTokenProviderBase
{
    public ZenyaClientSecretTokenProvider(
        HttpClient http,
        ZenyaOptions options,
        ILogger<ZenyaClientSecretTokenProvider> logger,
        TimeProvider? time = null)
        : base(http, options, logger, time)
    {
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new ArgumentException($"{nameof(ZenyaClientSecretTokenProvider)} requires {ZenyaOptions.ClientSecretKey}.", nameof(options));
    }

    protected override string ModeName => "client_secret";

    protected override Task<IReadOnlyDictionary<string, string>> CredentialFieldsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["client_secret"] = Options.ClientSecret!,
        });

    protected override string RefusalHint(string? title) =>
        string.Equals(title, "invalid client assertion", StringComparison.OrdinalIgnoreCase)
            ? " The registration is client_assertion type and takes no secret; use ZENYA_ENTRA_SCOPE (client_assertion mode) or a secret-type registration (D175)."
            : string.Empty;
}
