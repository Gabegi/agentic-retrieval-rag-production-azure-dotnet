using System.Net;
using System.Text;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

// GetCurrentlyIndexedDocsIdsNDatesAsync against the REAL SDK deserialiser, fed the exact bytes the
// service returned (D200 §6h, 2026-09-17).
//
// Why this exists: runs 9/260917/4 and /5 read 0 documents from an index holding 3,740, and each
// re-extracted the whole corpus (871 pages) as a result. A probe from the app's own Kudu sandbox
// (src/Tools/ProbeIndexState.ps1) then returned 3,746 rows under both api-versions, every row
// carrying document_id and an ISO-8601 last_modified_date - so the query, the api-version pin,
// the identity and the index name are all cleared, and what is left is what this method does
// with those bytes. IndexDocumentServiceTests cannot see that: it builds SearchDocuments in-test
// with CLR-typed values, so the SDK's JSON reader never runs there.
//
// Same construction as SearchSerializationTests (D199 C1): a real SearchClient over a local
// HttpMessageHandler, no Azure.Core.TestFramework.
[TestClass]
public class IndexStateReadWireTests
{
    // Verbatim from the probe (9/260917/5, 2026-09-17), minus 3,743 rows. Three rows of one
    // document, which is also a short page against Size = 1000, so the read terminates.
    private const string ProbeBody = """
        {
          "@odata.context": "https://con-srch-cap-dev-we-001.search.windows.net/indexes('zenya-pdf-index')/$metadata#docs(*)",
          "@odata.count": 3746,
          "value": [
            {
              "@search.score": 1.0,
              "id": "MS4gSW5mb2thYXJ0IExHIC0gSG9lIGRvZSBpayBlZW4gUklFIChWZXJzaWUgMSkucGRmOjpzMDo6MA==",
              "document_id": "1. Infokaart LG - Hoe doe ik een RIE (Versie 1).pdf",
              "last_modified_date": "2026-08-06T13:46:46Z"
            },
            {
              "@search.score": 1.0,
              "id": "MS4gSW5mb2thYXJ0IExHIC0gSG9lIGRvZSBpayBlZW4gUklFIChWZXJzaWUgMSkucGRmOjpzMDo6MQ==",
              "document_id": "1. Infokaart LG - Hoe doe ik een RIE (Versie 1).pdf",
              "last_modified_date": "2026-08-06T13:46:46Z"
            },
            {
              "@search.score": 1.0,
              "id": "MS4gSW5mb2thYXJ0IExHIC0gSG9lIGRvZSBpayBlZW4gUklFIChWZXJzaWUgMSkucGRmOjpzMDo6Mg==",
              "document_id": "1. Infokaart LG - Hoe doe ik een RIE (Versie 1).pdf",
              "last_modified_date": "2026-08-06T13:46:46Z"
            }
          ]
        }
        """;

    // The third shape the wire can take for a selected DateTimeOffset field: present and null.
    // Distinct from an ABSENT key (which the in-test SearchDocuments of IndexDocumentServiceTests
    // model) because the SDK's GetDateTimeOffset treats the two differently - null for present
    // null, KeyNotFoundException for absent - and the rollup has to skip the row either way.
    private const string NullDateBody = """
        {
          "@odata.count": 1,
          "value": [
            {
              "@search.score": 1.0,
              "id": "chunk-1",
              "document_id": "dateless.pdf",
              "last_modified_date": null
            }
          ]
        }
        """;

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        public int Invocations { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Invocations++;
            LastUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (SearchClient Client, CannedHandler Handler) BuildClient(string body = ProbeBody)
    {
        var handler = new CannedHandler(body);
        var options = new SearchClientOptions(SearchServiceVersion.Current)
        {
            Transport = new HttpClientTransport(new HttpClient(handler)),
        };
        options.Retry.MaxRetries = 0;

        var client = new SearchClient(
            new Uri("https://search.example.com"), "zenya-pdf-index", new AzureKeyCredential("not-a-real-key"), options);
        return (client, handler);
    }

    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "zenya-pdf-index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    // The end-to-end claim: the bytes the service actually sent, through the real reader, through
    // the real rollup, come out as one document with the date the rows carry. If this fails, the
    // defect that billed 871 pages twice on 2026-09-17 is inside this method.
    [TestMethod]
    public async Task GetCurrentlyIndexedDocsIdsNDatesAsync_OnTheServicesActualBytes_ReturnsTheDocument()
    {
        var (client, handler) = BuildClient();
        var service = new IndexDocumentService(
            Config(), client, Mock.Of<SearchIndexClient>(), NullLogger<IndexDocumentService>.Instance);

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(1, handler.Invocations, "three rows against Size=1000 is a short page; exactly one request");
        Assert.AreEqual(1, result.Count,
            $"expected the one document the three rows belong to; request was {handler.LastUri} body {handler.LastRequestBody}");
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-06T13:46:46Z"),
            result["1. Infokaart LG - Hoe doe ik een RIE (Versie 1).pdf"]);
    }

    // A selected field that is null on the row arrives as a present JSON null. The row is dateless
    // and must be skipped (the document absent from the map, so the diff reprocesses it) rather
    // than throw or read as the oldest date.
    [TestMethod]
    public async Task GetCurrentlyIndexedDocsIdsNDatesAsync_PresentNullDateOnTheWire_SkipsTheRow()
    {
        var (client, _) = BuildClient(NullDateBody);
        var service = new IndexDocumentService(
            Config(), client, Mock.Of<SearchIndexClient>(), NullLogger<IndexDocumentService>.Instance);

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(0, result.Count, "a present-null date is a dateless row; the document must be absent");
    }

    // The mechanism under the claim above, isolated and pinned as MEASURED on 2026-09-17: the SDK
    // hands a DateTimeOffset field back as a System.String, and only its own GetDateTimeOffset
    // accessor turns it into a date. The rollup used to pattern-match `is DateTimeOffset` on the
    // raw value, which is why it read 0. This test asserts the string, not a wish - if a later
    // SDK starts returning DateTimeOffset, this fails on purpose and the comment above is what
    // needs revisiting, not the accessor.
    [TestMethod]
    public async Task SearchDocument_LastModifiedDateFromTheWire_ArrivesAsString_AndOnlyTheAccessorParsesIt()
    {
        var (client, _) = BuildClient();
        var options = new SearchOptions
        {
            Select  = { "id", "document_id", "last_modified_date" },
            OrderBy = { "id" },
            Size    = 1000,
        };

        var response = await client.SearchAsync<SearchDocument>("*", options);
        var rows = new List<SearchDocument>();
        await foreach (var r in response.Value.GetResultsAsync())
            rows.Add(r.Document);

        Assert.AreEqual(3, rows.Count, "the reader must yield every row in the canned page");

        var first = rows[0];
        Assert.IsTrue(first.TryGetValue("document_id", out var docId), "document_id missing from the SearchDocument");
        Assert.IsInstanceOfType(docId, typeof(string), $"document_id arrived as {docId?.GetType().FullName ?? "null"}");

        Assert.IsTrue(first.TryGetValue("last_modified_date", out var raw), "last_modified_date missing from the SearchDocument");
        Assert.IsInstanceOfType(raw, typeof(string),
            $"the SDK handed last_modified_date back as {raw?.GetType().FullName ?? "null"}; the raw-value type is what this test pins");
        Assert.IsFalse(raw is DateTimeOffset, "if this ever holds, the 2026-09-17 comment in IndexDocumentService is stale");

        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-06T13:46:46Z"),
            first.GetDateTimeOffset("last_modified_date"),
            "the accessor is the one thing that parses the wire string - it is what the rollup must use");
    }
}
