using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgenticRagApp.Api.Security;

// The interim inbound auth for POST /api/query: one static shared secret, sent as
// `Authorization: Bearer <token>`, checked against the QUERY_API_KEY app setting.
//
// WHAT THIS IS AND IS NOT (2026-09-24, D238). It is a bearer TOKEN, not an identity: every
// caller presents the same string, so the API learns that the caller holds the secret and
// nothing about WHO called. There is no expiry, no revocation short of rotating the setting, no
// per-caller rate limit and no audit trail naming a principal - the query report still records
// only the question. It exists so the network allowlist stops being the only control the moment
// a caller outside Azure is let in, and it is meant to be replaced by Entra app-to-app auth
// (D238 §4), not extended.
//
// The secret is NOT in source and must never be: Terraform generates it
// (random_password.query_api_key) and writes the app setting, so it lives in Terraform state and
// in the app's configuration, never in git and never in the public mirror. The value is read
// once at startup.
public sealed class ApiKeyGuard
{
    private const string BearerPrefix = "Bearer ";

    // Stored as UTF-8 bytes so the comparison below never has to encode on the hot path, and so
    // the constant-time helper can work on spans.
    private readonly byte[] _expected;

    public ApiKeyGuard(string expectedKey)
    {
        if (string.IsNullOrWhiteSpace(expectedKey))
            throw new ArgumentException("The API key must be a non-empty string.", nameof(expectedKey));

        _expected = Encoding.UTF8.GetBytes(expectedKey);
    }

    // True only for "Bearer <exact token>". Deliberately strict: the scheme is matched
    // case-sensitively and no other scheme is accepted, because a client that sends the token
    // under the wrong scheme should fail loudly here rather than half-work.
    //
    // FixedTimeEquals, not ==: string equality returns on the first differing byte, which leaks
    // how much of a guessed token is correct to anyone who can time the response. It also
    // requires equal lengths, so the length check in front of it is what would leak instead -
    // and token length is not a secret, since every caller holds the same 48-character value.
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader)) return false;
        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.Ordinal)) return false;

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[BearerPrefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }
}

// Applied to the query endpoint only (Program.cs). GET /health stays open because App Service's
// own health check cannot send a header and would evict the instance; GET /openapi/v1.json stays
// open because the OutSystems import reads it before any credential is configured. Both are
// behind the same network deny-by-default as everything else - see D238 §3 for why that is
// currently load-bearing.
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    private readonly ApiKeyGuard _guard;
    private readonly ILogger<ApiKeyEndpointFilter> _logger;

    public ApiKeyEndpointFilter(ApiKeyGuard guard, ILogger<ApiKeyEndpointFilter> logger)
    {
        _guard  = guard;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();

        if (!_guard.IsAuthorized(header))
        {
            // No token echoed, not even a prefix: the log ships to App Insights and a rejected
            // token is still a credential. The remote IP is what an operator actually needs.
            _logger.LogWarning(
                "Rejected an unauthenticated call to {Path} from {RemoteIp}",
                context.HttpContext.Request.Path,
                context.HttpContext.Connection.RemoteIpAddress);

            // problem+json, like every other non-2xx this host produces (AddProblemDetails), so
            // the OutSystems side parses one error shape. WWW-Authenticate names the scheme, per
            // RFC 9110 - a bare 401 leaves a client guessing.
            context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";

            return TypedResults.Problem(
                title:      "Missing or invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
