using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class RestoreServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed-deployment",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
        OpenAiEmbeddingModelName  = "text-embedding-3-large",
    };

    private static Mock<ISnapshotService> MockSnapshotService(IReadOnlyList<SnapshotChunk> chunks, string? instanceId)
    {
        var mock = new Mock<ISnapshotService>();
        mock.Setup(m => m.ReadLatestAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((chunks, instanceId));
        return mock;
    }

    private static Mock<IVectorCache> MockVectorCache(Dictionary<string, float[]>? vectorsByHash = null)
    {
        var mock = new Mock<IVectorCache>();
        mock.Setup(m => m.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string hash, CancellationToken _) =>
                vectorsByHash is not null && vectorsByHash.TryGetValue(hash, out var v) ? v : null);
        return mock;
    }

    private static Mock<IUploadService> MockUploadService(UploadResult? result = null)
    {
        var mock = new Mock<IUploadService>();
        mock.Setup(m => m.UploadDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? new UploadResult(0, 0, 0, null, null, []));
        return mock;
    }

    private static RestoreService BuildService(
        Mock<ISnapshotService> snapshotService, Mock<IVectorCache> vectorCache, Mock<IUploadService> uploadService) =>
        new(snapshotService.Object, vectorCache.Object, uploadService.Object, Config(), NullLogger<RestoreService>.Instance);

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_NoSnapshotExists_ReturnsZeroRestoredWithoutUploading()
    {
        var snapshotService = MockSnapshotService([], null);
        var vectorCache      = MockVectorCache();
        var uploadService    = MockUploadService();
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(0, result.ChunksRestored);
        Assert.IsNull(result.SnapshotInstanceId);
        uploadService.Verify(u => u.UploadDocumentsAsync(
            It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_ChunksWithCachedVectors_AreUploadedWithVectorsAttached()
    {
        var chunk = new SnapshotChunk("id1", "doc1.pdf", "Title", null, "content", null, 0, 0, "hash1");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(new() { ["hash1"] = [0.1f, 0.2f] });
        var uploadService    = MockUploadService(new UploadResult(1, 0, 0, 42, 1024, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        List<DocumentChunk>? uploaded = null;
        uploadService.Setup(u => u.UploadDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DocumentChunk>, IReadOnlyList<string>, CancellationToken>((docs, _, _) => uploaded = docs.ToList())
            .ReturnsAsync(new UploadResult(1, 0, 0, 42, 1024, []));

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual("instance-1", result.SnapshotInstanceId);
        Assert.AreEqual(1, result.ChunksRestored);
        Assert.AreEqual(0, result.ChunksFailed);
        Assert.AreEqual(0, result.ChunksMissingVector);
        Assert.AreEqual(42, result.IndexDocumentCountSnapshot);
        Assert.AreEqual("my-index", result.SearchIndexName);
        Assert.AreEqual("text-embedding-3-large", result.EmbeddingModel);

        Assert.IsNotNull(uploaded);
        Assert.AreEqual(1, uploaded!.Count);
        Assert.AreEqual("doc1.pdf", uploaded[0].DocumentId);
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f }, uploaded[0].ContentVector);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_ChunkWithNoCachedVector_IsCountedAsMissingButStillUploaded()
    {
        var chunk = new SnapshotChunk("id1", "doc1.pdf", "Title", null, "content", null, 0, 0, "hash-not-cached");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(); // empty - every lookup misses
        var uploadService    = MockUploadService(new UploadResult(1, 0, 0, null, null, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(1, result.ChunksMissingVector);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_UploadReportsFailures_PropagatesChunksFailed()
    {
        var chunk = new SnapshotChunk("id1", "doc1.pdf", "Title", null, "content", null, 0, 0, "hash1");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(new() { ["hash1"] = [0.1f, 0.2f] });
        var uploadService    = MockUploadService(new UploadResult(0, 1, 0, 0, 0, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(1, result.ChunksFailed);
    }
}
