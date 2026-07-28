using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class EmbeddingServiceTests
{
    private static IndexerConfig Config(int dims = 4) => new()
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
        OpenAiEmbeddingDimensions = dims,
    };

    private static ChunkStatsSource Document(string id, string content, string? summary = null) => new()
    {
        Id      = id,
        Content = content,
        Summary = summary,
    };

    private static float[][] Vectors(int count, int dims = 4) =>
        Enumerable.Range(0, count).Select(_ => new float[dims]).ToArray();

    private static Mock<IEmbeddingClient> MockEmbeddingClient() => new();

    private static EmbeddingService BuildService(Mock<IEmbeddingClient> embeddingClient, IndexerConfig? config = null) =>
        new(embeddingClient.Object, config ?? Config(), NullLogger<EmbeddingService>.Instance);

    [TestMethod]
    public async Task EmbedDocumentsAsync_AllDocumentsGetContentVectorSet()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "content one"), Document("d2", "content two") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
        Assert.AreEqual(0, result.VectorDimErrors);
        Assert.AreEqual(0, result.ChunksTruncated);
        Assert.AreEqual(0, result.EmbeddingRetries);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_WrongVectorDimensions_CountedAsDimError()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count, dims: 3), 0));
        var service = BuildService(embeddingClient, Config(dims: 4)); // expects 4, generator returns 3
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.VectorDimErrors);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_OversizedChunk_IsTruncatedBeforeEmbedding()
    {
        var oversized = new string('a', 25_000);
        IReadOnlyList<string>? capturedTexts = null;
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                capturedTexts = texts;
                return (Vectors(texts.Count), 0);
            });
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", oversized) };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.ChunksTruncated);
        Assert.IsNotNull(capturedTexts);
        Assert.AreEqual(24_000, capturedTexts![0].Length);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_SmallChunk_IsNotTruncated()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "short content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.ChunksTruncated);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_NoSummary_EmbeddingTextIsContentOnly()
    {
        IReadOnlyList<string>? capturedTexts = null;
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                capturedTexts = texts;
                return (Vectors(texts.Count), 0);
            });
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "My Title\n\nbody") };

        await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual("My Title\n\nbody", capturedTexts![0]);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_WithSummary_EmbeddingTextPrependsSummary()
    {
        IReadOnlyList<string>? capturedTexts = null;
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                capturedTexts = texts;
                return (Vectors(texts.Count), 0);
            });
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "body", summary: "A summary") };

        await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual("A summary\n\nbody", capturedTexts![0]);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_MoreThanOneBatch_SplitsIntoMultipleGenerateCalls()
    {
        var callSizes = new List<int>();
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                lock (callSizes) callSizes.Add(texts.Count);
                return (Vectors(texts.Count), 0);
            });
        var service = BuildService(embeddingClient);
        var docs = Enumerable.Range(0, 150).Select(i => Document($"d{i}", $"content {i}")).ToArray();

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(150, result.Documents.Count());
        Assert.AreEqual(2, callSizes.Count);
        CollectionAssert.AreEquivalent(new[] { 100, 50 }, callSizes);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_RetriesReportedByClient_AreSurfacedInResult()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 1));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.EmbeddingRetries);
        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_NoDocuments_ReturnsEmptyResultWithoutCallingGenerator()
    {
        var embeddingClient = MockEmbeddingClient();
        var service   = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([]);

        Assert.AreEqual(0, result.Documents.Count());
        embeddingClient.Verify(
            c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
