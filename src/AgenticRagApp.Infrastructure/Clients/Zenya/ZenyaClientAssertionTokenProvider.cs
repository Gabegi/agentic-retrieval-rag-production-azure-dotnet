using Azure.Core;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// The route Zenya chose for us (D175, 2026-09-10): the RefSync pattern applied to our own
// identity. Two legs, no secret anywhere:
//
//   leg 1  Entra issues a token for OUR Azure identity (TokenCredential: AzureCliCredential
//          under the ADO service connection, ManagedIdentityCredential on the Function App),
//          for the scope Zenya's assertion validation expects (ZENYA_ENTRA_SCOPE - P2, supplied
//          by Zenya; undocumented).
//   leg 2  POST {base}/oauth/token
//          grant_type=client_credentials&client_id=<zenya app reg id>
//          &client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
//          &client_assertion=<the Entra token>
//
// Zenya validates that the JWT was issued by the azure_tenant_id / azure_client_id pair on the
// registration (D173 §1). A wrong pair, wrong audience or a token for a different identity all
// come back as the same bare 401 "invalid client assertion", so when leg 2 fails the first thing
// to check is the registration's two Azure ids, then the scope.
//
// client_assertion_type is the RFC 7523 §2.2 value. Zenya's swagger names only client_assertion;
// the type field is sent because RFC 7521 requires it and every compliant server ignores an
// unexpected form field rather than rejecting on it. If the first live run shows Zenya rejecting
// on it, drop it here and record why.
public sealed class ZenyaClientAssertionTokenProvider : ZenyaTokenProviderBase
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly TokenCredential _credential;
    private readonly ILogger<ZenyaClientAssertionTokenProvider> _logger;

    public ZenyaClientAssertionTokenProvider(
        HttpClient http,
        ZenyaOptions options,
        TokenCredential credential,
        ILogger<ZenyaClientAssertionTokenProvider> logger,
        TimeProvider? time = null)
        : base(http, options, logger, time)
    {
        if (!options.UsesClientAssertion)
            throw new ArgumentException($"{nameof(ZenyaClientAssertionTokenProvider)} requires {ZenyaOptions.EntraScopeKey}.", nameof(options));
        _credential = credential;
        _logger     = logger;
    }

    protected override string ModeName => "client_assertion";

    protected override async Task<IReadOnlyDictionary<string, string>> CredentialFieldsAsync(CancellationToken ct)
    {
        // Leg 1. Entra's failures are the informative ones and surface as
        // Azure.Identity exceptions with their AADSTS code intact - let them propagate.
        var entra = await _credential.GetTokenAsync(new TokenRequestContext(new[] { Options.EntraScope! }), ct);
        _logger.LogInformation("Entra token obtained for scope {Scope}, expires {ExpiresOn:u}", Options.EntraScope, entra.ExpiresOn);

        return new Dictionary<string, string>
        {
            ["client_assertion_type"] = AssertionType,
            ["client_assertion"]      = entra.Token,
        };
    }

    protected override string RefusalHint(string? title) =>
        string.Equals(title, "invalid client assertion", StringComparison.OrdinalIgnoreCase)
            ? " Zenya did not accept the Entra token: check that the registration's azure_tenant_id/azure_client_id match the identity this host runs as, then the scope (ZENYA_ENTRA_SCOPE)."
            : string.Empty;
}
