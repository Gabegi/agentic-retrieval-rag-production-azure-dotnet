using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

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

    // Id lives on the metadata now - ChunkObject.Id is a read-only pass-through onto it.
    private static ChunkObject Document(string id, string content) => new()
    {
        Content  = content,
        Metadata = new ChunkMetadata { Id = id },
    };

    // Non-zero on purpose: an all-zero vector is now a counted defect (EmptyVectors), so the
    // "healthy response" fixture has to look like one.
    private static float[][] Vectors(int count, int dims = 4) =>
        Enumerable.Range(0, count).Select(_ => Enumerable.Repeat(1f, dims).ToArray()).ToArray();

    private static Mock<IEmbeddingClient> MockEmbeddingClient() => new();

    // Always-miss by default, matching the pre-cache behavior every existing test below
    // already expects (every doc actually goes through the generator). Cache-specific
    // tests build their own mock with a real hit configured.
    private static Mock<IVectorCache> MockVectorCache()
    {
        var mock = new Mock<IVectorCache>();
        mock.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((float[]?)null);
        mock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    private static EmbeddingService BuildService(
        Mock<IEmbeddingClient> embeddingClient,
        IndexerConfig?    config      = null,
        Mock<IVectorCache>? vectorCache = null) =>
        new(embeddingClient.Object, (vectorCache ?? MockVectorCache()).Object, config ?? Config(), NullLogger<EmbeddingService>.Instance);

    [TestMethod]
    public async Task EmbedDocumentsAsync_AllDocumentsGetContentVectorSet()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "content one"), Document("d2", "content two") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
        Assert.AreEqual(0, result.VectorDimErrors);
        Assert.AreEqual(0, result.EmptyVectors);
        Assert.AreEqual(0, result.ChunksTruncated);
        Assert.AreEqual(0, result.EmbeddingRetries);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_WrongVectorDimensions_CountedAsDimError()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count, dims: 3), 0, 0, null));
        var service = BuildService(embeddingClient, Config(dims: 4)); // expects 4, generator returns 3
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.VectorDimErrors);
    }

    // The defect the dimension check cannot see: right length, no information. Search stores
    // it without complaint and it never scores against any query.
    [TestMethod]
    public async Task EmbedDocumentsAsync_AllZeroVector_CountedAsEmptyVector()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
                (texts.Select(_ => new float[4]).ToArray(), 0, 0, null));
        var service = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([Document("d1", "content")]);

        Assert.AreEqual(1, result.EmptyVectors);
        Assert.AreEqual(0, result.VectorDimErrors, "right length - this is not a dimension error");
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_NonFiniteComponent_CountedAsEmptyVector()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
                (texts.Select(_ => new[] { 0.1f, float.NaN, 0.3f, 0.4f }).ToArray(), 0, 0, null));
        var service = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([Document("d1", "content")]);

        Assert.AreEqual(1, result.EmptyVectors);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_WrongDimensionsAndAllZero_CountedOnce_AsDimError()
    {
        // One bad response is one defect. A zero vector of the wrong length is a dimension error
        // and must not also be counted as empty.
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
                (texts.Select(_ => new float[3]).ToArray(), 0, 0, null));
        var service = BuildService(embeddingClient, Config(dims: 4));

        var result = await service.EmbedDocumentsAsync([Document("d1", "content")]);

        Assert.AreEqual(1, result.VectorDimErrors);
        Assert.AreEqual(0, result.EmptyVectors);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_EmptyVector_IsNotWrittenToCache()
    {
        // Same rule as dimension errors: a result we already know is wrong must not become the
        // next run's cache hit.
        var vectorCache     = MockVectorCache();
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
                (texts.Select(_ => new float[4]).ToArray(), 0, 0, null));
        var service = BuildService(embeddingClient, vectorCache: vectorCache);

        await service.EmbedDocumentsAsync([Document("d1", "content")]);

        vectorCache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CachedEmptyVector_TreatedAsMissAndReEmbedded()
    {
        // A zero vector cached before this check existed would otherwise be trusted forever -
        // the cache is keyed by content hash and the chunk never changes.
        var vectorCache = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[4]);
        vectorCache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient, Config(dims: 4), vectorCache);

        var result = await service.EmbedDocumentsAsync([Document("d1", "content one")]);

        Assert.AreEqual(0, result.CacheHits);
        Assert.AreEqual(0, result.EmptyVectors, "the fresh vector is healthy; the cached one was only a miss");
        embeddingClient.Verify(
            c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
                return (Vectors(texts.Count), 0, 0, null);
            });
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", oversized) };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.ChunksTruncated);
        Assert.IsNotNull(capturedTexts);
        Assert.AreEqual(24_000, capturedTexts![0].Length);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_UnderTheCharacterLimitButOverTheTokenLimit_IsStillTruncated()
    {
        // The case the character guard alone lets through. The model's limit is in TOKENS, and
        // chars-per-token is not constant: prose runs ~3.1-3.3, table markdown ~1.9-2.8. Dense
        // text can therefore sit under 24,000 characters and still exceed 8,191 tokens, where it
        // would have been truncated by the API rather than by us - silently, and reported as
        // untruncated.
        var dense = string.Concat(Enumerable.Repeat("| ", 10_000));   // 20,000 chars, ~2 chars/token
        Assert.IsTrue(dense.Length < 24_000, "the fixture has to pass the character guard to be meaningful");

        IReadOnlyList<string>? capturedTexts = null;
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                capturedTexts = texts;
                return (Vectors(texts.Count), 0, 0, null);
            });
        var service = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([Document("d1", dense)]);

        Assert.AreEqual(1, result.ChunksTruncated);
        Assert.IsNotNull(capturedTexts);
        Assert.IsTrue(capturedTexts![0].Length < dense.Length, "it should have been cut");
        Assert.IsTrue(TokenCounter.Count(capturedTexts[0]) <= 8_191,
            "and cut far enough that what leaves for the API is within the model's input limit");
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_OneChunkTruncated_IsCountedOnce_NotOncePerGuard()
    {
        // A chunk long enough to trip the character cut and still dense enough to trip the token
        // cut afterwards must count as one truncation, not two.
        var dense = string.Concat(Enumerable.Repeat("| ", 20_000));   // 40,000 chars

        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([Document("d1", dense)]);

        Assert.AreEqual(1, result.ChunksTruncated);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_SmallChunk_IsNotTruncated()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "short content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.ChunksTruncated);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_EmbeddingTextIsContent()
    {
        // Title/Breadcrumb are already prepended into Content by ChunkingService before
        // EmbeddingService ever sees a chunk - EmbeddingText is just Content directly now
        // (no separate Summary fold-in - that field no longer exists).
        IReadOnlyList<string>? capturedTexts = null;
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                capturedTexts = texts;
                return (Vectors(texts.Count), 0, 0, null);
            });
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "My Title\n\nbody") };

        await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual("My Title\n\nbody", capturedTexts![0]);
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
                return (Vectors(texts.Count), 0, 0, null);
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
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 1, 0, null));
        var service = BuildService(embeddingClient);
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.EmbeddingRetries);
        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CacheHit_SkipsEmbeddingAndReusesVector()
    {
        var cachedVector = new float[] { 1, 2, 3, 4 };
        var vectorCache  = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(cachedVector);
        var embeddingClient = MockEmbeddingClient();
        var service   = BuildService(embeddingClient, vectorCache: vectorCache);
        var docs      = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.CacheHits);
        CollectionAssert.AreEqual(cachedVector, result.Documents.Single().ContentVector);
        embeddingClient.Verify(
            c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CacheHits_ReportTheTokensTheCacheSaved()
    {
        // CacheHits says how many chunks; CacheHitTokens says how much they would have billed -
        // the stored count of the exact text a fresh embed would have sent.
        var vectorCache = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[] { 1, 2, 3, 4 });
        var service = BuildService(MockEmbeddingClient(), vectorCache: vectorCache);
        var docs    = new[]
        {
            new ChunkObject { Content = "one", Metadata = new ChunkMetadata { Id = "d1", TokenCount = 120 } },
            new ChunkObject { Content = "two", Metadata = new ChunkMetadata { Id = "d2", TokenCount = 380 } },
        };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(2,    result.CacheHits);
        Assert.AreEqual(500L, result.CacheHitTokens);
        Assert.AreEqual(0L,   result.ApiPhaseMs, "no batch ran, so the API phase is zero wall time");
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_FreshChunks_SaveNoCacheTokens()
    {
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient);

        var result = await service.EmbedDocumentsAsync([Document("d1", "content")]);

        Assert.AreEqual(0L, result.CacheHitTokens);
        Assert.IsTrue(result.ApiPhaseMs >= 0 && result.CachePhaseMs >= 0);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CacheMiss_EmbedsAndWritesResultToCache()
    {
        var vectorCache = MockVectorCache();
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count), 0, 0, null));
        var service = BuildService(embeddingClient, vectorCache: vectorCache);
        var docs    = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.CacheHits);
        vectorCache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CachedVectorWrongDimensions_TreatedAsMissAndReEmbedded()
    {
        // Cached under an older embedding config (2 dims); current config expects 4 -
        // must not be trusted blindly, has to fall back to a real embedding call.
        var vectorCache = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[] { 1, 2 });
        vectorCache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var embeddingClient = MockEmbeddingClient();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) => (Vectors(texts.Count, dims: 4), 0, 0, null));
        var service = BuildService(embeddingClient, Config(dims: 4), vectorCache);
        var docs    = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.CacheHits);
        embeddingClient.Verify(
            c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
