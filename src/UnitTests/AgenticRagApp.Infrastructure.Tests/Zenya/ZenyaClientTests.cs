using System.Net;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// Hand-rolled against the v5 spec, so nothing but these pins the request shape (version header,
// Authorization scheme, envelope paging) or the response mapping (routing flags). The one
// behaviour worth being strict about is the Anonymous trap: a 200 from /users/me is NOT proof
// of authentication (D173 §2a).
[TestClass]
public class ZenyaClientTests
{
    private sealed class FixedTokenProvider : IZenyaTokenProvider
    {
        public ZenyaAccessToken Token { get; set; } = new("tok-1", "Bearer", DateTimeOffset.MaxValue);
        public int Calls { get; private set; }
        public ValueTask<ZenyaAccessToken> GetTokenAsync(CancellationToken ct = default)
        {
            Calls++;
            return new(Token);
        }
    }

    private static (ZenyaClient Client, ScriptedHandler Handler, FixedTokenProvider Tokens) Build(ScriptedHandler handler)
    {
        var tokens = new FixedTokenProvider();
        var http   = new HttpClient(handler) { BaseAddress = ZenyaTestData.BaseUrl };
        return (new ZenyaClient(http, tokens, NullLogger<ZenyaClient>.Instance), handler, tokens);
    }

    [TestMethod]
    public async Task GetCurrentUserAsync_SendsVersionHeaderAndTokenScheme()
    {
        var (client, handler, tokens) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.ServiceUserJson));
        tokens.Token = new("tok-9", "token", DateTimeOffset.MaxValue);

        var me = await client.GetCurrentUserAsync();

        var request = handler.Requests.Single();
        Assert.AreEqual("https://tenant.zenya.work/api/users/me", request.RequestUri!.ToString());
        Assert.AreEqual("5", request.Headers.GetValues("x-api-version").Single());
        Assert.AreEqual("token", request.Headers.Authorization!.Scheme);
        Assert.AreEqual("tok-9", request.Headers.Authorization!.Parameter);
        Assert.AreEqual("RAG_API_User", me.LoginCode);
        Assert.IsFalse(me.IsAnonymous);
    }

    [TestMethod]
    public async Task EnsureAuthenticatedAsync_AnonymousAccount_Throws()
    {
        var (client, _, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.AnonymousUserJson));

        await Assert.ThrowsExactlyAsync<ZenyaAnonymousException>(() => client.EnsureAuthenticatedAsync());
    }

    [TestMethod]
    public async Task EnsureAuthenticatedAsync_RealUser_ReturnsIt()
    {
        var (client, _, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.ServiceUserJson));

        var me = await client.EnsureAuthenticatedAsync();

        Assert.AreEqual("RAG_API_User", me.LoginCode);
    }

    [TestMethod]
    public async Task ListDocumentsAsync_WalksEnvelopePages_UntilShortPage()
    {
        // Two full pages of PageSize would need 2000 fixtures; the paging logic is the same at
        // any size, so this exercises it through the envelope's `returned` < limit rule with a
        // final short page, and asserts the offsets the client asked for.
        var docs = Enumerable.Range(1, ZenyaClient.PageSize).Select(i => ($"d{i}", 1)).ToArray();
        var handler = new ScriptedHandler()
            .EnqueueJson(ZenyaTestData.Page(0, ZenyaClient.PageSize, 1002, docs))
            .EnqueueJson(ZenyaTestData.Page(ZenyaClient.PageSize, ZenyaClient.PageSize, 1002, ("d1001", 2), ("d1002", 7)));
        var (client, _, _) = Build(handler);

        var all = new List<ZenyaDocumentListItem>();
        await foreach (var d in client.ListDocumentsAsync()) all.Add(d);

        Assert.AreEqual(1002, all.Count);
        Assert.AreEqual(2, handler.Requests.Count);
        var first  = handler.Requests[0].RequestUri!.Query;
        var second = handler.Requests[1].RequestUri!.Query;
        StringAssert.Contains(first, "limit=1000");
        StringAssert.Contains(first, "offset=0");
        StringAssert.Contains(first, "envelope=true");
        StringAssert.Contains(first, "include_total=true");
        StringAssert.Contains(second, "offset=1000");
        Assert.AreEqual(7, all.Last().Version);
    }

    [TestMethod]
    public async Task ListDocumentsAsync_TotalReached_StopsWithoutExtraCall()
    {
        // A page that is exactly full but already accounts for `total` must not trigger a third,
        // empty request.
        var docs = Enumerable.Range(1, ZenyaClient.PageSize).Select(i => ($"d{i}", 1)).ToArray();
        var handler = new ScriptedHandler()
            .EnqueueJson(ZenyaTestData.Page(0, ZenyaClient.PageSize, ZenyaClient.PageSize, docs));
        var (client, _, _) = Build(handler);

        var count = 0;
        await foreach (var _ in client.ListDocumentsAsync()) count++;

        Assert.AreEqual(ZenyaClient.PageSize, count);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ListDocumentsAsync_EmptyCorpus_YieldsNothing()
    {
        var (client, handler, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.Page(0, ZenyaClient.PageSize, 0)));

        var count = 0;
        await foreach (var _ in client.ListDocumentsAsync()) count++;

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ListDocumentsAsync_States_AreRepeatedQueryParameters()
    {
        var (client, handler, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.Page(0, ZenyaClient.PageSize, 0)));

        await foreach (var _ in client.ListDocumentsAsync(new[] { "published", "expired" })) { }

        var query = handler.Requests.Single().RequestUri!.Query;
        StringAssert.Contains(query, "states=published");
        StringAssert.Contains(query, "states=expired");
    }

    [TestMethod]
    public async Task GetDocumentAsync_MapsRoutingFlagsAndQuickCode()
    {
        var (client, handler, _) = Build(new ScriptedHandler().EnqueueJson(ZenyaTestData.PdfMetadataJson));

        var doc = await client.GetDocumentAsync("doc-1");

        Assert.AreEqual("https://tenant.zenya.work/api/documents/doc-1", handler.Requests.Single().RequestUri!.ToString());
        Assert.IsTrue(doc.CanDownloadBinary);
        Assert.IsFalse(doc.CanDownloadContent);
        Assert.AreEqual("application/pdf", doc.MimeType);
        Assert.AreEqual("pdf", doc.DownloadBinaryExtension);
        Assert.AreEqual("HYG-001", doc.QuickCode);
        Assert.AreEqual("file", doc.Type);
        Assert.AreEqual(3, doc.Version);
        Assert.AreEqual("20260315101500", doc.LastModifiedDateTime);
    }

    [TestMethod]
    public async Task DownloadAsync_StreamsVersionedRoute_WithContentType()
    {
        var handler = new ScriptedHandler().Enqueue(HttpStatusCode.OK, "%PDF-1.7 fake", "application/pdf");
        var (client, _, _) = Build(handler);

        await using var download = await client.DownloadAsync("doc-1", 3);

        Assert.AreEqual("https://tenant.zenya.work/api/documents/doc-1/v3/download", handler.Requests.Single().RequestUri!.ToString());
        Assert.AreEqual("application/pdf", download.ContentType);
        using var reader = new StreamReader(download.Content);
        Assert.AreEqual("%PDF-1.7 fake", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task GetContentsAsync_UsesVersionedTypedRoute()
    {
        var (client, handler, _) = Build(new ScriptedHandler().EnqueueJson(
            """{ "content": "<h1>Protocol</h1>", "schema_version": "2" }"""));

        var content = await client.GetContentsAsync("doc-2", 5);

        Assert.AreEqual("https://tenant.zenya.work/api/documents/doc-2/v5/contents", handler.Requests.Single().RequestUri!.ToString());
        Assert.AreEqual("<h1>Protocol</h1>", content.Content);
        Assert.AreEqual("2", content.SchemaVersion);
    }

    [TestMethod]
    public async Task Forbidden_SurfacesProblemTitle_AsZenyaApiException()
    {
        // 403 is also what Zenya returns instead of 404 when the user lacks read rights (D155 §5).
        var (client, _, _) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.Forbidden,
            """{ "title": "No read rights on this document", "status": 403 }""", "application/problem+json"));

        var ex = await Assert.ThrowsExactlyAsync<ZenyaApiException>(() => client.GetDocumentAsync("doc-1"));

        Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.AreEqual("No read rights on this document", ex.Title);
    }

    [TestMethod]
    public async Task ServerError_CarriesErrorCodeForZenyaSupport()
    {
        var (client, _, _) = Build(new ScriptedHandler().Enqueue(HttpStatusCode.InternalServerError,
            """{ "error_code": "E-12345", "error_date": "20260910093000" }""", "application/problem+json"));

        var ex = await Assert.ThrowsExactlyAsync<ZenyaApiException>(() => client.GetCurrentUserAsync());

        Assert.AreEqual("E-12345", ex.ErrorCode);
        StringAssert.Contains(ex.Message, "E-12345");
    }

    [TestMethod]
    public async Task EveryRequest_AsksTheTokenProvider()
    {
        // The provider owns caching; the client must not hold a token of its own, or a refresh
        // mid-crawl would never reach the wire.
        var (client, _, tokens) = Build(new ScriptedHandler()
            .EnqueueJson(ZenyaTestData.ServiceUserJson)
            .EnqueueJson(ZenyaTestData.PdfMetadataJson));

        await client.GetCurrentUserAsync();
        await client.GetDocumentAsync("doc-1");

        Assert.AreEqual(2, tokens.Calls);
    }
}
