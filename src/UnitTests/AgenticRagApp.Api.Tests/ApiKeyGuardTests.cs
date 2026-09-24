using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Api.Security;

namespace RagApp.UnitTests.Api;

// Pins the interim shared-secret check (D238 §2). The filter itself is thin plumbing over
// ApiKeyGuard.IsAuthorized, so the decision is what gets tested here; the 401 body and the
// WWW-Authenticate header are part of the framework half QueryEndpointTests already documents
// as out of reach without running the app.
[TestClass]
public class ApiKeyGuardTests
{
    private const string Key   = "s3cr3t-token-value";
    private static ApiKeyGuard Guard() => new(Key);

    [TestMethod]
    public void Accepts_the_exact_bearer_token()
        => Assert.IsTrue(Guard().IsAuthorized($"Bearer {Key}"));

    [TestMethod]
    public void Rejects_a_wrong_token_of_the_same_length()
        => Assert.IsFalse(Guard().IsAuthorized("Bearer s3cr3t-token-valuX"));

    [TestMethod]
    public void Rejects_a_token_that_is_a_prefix_of_the_real_one()
        => Assert.IsFalse(Guard().IsAuthorized("Bearer s3cr3t"));

    [TestMethod]
    public void Rejects_a_token_that_extends_the_real_one()
        => Assert.IsFalse(Guard().IsAuthorized($"Bearer {Key}extra"));

    // The header is absent entirely - what an un-updated caller sends, and what the pipeline's
    // smoke step sent until the QUERY_API_KEY fetch was added to it.
    [TestMethod]
    public void Rejects_a_missing_header()
    {
        Assert.IsFalse(Guard().IsAuthorized(null));
        Assert.IsFalse(Guard().IsAuthorized(""));
    }

    // The right secret under the wrong scheme fails loudly rather than working by accident -
    // an API-key header and a bearer header are different contracts.
    [TestMethod]
    public void Rejects_the_right_token_under_another_scheme()
    {
        Assert.IsFalse(Guard().IsAuthorized(Key));
        Assert.IsFalse(Guard().IsAuthorized($"ApiKey {Key}"));
        Assert.IsFalse(Guard().IsAuthorized($"Basic {Key}"));
    }

    // "bearer" and "BEARER" are legal per RFC 9110, which matches schemes case-insensitively.
    // This implementation does not, and that is a deliberate narrowing recorded here so the
    // next person reads it as a decision rather than an oversight: every caller is issued the
    // header verbatim, and a case-insensitive match is one more shape to keep working.
    [TestMethod]
    public void Rejects_a_differently_cased_scheme()
        => Assert.IsFalse(Guard().IsAuthorized($"bearer {Key}"));

    // Whitespace is part of the token once the prefix is stripped, so a trailing space is a
    // different secret. Worth pinning: it is the classic copy-paste failure from a config UI.
    [TestMethod]
    public void Rejects_a_token_with_surrounding_whitespace()
    {
        Assert.IsFalse(Guard().IsAuthorized($"Bearer {Key} "));
        Assert.IsFalse(Guard().IsAuthorized($"Bearer  {Key}"));
    }

    // The app must not start without a key rather than default to serving unauthenticated;
    // Program.cs throws on a missing setting, and this covers the constructor behind it.
    [TestMethod]
    public void Refuses_to_construct_without_a_key()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ApiKeyGuard(""));
        Assert.ThrowsExactly<ArgumentException>(() => new ApiKeyGuard("   "));
        Assert.ThrowsExactly<ArgumentException>(() => new ApiKeyGuard(null!));
    }

    // Non-ASCII in the secret must survive the UTF-8 round trip - the generated token is
    // alphanumeric, but nothing stops an operator setting the app setting by hand.
    [TestMethod]
    public void Compares_non_ascii_tokens_by_their_utf8_bytes()
    {
        var guard = new ApiKeyGuard("wachtwoord-café-✓");
        Assert.IsTrue(guard.IsAuthorized("Bearer wachtwoord-café-✓"));
        Assert.IsFalse(guard.IsAuthorized("Bearer wachtwoord-cafe-✓"));
    }
}
