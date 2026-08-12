using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Moq;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class ChunkNeighborExpanderTests
{
    private static RetrievedChunk Hit(string id, string docId, int page, int chunkIndex = 0, string content = "content") =>
        new(id, docId, page, chunkIndex, Title: null, Summary: null, Content: content);

    private static SearchDocument NeighborDoc(string id, string docId, int page, int chunkIndex, string content) => new()
    {
        ["id"]           = id,
        ["document_id"]  = docId,
        ["content"]      = content,
        ["page_start"]   = page,
        ["child_index"]  = chunkIndex,
    };

    private static Response<SearchResults<SearchDocument>> SearchResponse(params SearchDocument[] docs)
    {
        var results = SearchModelFactory.SearchResults(
            values: docs.Select(d => SearchModelFactory.SearchResult(d, 0.0, null)).ToList(),
            totalCount: docs.Length,
            facets: null,
            coverage: null,
            rawResponse: Mock.Of<Response>());
        return Response.FromValue(results, Mock.Of<Response>());
    }

    private static Mock<SearchClient> MockSearchClient(params SearchDocument[] neighborDocs)
    {
        var mock = new Mock<SearchClient>();
        mock.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(neighborDocs));
        return mock;
    }

    [TestMethod]
    public async Task ExpandAsync_NoHits_ReturnsEmpty()
    {
        var expander = new ChunkNeighborExpander(MockSearchClient().Object);

        var result = await expander.ExpandAsync([]);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ExpandAsync_SinglePageHit_FetchesPreviousAndNextPageNeighbors()
    {
        var searchClient = MockSearchClient(
            NeighborDoc("doc1_p4_c0", "doc1", 4, 0, "page four content"),
            NeighborDoc("doc1_p6_c0", "doc1", 6, 0, "page six content"));
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[] { Hit("doc1_p5_c0", "doc1", page: 5, content: "page five content") };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(3, result.Count);
        // Reading order: page 4, then 5 (original hit), then 6.
        Assert.IsTrue(result[0].Contains("page four content"));
        Assert.IsTrue(result[1].Contains("page five content"));
        Assert.IsTrue(result[2].Contains("page six content"));
    }

    [TestMethod]
    public async Task ExpandAsync_EmptyDocumentId_SkipsNeighborFetchForThatHit()
    {
        var searchClient = MockSearchClient();
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[] { Hit("chunk1", docId: "", page: 0, content: "orphan content") };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].Contains("orphan content"));
    }

    [TestMethod]
    public async Task ExpandAsync_NeighborWithBlankContent_IsSkipped()
    {
        var searchClient = MockSearchClient(
            NeighborDoc("doc1_p1_c0", "doc1", 1, 0, "   "));
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[] { Hit("doc1_p0_c0", "doc1", page: 0, content: "page zero") };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].Contains("page zero"));
    }

    [TestMethod]
    public async Task ExpandAsync_MultipleDocuments_PreservesRelevanceOrderOfFirstHitPerDocument()
    {
        var searchClient = MockSearchClient(); // no neighbors needed - hits already adjacent
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[]
        {
            Hit("docB_p0", "docB", page: 0, content: "docB content"),
            Hit("docA_p0", "docA", page: 0, content: "docA content"),
        };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual("docB content", result[0]);
        Assert.AreEqual("docA content", result[1]);
    }

    [TestMethod]
    public async Task ExpandAsync_TotalContextExceedsCap_StopsAddingFurtherChunks()
    {
        // MaxContextChars is 16_000 - three hits of 7500 chars each: the first two fit
        // (15,000 total) but adding the third would push the running total to 22,500.
        var searchClient = MockSearchClient();
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var big = new string('x', 7_500);
        var hits = new[]
        {
            Hit("d1_p0", "d1", page: 0, content: big),
            Hit("d2_p0", "d2", page: 0, content: big),
            Hit("d3_p0", "d3", page: 0, content: big),
        };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(2, result.Count);
    }
}
