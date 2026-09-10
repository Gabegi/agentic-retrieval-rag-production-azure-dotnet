using System.Net;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Zenya's error contract (D155 §5): 4xx bodies are RFC 7807 problem+json with a `title`;
// 401 for anything credential-related; 403 both for "not permitted" and instead of 404 when the
// user lacks read rights; 500 carries error_code + error_date to quote to Zenya support.
public class ZenyaApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? Title { get; }
    public string? ErrorCode { get; }

    public ZenyaApiException(HttpStatusCode statusCode, string? title, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Title      = title;
        ErrorCode  = errorCode;
    }
}

// The token endpoint refused the client_id/client_secret pair. Zenya's title discriminates:
// "invalid client assertion" means the registration is client_assertion type and takes no
// secret at all (measured 2026-08-28 and 2026-09-10 against the RefSync registration).
public sealed class ZenyaAuthenticationException : ZenyaApiException
{
    public ZenyaAuthenticationException(HttpStatusCode statusCode, string? title, string message)
        : base(statusCode, title, errorCode: null, message) { }
}

// /users/me answered as Zenya's built-in Anonymous account. Zenya permits unauthenticated API
// calls and answers them 200, so a dropped or rejected token looks like a working session
// against an empty corpus (D173 §2a, "the trap worth remembering"). Nothing may trust a
// document count until this has NOT been thrown.
public sealed class ZenyaAnonymousException : Exception
{
    public ZenyaAnonymousException()
        : base("Zenya answered /users/me as the Anonymous account: the token was not accepted. " +
               "Zenya returns 200 + Anonymous instead of 401 for a bad token. The server-side reason is in " +
               "Zenya's activity log (Activiteiten > Section=Zenya > Login > unsuccessful attempts).") { }
}
