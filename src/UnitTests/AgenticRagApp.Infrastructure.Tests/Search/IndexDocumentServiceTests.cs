using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class IndexDocumentServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static (IndexDocumentService Service, Mock<SearchClient> Client, Mock<SearchIndexClient> IndexClient)
        BuildService()
    {
        var client      = new Mock<SearchClient>();
        var indexClient = new Mock<SearchIndexClient>();
        var service     = new IndexDocumentService(Config(), client.Object, indexClient.Object, NullLogger<IndexDocumentService>.Instance);

        return (service, client, indexClient);
    }

    private static Response<IndexDocumentsResult> UploadResponse(params (string Key, bool Succeeded)[] results)
    {
        var indexingResults = results.Select(r => SearchModelFactory.IndexingResult(r.Key, r.Succeeded ? null : "failed", r.Succeeded, r.Succeeded ? 200 : 400)).ToList();
        return Response.FromValue(SearchModelFactory.IndexDocumentsResult(indexingResults), Mock.Of<Response>());
    }

    private static SearchDocument DateDoc(string documentId, DateTimeOffset lastModified, string? id = null) => new()
    {
        ["id"]                 = id ?? documentId,
        ["document_id"]        = documentId,
        ["last_modified_date"] = lastModified,
    };

    private static SearchDocument IdDoc(string id) => new() { ["id"] = id };

    private static Response<SearchResults<SearchDocument>> SearchResponse(params SearchDocument[] docs)
    {
        var results = SearchModelFactory.SearchResults(
            values: docs.Select(d => SearchModelFactory.SearchResult(d, 0.0, null)).ToList(),
            totalCount: (long)docs.Length,
            facets: null,
            coverage: null,
            rawResponse: Mock.Of<Response>());
        return Response.FromValue(results, Mock.Of<Response>());
    }

    private sealed record FakeUploadChunk(string Id);

    [TestMethod]
    public async Task UpsertDocumentsAsync_CountsSucceededAndFailedFromResponse()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.UploadDocumentsAsync(It.IsAny<IEnumerable<FakeUploadChunk>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", false)));

        var (succeeded, failed) = await service.UpsertDocumentsAsync(new[] { new FakeUploadChunk("c1"), new FakeUploadChunk("c2") });

        Assert.AreEqual(1, succeeded);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_DeduplicatesByDocumentId_KeepingFirstOccurrence()
    {
        var (service, client, _) = BuildService();
        var first  = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var second = DateTimeOffset.Parse("2024-06-01T00:00:00Z");
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("doc1", first), DateDoc("doc1", second)));

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(first, result["doc1"]);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_KeyLookupIsCaseInsensitive()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("DOC1", DateTimeOffset.UtcNow)));

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.IsTrue(result.ContainsKey("doc1"));
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_FullFirstPage_FetchesNextPageViaKeysetFilter()
    {
        // Regression test for finding #4: a flat Size=1000 with no paging silently truncated
        // the index-state read. A full first page (== page size) must trigger a second,
        // keyset-filtered request ("id gt {lastSeenId}") - not $skip, which is capped at a
        // combined skip+top of 100,000 - and must not be treated as "that's everything."
        var (service, client, _) = BuildService();
        var firstPage  = Enumerable.Range(1, 1000)
            .Select(i => DateDoc($"doc{i}", DateTimeOffset.UtcNow, id: $"chunk{i:D4}"))
            .ToArray();
        var secondPage = new[] { DateDoc("doc1001", DateTimeOffset.UtcNow, id: "chunk1001") };

        client.Setup(c => c.SearchAsync<SearchDocument>("*", It.Is<SearchOptions>(o => o.Filter == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(firstPage));
        client.Setup(c => c.SearchAsync<SearchDocument>("*", It.Is<SearchOptions>(o => o.Filter == "id gt 'chunk1000'"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(secondPage));

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(1001, result.Count);
        Assert.IsTrue(result.ContainsKey("doc1001"));
        client.Verify(c => c.SearchAsync<SearchDocument>("*", It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_LastIdContainingSingleQuote_EscapesInNextPageFilter()
    {
        // OData filter syntax uses ' to delimit string literals - an unescaped ' in the id
        // would either break the filter or (worse) let it be misread as filter syntax.
        var (service, client, _) = BuildService();
        var firstPage  = Enumerable.Range(1, 1000)
            .Select(i => DateDoc($"doc{i}", DateTimeOffset.UtcNow, id: i == 1000 ? "chunk'1000" : $"chunk{i:D4}"))
            .ToArray();
        var secondPage = new[] { DateDoc("doc1001", DateTimeOffset.UtcNow, id: "chunk1001") };

        client.Setup(c => c.SearchAsync<SearchDocument>("*", It.Is<SearchOptions>(o => o.Filter == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(firstPage));
        client.Setup(c => c.SearchAsync<SearchDocument>("*", It.Is<SearchOptions>(o => o.Filter == "id gt 'chunk''1000'"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(secondPage));

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(1001, result.Count);
        Assert.IsTrue(result.ContainsKey("doc1001"));
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_ShortFirstPage_DoesNotFetchASecondPage()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("doc1", DateTimeOffset.UtcNow)));

        var result = await service.GetCurrentlyIndexedDocsIdsNDatesAsync();

        Assert.AreEqual(1, result.Count);
        client.Verify(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_NoDocumentIds_ReturnsEmptyWithoutSearching()
    {
        var (service, client, _) = BuildService();

        var result = await service.GetChunkIdsForDocumentsAsync([]);

        Assert.AreEqual(0, result.Count);
        client.Verify(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_ReturnsIdsFromSearchResults()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(IdDoc("chunk1"), IdDoc("chunk2")));

        var result = await service.GetChunkIdsForDocumentsAsync(["doc1"]);

        CollectionAssert.AreEquivalent(new[] { "chunk1", "chunk2" }, result.ToList());
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_FullFirstPage_FetchesNextPageViaKeysetFilter()
    {
        // Review finding #5's own bug, which had no test until now. A flat Size=1000 returned
        // the first 1,000 chunks of a document and reported success - so a 1,000+-chunk
        // document kept every stale row past the cap, with the wrong family_id or the previous
        // run's content, and nothing would ever revisit it. The paging is only load-bearing on
        // exactly this path: a full page must produce a second, keyset-filtered request.
        var (service, client, _) = BuildService();
        var firstPage  = Enumerable.Range(1, 1000).Select(i => IdDoc($"chunk{i:D4}")).ToArray();
        var secondPage = new[] { IdDoc("chunk1001") };

        client.Setup(c => c.SearchAsync<SearchDocument>("*",
                It.Is<SearchOptions>(o => o.Filter != null && !o.Filter.Contains("id gt")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(firstPage));
        client.Setup(c => c.SearchAsync<SearchDocument>("*",
                It.Is<SearchOptions>(o => o.Filter != null && o.Filter.Contains("id gt 'chunk1000'")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(secondPage));

        var result = await service.GetChunkIdsForDocumentsAsync(["doc1"]);

        Assert.AreEqual(1001, result.Count);
        CollectionAssert.Contains(result.ToList(), "chunk1001");
        client.Verify(c => c.SearchAsync<SearchDocument>("*", It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_ShortFirstPage_DoesNotFetchASecondPage()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(IdDoc("chunk1")));

        var result = await service.GetChunkIdsForDocumentsAsync(["doc1"]);

        Assert.AreEqual(1, result.Count);
        client.Verify(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_MoreThan50Documents_IsBatchedIntoSeparateFilters()
    {
        // The other half of the same method: document ids are chunked 50 at a time to keep the
        // OData filter length manageable, so 120 documents is three requests, not one.
        var (service, client, _) = BuildService();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(IdDoc("chunk1")));

        var docIds = Enumerable.Range(1, 120).Select(i => $"doc{i}").ToArray();
        await service.GetChunkIdsForDocumentsAsync(docIds);

        client.Verify(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
    [TestMethod]
    public async Task DeleteChunksByIdAsync_NoIds_ReturnsZeroWithoutCallingClient()
    {
        var (service, client, _) = BuildService();

        var result = await service.DeleteChunksByIdAsync([]);

        Assert.AreEqual(0, result);
        client.Verify(c => c.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_ReturnsCountOfIdsRequested()
    {
        var (service, client, _) = BuildService();
        client.Setup(c => c.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", true)));

        var result = await service.DeleteChunksByIdAsync(["c1", "c2"]);

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_ReturnsDocumentCountAndStorageSizeForConfiguredIndexName()
    {
        var (service, _, indexClient) = BuildService();
        indexClient.Setup(c => c.GetIndexStatisticsAsync("index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SearchModelFactory.SearchIndexStatistics(100, 2048), Mock.Of<Response>()));

        var (docCount, storageBytes) = await service.GetStatisticsAsync();

        Assert.AreEqual(100L, docCount);
        Assert.AreEqual(2048L, storageBytes);
    }
}
