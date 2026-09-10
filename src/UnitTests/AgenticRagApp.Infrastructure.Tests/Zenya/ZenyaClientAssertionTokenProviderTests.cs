using System.Net;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// The route Zenya chose (D175, 2026-09-10): leg 1 an Entra token for our identity, leg 2 that
// token to Zenya as client_assertion. Leg 1 is Azure.Identity's job and is stubbed; what these
// pin is leg 2's form body, the scope handed to Entra, and that Zenya's bare refusal comes back
// with the assertion-specific hint rather than the secret one.
[TestClass]
public class ZenyaClientAssertionTokenProviderTests
{
    private sealed class StubCredential : TokenCredential
    {
        public TokenRequestContext? LastContext { get; private set; }
        public string Token { get; set; } = "eyJ.entra.jwt";
        public Exception? Throws { get; set; }

        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            LastContext = context;
            if (Throws is not null) throw Throws;
            return new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            new(GetToken(context, ct));
    }

    private static ZenyaOptions Options() => new()
    {
        BaseUrl    = ZenyaTestData.BaseUrl,
        ClientId   = "cap-kennisbank-reg-id",
        EntraScope = "api://zenya-audience/.default",
    };

    private static (ZenyaClientAssertionTokenProvider Provider, ScriptedHandler Handler, StubCredential Credential) Build(ScriptedHandler handler)
    {
        var credential = new StubCredential();
        var http       = new HttpClient(handler) { BaseAddress = ZenyaTestData.BaseUrl };
        var provider   = new ZenyaClientAssertionTokenProvider(
            http, Options(), credential, NullLogger<ZenyaClientAssertionTokenProvider>.Instance, new ManualTimeProvider());
        return (provider, handler, credential);
    }

    [TestMethod]
    public async Task GetTokenAsync_RequestsEntraTokenForConfiguredScope_ThenPostsItAsClientAssertion()
    {
        var (provider, handler, credential) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        var token = await provider.GetTokenAsync();

        // Leg 1: the scope is the one Zenya specified (P2), nothing else.
        CollectionAssert.AreEqual(new[] { "api://zenya-audience/.default" }, credential.LastContext!.Value.Scopes);

        // Leg 2: RFC 7521/7523 form, no client_secret anywhere.
        var request = handler.Requests.Single();
        Assert.AreEqual("https://tenant.zenya.work/api/oauth/token", request.RequestUri!.ToString());
        Assert.AreEqual("5", request.Headers.GetValues(ZenyaClient.ApiVersionHeader).Single());
        var body = handler.RequestBodies.Single()!;
        StringAssert.Contains(body, "grant_type=client_credentials");
        StringAssert.Contains(body, "client_id=cap-kennisbank-reg-id");
        StringAssert.Contains(body, "client_assertion=eyJ.entra.jwt");
        StringAssert.Contains(body, "client_assertion_type=" + Uri.EscapeDataString(ZenyaClientAssertionTokenProvider.AssertionType));
        Assert.IsFalse(body.Contains("client_secret"), "assertion mode must never send a secret field");

        Assert.AreEqual("tok-1", token.Token);
        Assert.AreEqual("Bearer", token.Scheme);
    }

    [TestMethod]
    public async Task GetTokenAsync_CachedZenyaToken_DoesNotAskEntraAgain()
    {
        var (provider, handler, credential) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.TokenJson));

        await provider.GetTokenAsync();
        credential.Token = "eyJ.second.jwt";
        await provider.GetTokenAsync();

        // One Zenya call, and the second Entra token was never needed - the Zenya token is what
        // is cached, so Entra is only consulted when Zenya's token is refreshed.
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetTokenAsync_EntraFailure_PropagatesWithItsOwnMessage()
    {
        // Entra's AADSTS codes are the informative half of this flow (D175 flow 3 table); they
        // must reach the log intact rather than be wrapped as a Zenya refusal.
        var (provider, handler, credential) = Build(new ScriptedHandler());
        credential.Throws = new Azure.Identity.AuthenticationFailedException("AADSTS700016: Application not found in the directory");

        var ex = await Assert.ThrowsExactlyAsync<Azure.Identity.AuthenticationFailedException>(() => provider.GetTokenAsync().AsTask());

        StringAssert.Contains(ex.Message, "AADSTS700016");
        Assert.AreEqual(0, handler.Requests.Count, "leg 2 must not run without a leg-1 token");
    }

    [TestMethod]
    public async Task GetTokenAsync_ZenyaRefusesAssertion_HintPointsAtRegistrationIdsAndScope()
    {
        var (provider, _, _) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.Unauthorized,
            """{ "title": "invalid client assertion", "status": 401, "error": "invalid_client", "error_description": "invalid client assertion" }""",
            "application/problem+json"));

        var ex = await Assert.ThrowsExactlyAsync<ZenyaAuthenticationException>(() => provider.GetTokenAsync().AsTask());

        Assert.AreEqual("invalid client assertion", ex.Title);
        StringAssert.Contains(ex.Message, "client_assertion");
        StringAssert.Contains(ex.Message, "azure_tenant_id/azure_client_id");
    }

    [TestMethod]
    public void Constructor_WithoutScope_Throws()
    {
        var options = new ZenyaOptions { BaseUrl = ZenyaTestData.BaseUrl, ClientId = "x", ClientSecret = "s" };

        Assert.ThrowsExactly<ArgumentException>(() => new ZenyaClientAssertionTokenProvider(
            new HttpClient(new ScriptedHandler()), options, new StubCredential(),
            NullLogger<ZenyaClientAssertionTokenProvider>.Instance));
    }
}
