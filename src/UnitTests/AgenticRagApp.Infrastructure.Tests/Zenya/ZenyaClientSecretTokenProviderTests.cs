using System.Net;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// The token leg is the part of the integration that has actually been measured against the
// live tenant (D173 §2a; pipeline runs 2026-09-10), so these pin the wire shape Zenya accepted
// and the refusal it gave. Caching is what lets a corpus crawl outlive a 600s token.
[TestClass]
public class ZenyaClientSecretTokenProviderTests
{
    private static ZenyaOptions Options(TimeSpan? skew = null) => new()
    {
        BaseUrl          = ZenyaTestData.BaseUrl,
        ClientId         = "11111111-2222-3333-4444-555555555555",
        ClientSecret     = "s3cret&with=form/chars",
        TokenRefreshSkew = skew ?? TimeSpan.FromMinutes(1),
    };

    private static (ZenyaClientSecretTokenProvider Provider, ScriptedHandler Handler, ManualTimeProvider Clock) Build(
        ScriptedHandler handler, ZenyaOptions? options = null)
    {
        var clock = new ManualTimeProvider();
        var http  = new HttpClient(handler) { BaseAddress = ZenyaTestData.BaseUrl };
        var provider = new ZenyaClientSecretTokenProvider(
            http, options ?? Options(), NullLogger<ZenyaClientSecretTokenProvider>.Instance, clock);
        return (provider, handler, clock);
    }

    [TestMethod]
    public async Task GetTokenAsync_PostsClientCredentialsForm_WithApiVersionHeader()
    {
        var (provider, handler, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        var token = await provider.GetTokenAsync();

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://tenant.zenya.work/api/oauth/token", request.RequestUri!.ToString());
        Assert.AreEqual("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.AreEqual("5", request.Headers.GetValues(ZenyaClient.ApiVersionHeader).Single());

        var body = handler.RequestBodies.Single()!;
        StringAssert.Contains(body, "grant_type=client_credentials");
        StringAssert.Contains(body, "client_id=11111111-2222-3333-4444-555555555555");
        // The secret must be form-encoded, never sent raw - '&' and '=' inside it would otherwise
        // split the body into extra fields.
        StringAssert.Contains(body, "client_secret=s3cret%26with%3Dform%2Fchars");

        Assert.AreEqual("tok-1", token.Token);
        Assert.AreEqual("Bearer", token.Scheme);
    }

    [TestMethod]
    public async Task GetTokenAsync_UsesTokenTypeFromResponse_AsScheme()
    {
        // Zenya's own customer doc shows "token" as the scheme; if a tenant returns that as
        // token_type, the client must follow it rather than hardcode Bearer (D175 route change).
        var (provider, _, _) = Build(new ScriptedHandler().EnqueueJson(
            """{ "access_token": "tok-x", "token_type": "token", "expires_in": 600 }"""));

        Assert.AreEqual("token", (await provider.GetTokenAsync()).Scheme);
    }

    [TestMethod]
    public async Task GetTokenAsync_ExpiresOn_IsRequestTimePlusExpiresIn()
    {
        var (provider, _, clock) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        var token = await provider.GetTokenAsync();

        Assert.AreEqual(clock.Now.AddSeconds(600), token.ExpiresOn);
    }

    [TestMethod]
    public async Task GetTokenAsync_SecondCallWithinLifetime_IsServedFromCache()
    {
        var (provider, handler, clock) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        var first = await provider.GetTokenAsync();
        clock.Now = clock.Now.AddMinutes(5);
        var second = await provider.GetTokenAsync();

        Assert.AreSame(first, second);
        Assert.AreEqual(1, handler.Requests.Count, "a valid cached token must not hit the token endpoint again");
    }

    [TestMethod]
    public async Task GetTokenAsync_InsideRefreshSkew_AcquiresFreshToken()
    {
        var handler = new ScriptedHandler()
            .EnqueueJson(ZenyaTestData.TokenJson)
            .EnqueueJson("""{ "access_token": "tok-2", "token_type": "Bearer", "expires_in": 600 }""");
        var (provider, _, clock) = Build(handler);

        await provider.GetTokenAsync();
        // 600s lifetime, 60s skew: at 9m30s the token is still technically valid but inside the
        // skew, so it must be refreshed before a request goes out with it.
        clock.Now = clock.Now.AddSeconds(570);
        var refreshed = await provider.GetTokenAsync();

        Assert.AreEqual("tok-2", refreshed.Token);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetTokenAsync_ConcurrentCallers_AcquireOnce()
    {
        var (provider, handler, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetTokenAsync().AsTask()));

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsTrue(tokens.All(t => t.Token == "tok-1"));
    }

    [TestMethod]
    public async Task GetTokenAsync_InvalidClientAssertion_ThrowsAuthenticationExceptionNamingTheCause()
    {
        // Verbatim body from the live tenant, 2026-08-28 and 2026-09-10: the RefSync registration
        // is client_assertion type and refuses a secret.
        var (provider, _, _) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.Unauthorized,
            """{ "title": "invalid client assertion", "status": 401, "error": "invalid_client", "error_description": "invalid client assertion" }""",
            "application/problem+json"));

        var ex = await Assert.ThrowsExactlyAsync<ZenyaAuthenticationException>(() => provider.GetTokenAsync().AsTask());

        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.AreEqual("invalid client assertion", ex.Title);
        StringAssert.Contains(ex.Message, "secret-type registration");
    }

    [TestMethod]
    public async Task GetTokenAsync_UnauthorizedWithoutBody_StillThrowsAuthenticationException()
    {
        // 401 is documented as detail-free (D155 §5); an empty body must not turn into a JSON
        // exception that hides the status.
        var (provider, _, _) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.Unauthorized, ""));

        var ex = await Assert.ThrowsExactlyAsync<ZenyaAuthenticationException>(() => provider.GetTokenAsync().AsTask());

        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.IsNull(ex.Title);
    }

    [TestMethod]
    public async Task GetTokenAsync_200WithoutAccessToken_Throws()
    {
        var (provider, _, _) = Build(new ScriptedHandler().EnqueueJson("""{ "token_type": "Bearer", "expires_in": 600 }"""));

        await Assert.ThrowsExactlyAsync<ZenyaAuthenticationException>(() => provider.GetTokenAsync().AsTask());
    }
}
