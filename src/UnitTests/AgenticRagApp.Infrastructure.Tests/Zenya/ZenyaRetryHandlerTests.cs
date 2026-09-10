using System.Net;
using System.Net.Http.Headers;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// Zenya documents neither its limits nor its retry headers (D155 §5), so the handler's job is
// to survive a 429 without hiding it: retry within bounds, honour Retry-After when it appears,
// count every throttle for A8, and hand the last 429 back rather than loop forever.
[TestClass]
public class ZenyaRetryHandlerTests
{
    private static ZenyaOptions Options(int maxRetries) => new()
    {
        BaseUrl        = ZenyaTestData.BaseUrl,
        ClientId       = "id",
        ClientSecret   = "secret",
        MaxRetries     = maxRetries,
        RetryBaseDelay = TimeSpan.Zero, // tests must not sleep; the delay arithmetic is asserted separately
    };

    private static (HttpClient Http, ZenyaRetryHandler Retry, ScriptedHandler Inner) Build(ScriptedHandler inner, int maxRetries = 3)
    {
        var retry = new ZenyaRetryHandler(Options(maxRetries), NullLogger<ZenyaRetryHandler>.Instance) { InnerHandler = inner };
        return (new HttpClient(retry) { BaseAddress = ZenyaTestData.BaseUrl }, retry, inner);
    }

    [TestMethod]
    public async Task NonThrottledResponse_PassesThroughUntouched()
    {
        var (http, retry, inner) = Build(new ScriptedHandler().EnqueueJson("{}"));

        var response = await http.GetAsync("documents");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, inner.Requests.Count);
        Assert.AreEqual(0, retry.ThrottledResponses);
    }

    [TestMethod]
    public async Task TooManyRequests_ThenSuccess_RetriesAndCounts()
    {
        var (http, retry, inner) = Build(new ScriptedHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "", configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero))
            .Enqueue(HttpStatusCode.TooManyRequests, "", configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero))
            .EnqueueJson("{}"));

        var response = await http.GetAsync("documents");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, inner.Requests.Count);
        Assert.AreEqual(2, retry.ThrottledResponses);
    }

    [TestMethod]
    public async Task TooManyRequests_BeyondMaxRetries_ReturnsTheLast429()
    {
        var inner = new ScriptedHandler();
        for (var i = 0; i < 3; i++) inner.Enqueue(HttpStatusCode.TooManyRequests, "");
        var (http, retry, _) = Build(inner, maxRetries: 2);

        var response = await http.GetAsync("documents");

        // 1 original + 2 retries = 3 attempts, then the 429 is returned for the caller to surface.
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.AreEqual(3, inner.Requests.Count);
        Assert.AreEqual(3, retry.ThrottledResponses);
    }

    [TestMethod]
    public async Task OtherErrors_AreNotRetried()
    {
        // A 5xx carries an error_code to quote to Zenya support (D155 §5); retrying would hide it.
        var (http, _, inner) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.InternalServerError, ""));

        var response = await http.GetAsync("documents");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(1, inner.Requests.Count);
    }
}
