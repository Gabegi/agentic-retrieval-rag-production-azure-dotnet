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
        // Honours the document_id filter the expander sends (one fetch per document), as the
        // real index does. Returning every doc to every fetch labelled the neighbours of one
        // document with another's id whenever a fixture had two documents with neighbours
        // (2026-09-23, found by the 3b alignment test).
        var mock = new Mock<SearchClient>();
        mock.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, SearchOptions options, CancellationToken _) => Task.FromResult(SearchResponse(
                neighborDocs.Where(d => options.Filter is null || options.Filter.Contains($"document_id eq '{d["document_id"]}'")).ToArray())));
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
        Assert.IsTrue(result[0].ToContextText().Contains("page four content"));
        Assert.IsTrue(result[1].ToContextText().Contains("page five content"));
        Assert.IsTrue(result[2].ToContextText().Contains("page six content"));
    }

    [TestMethod]
    public async Task ExpandAsync_EmptyDocumentId_SkipsNeighborFetchForThatHit()
    {
        var searchClient = MockSearchClient();
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[] { Hit("chunk1", docId: "", page: 0, content: "orphan content") };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].ToContextText().Contains("orphan content"));
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
        Assert.IsTrue(result[0].ToContextText().Contains("page zero"));
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

        Assert.AreEqual("docB content", result[0].ToContextText());
        Assert.AreEqual("docA content", result[1].ToContextText());
    }

    [TestMethod]
    public async Task ExpandAsync_AChunkThatDoesNotFit_IsSkipped()
    {
        // MaxContextChars is 16_000 - three hits of 7500 chars each: the first two fit
        // (15,000 total), the third would push the running total to 22,500 and is skipped.
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

    [TestMethod]
    public async Task ExpandAsync_AChunkBehindAMisfit_StillArrivesWhenItFits()
    {
        // 7,500 + 7,500 = 15,000. The third 7,500 does not fit; the 500-char chunk behind it
        // does and must still arrive, in order. Before 2026-09-22 the loop stopped at the first
        // misfit and lost it (D211 §3.3 item 7).
        var expander = new ChunkNeighborExpander(MockSearchClient().Object);
        var big   = new string('x', 7_500);
        var small = new string('y', 500);
        var hits = new[]
        {
            Hit("d1_p0", "d1", page: 0, content: big),
            Hit("d2_p0", "d2", page: 0, content: big),
            Hit("d3_p0", "d3", page: 0, content: big),
            Hit("d4_p0", "d4", page: 0, content: small),
        };

        var result = await expander.ExpandAsync(hits);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(small, result[2].ToContextText());
    }

    // ── 3b: the context string is unchanged, and ids align with blocks (2026-09-23) ──────

    // The string-based composition exactly as ExpandAsync produced it before 2026-09-23 (D228
    // step 3b changed the return type to chunks). Kept here as the reference the new output is
    // held against: that string is what the judges read, so it may not shift by a byte.
    private static string LegacyContext(IReadOnlyList<RetrievedChunk> hits, IReadOnlyList<RetrievedChunk> neighbors)
    {
        var docRank = new Dictionary<string, int>();
        for (int i = 0; i < hits.Count; i++)
            if (!string.IsNullOrEmpty(hits[i].DocumentId) && !docRank.ContainsKey(hits[i].DocumentId))
                docRank[hits[i].DocumentId] = i;
        var seenIds  = hits.Select(h => h.Id).ToHashSet();
        var expanded = new List<RetrievedChunk>(hits);
        foreach (var chunk in neighbors)
            if (seenIds.Add(chunk.Id))
                expanded.Add(chunk);
        var ordered = expanded
            .GroupBy(c => c.DocumentId)
            .OrderBy(g => docRank.TryGetValue(g.Key, out var r) ? r : int.MaxValue)
            .SelectMany(g => g.OrderBy(c => c.Page).ThenBy(c => c.ChunkIndex));
        var chunks = new List<string>();
        int total  = 0;
        foreach (var chunk in ordered)
        {
            var text = chunk.ToContextText();
            if (total + text.Length > 16_000) continue;
            chunks.Add(text);
            total += text.Length;
        }
        return string.Join("\n\n---\n\n", chunks);
    }

    private static string Render(IReadOnlyList<RetrievedChunk> chunks) =>
        string.Join("\n\n---\n\n", chunks.Select(c => c.ToContextText()));   // AgenticRagQueryService's join

    [TestMethod]
    public async Task ExpandAsync_RenderedContext_IsByteIdenticalToTheStringBasedOutput_OnEveryFixture()
    {
        var big = new string('x', 7_500); var small = new string('y', 500);
        var fixtures = new (RetrievedChunk[] Hits, SearchDocument[] Neighbors)[]
        {
            ([Hit("doc1_p5_c0", "doc1", 5, content: "page five content")],
             [NeighborDoc("doc1_p4_c0", "doc1", 4, 0, "page four content"), NeighborDoc("doc1_p6_c0", "doc1", 6, 0, "page six content")]),
            ([Hit("chunk1", "", 0, content: "orphan content")], []),
            ([Hit("doc1_p0_c0", "doc1", 0, content: "page zero")], [NeighborDoc("doc1_p1_c0", "doc1", 1, 0, "   ")]),
            ([Hit("docB_p0", "docB", 0, content: "docB content"), Hit("docA_p0", "docA", 0, content: "docA content")], []),
            ([Hit("d1_p0", "d1", 0, content: big), Hit("d2_p0", "d2", 0, content: big), Hit("d3_p0", "d3", 0, content: big)], []),
            ([Hit("d1_p0", "d1", 0, content: big), Hit("d2_p0", "d2", 0, content: big), Hit("d3_p0", "d3", 0, content: big), Hit("d4_p0", "d4", 0, content: small)], []),
            // Two documents with neighbours, a titled hit, and a chunk_index tie-break.
            ([new RetrievedChunk("t1", "docT", 3, 1, "Titel", null, "titled body", DomainTag: "VVT"), Hit("docU_p1_c0", "docU", 1, content: "u one")],
             [NeighborDoc("docT_p3_c0", "docT", 3, 0, "before titled"), NeighborDoc("docT_p4_c0", "docT", 4, 0, "after titled"), NeighborDoc("docU_p2_c0", "docU", 2, 0, "u two")]),
        };

        foreach (var (hits, neighborDocs) in fixtures)
        {
            var expander = new ChunkNeighborExpander(MockSearchClient(neighborDocs).Object);
            var neighbors = neighborDocs.Where(d => !string.IsNullOrWhiteSpace((string)d["content"]))
                .Select(d => new RetrievedChunk((string)d["id"], (string)d["document_id"], (int)d["page_start"], (int)d["child_index"], null, null, (string)d["content"]))
                .ToList();

            var result = await expander.ExpandAsync(hits);

            Assert.AreEqual(LegacyContext(hits, neighbors), Render(result), $"context shifted for fixture starting with {hits[0].Id}");
        }
    }

    [TestMethod]
    public async Task ExpandAsync_OneChunkIsOneBlock_SoDocumentIdsAlignWithTheRenderedBlocks()
    {
        // A hit and its neighbours never merge: each admitted chunk renders as exactly one block,
        // so the id list (what the service records as ContextDocumentIds) is aligned with the
        // rendered string block for block, including a neighbour of a lower-ranked document.
        var searchClient = MockSearchClient(
            NeighborDoc("docA_p4", "docA", 4, 0, "A four"),
            NeighborDoc("docB_p2", "docB", 2, 0, "B two"));
        var expander = new ChunkNeighborExpander(searchClient.Object);
        var hits = new[] { Hit("docA_p5", "docA", 5, content: "A five"), Hit("docB_p1", "docB", 1, content: "B one") };

        var result = await expander.ExpandAsync(hits);

        var blocks = Render(result).Split("\n\n---\n\n");
        CollectionAssert.AreEqual(new[] { "A four", "A five", "B one", "B two" }, blocks);
        CollectionAssert.AreEqual(new[] { "docA", "docA", "docB", "docB" }, result.Select(c => c.DocumentId).ToList());
        Assert.AreEqual(blocks.Length, result.Count);
    }
}
