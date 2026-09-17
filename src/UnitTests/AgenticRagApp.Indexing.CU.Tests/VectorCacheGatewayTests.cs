using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// The run-level cache passes split out of EmbeddingService on 2026-09-16. EmbeddingServiceTests
// still covers the split/embed/write flow end to end; these pin the two things the gateway
// alone decides - what counts as a usable cached vector, and that the container existence check
// runs once per write pass rather than once per PUT (D197 action 1c).
[TestClass]
public class VectorCacheGatewayTests
{
    private const int Dims = 4;

    private static ChunkObject Document(string id) => new()
    {
        Content  = $"content for {id}",
        Metadata = new ChunkMetadata { Id = id },
    };

    private static Mock<IVectorCache> Cache()
    {
        var mock = new Mock<IVectorCache>();
        mock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.Setup(c => c.AssertContainerExistsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    private static EmbedChunkResult Fresh(ChunkObject doc, bool dimError = false, bool emptyVector = false)
    {
        doc.ContentVector = Enumerable.Repeat(1f, Dims).ToArray();
        return new EmbedChunkResult(doc, Truncated: false, DimError: dimError, EmptyVector: emptyVector);
    }

    [TestMethod]
    public async Task WriteFreshAsync_AssertsTheContainerOnce_ThenWritesEveryUsableVector()
    {
        var cache   = Cache();
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var results = new[] { Fresh(Document("a")), Fresh(Document("b")), Fresh(Document("c")) };

        await gateway.WriteFreshAsync(results, CancellationToken.None);

        cache.Verify(c => c.AssertContainerExistsAsync(It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task WriteFreshAsync_SkipsDimErrorAndEmptyVectors()
    {
        var cache   = Cache();
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var good    = Document("good");
        var results = new[]
        {
            Fresh(good),
            Fresh(Document("dim"),   dimError: true),
            Fresh(Document("empty"), emptyVector: true),
        };

        await gateway.WriteFreshAsync(results, CancellationToken.None);

        cache.Verify(c => c.SetAsync(good.ContentHash, It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Nothing to write means no round trip at all - not even the existence check. A run whose
    // every chunk was a cache hit should touch the container zero times on the write side.
    [TestMethod]
    public async Task WriteFreshAsync_NothingUsable_DoesNotTouchTheContainer()
    {
        var cache   = Cache();
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var results = new[] { Fresh(Document("dim"), dimError: true) };

        await gateway.WriteFreshAsync(results, CancellationToken.None);
        await gateway.WriteFreshAsync([], CancellationToken.None);

        cache.Verify(c => c.AssertContainerExistsAsync(It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SplitAsync_HealthyCachedVector_IsAHit_AndAssignedToTheDocument()
    {
        var cache = Cache();
        var hit   = Enumerable.Repeat(0.5f, Dims).ToArray();
        cache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(hit);
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var doc     = Document("a");

        var (cached, toEmbed, _) = await gateway.SplitAsync([doc], CancellationToken.None);

        Assert.AreEqual(1, cached.Count);
        Assert.AreEqual(0, toEmbed.Count);
        CollectionAssert.AreEqual(hit, doc.ContentVector);
    }

    // Three ways a cached entry is a miss even though the blob exists: absent, wrong width
    // (model/config changed since it was cached), and a vector that can never match anything.
    [TestMethod]
    public async Task SplitAsync_MissingWrongWidthOrEmptyVector_AreMisses()
    {
        var cache   = Cache();
        var absent  = Document("absent");
        var narrow  = Document("narrow");
        var zeroes  = Document("zeroes");
        var nan     = Document("nan");
        cache.Setup(c => c.TryGetAsync(absent.ContentHash, It.IsAny<CancellationToken>())).ReturnsAsync((float[]?)null);
        cache.Setup(c => c.TryGetAsync(narrow.ContentHash, It.IsAny<CancellationToken>())).ReturnsAsync(new float[Dims - 1]);
        cache.Setup(c => c.TryGetAsync(zeroes.ContentHash, It.IsAny<CancellationToken>())).ReturnsAsync(new float[Dims]);
        cache.Setup(c => c.TryGetAsync(nan.ContentHash,    It.IsAny<CancellationToken>())).ReturnsAsync([1f, float.NaN, 1f, 1f]);
        var gateway = new VectorCacheGateway(cache.Object, Dims);

        var (cached, toEmbed, _) = await gateway.SplitAsync([absent, narrow, zeroes, nan], CancellationToken.None);

        Assert.AreEqual(0, cached.Count);
        Assert.AreEqual(4, toEmbed.Count);
        Assert.IsTrue(toEmbed.All(d => d.ContentVector is null), "a rejected cached vector must not be left on the document");
    }

    // --- Operation counts (D197 action 4) ---
    //
    // These pin the count to the round trips actually made, which is the whole point of the
    // field: ms/op is read off CachePhaseMs × P / operations, and the ops term has already
    // changed basis once (action 1c made a PUT one round trip instead of two). A count derived
    // from chunk totals would have gone on reading the same across that change.

    [TestMethod]
    public async Task SplitAsync_CountsOneOperationPerProbe_HitOrMiss()
    {
        var cache = Cache();
        var hit   = Enumerable.Repeat(0.5f, Dims).ToArray();
        var found = Document("found");
        var miss  = Document("miss");
        cache.Setup(c => c.TryGetAsync(found.ContentHash, It.IsAny<CancellationToken>())).ReturnsAsync(hit);
        cache.Setup(c => c.TryGetAsync(miss.ContentHash,  It.IsAny<CancellationToken>())).ReturnsAsync((float[]?)null);
        var gateway = new VectorCacheGateway(cache.Object, Dims);

        var (cached, toEmbed, operations) = await gateway.SplitAsync([found, miss], CancellationToken.None);

        Assert.AreEqual(1, cached.Count);
        Assert.AreEqual(1, toEmbed.Count);
        Assert.AreEqual(2, operations, "a probe is a round trip whether or not it returns a usable vector");
    }

    [TestMethod]
    public async Task SplitAsync_NoDocuments_CountsNoOperations()
    {
        var gateway = new VectorCacheGateway(Cache().Object, Dims);

        var (_, _, operations) = await gateway.SplitAsync([], CancellationToken.None);

        Assert.AreEqual(0, operations);
    }

    // One existence check plus one PUT each - the shape action 1c produced. If SetAsync ever
    // goes back to creating the container per write this count has to go back to 2 per PUT,
    // and that is the change this assertion is here to force someone to make deliberately.
    [TestMethod]
    public async Task WriteFreshAsync_CountsTheContainerCheckPlusOnePerWrite()
    {
        var cache   = Cache();
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var results = new[] { Fresh(Document("a")), Fresh(Document("b")), Fresh(Document("c")) };

        var operations = await gateway.WriteFreshAsync(results, CancellationToken.None);

        Assert.AreEqual(4, operations, "1 AssertContainerExists + 3 SetAsync");
    }

    // Skipped vectors are not written, so they are not counted either.
    [TestMethod]
    public async Task WriteFreshAsync_CountsOnlyTheVectorsItActuallyWrote()
    {
        var cache   = Cache();
        var gateway = new VectorCacheGateway(cache.Object, Dims);
        var results = new[]
        {
            Fresh(Document("good")),
            Fresh(Document("dim"),   dimError: true),
            Fresh(Document("empty"), emptyVector: true),
        };

        var operations = await gateway.WriteFreshAsync(results, CancellationToken.None);

        Assert.AreEqual(2, operations, "1 AssertContainerExists + 1 SetAsync");
    }

    [TestMethod]
    public async Task WriteFreshAsync_NothingUsable_CountsNoOperations()
    {
        var gateway = new VectorCacheGateway(Cache().Object, Dims);

        Assert.AreEqual(0, await gateway.WriteFreshAsync([Fresh(Document("dim"), dimError: true)], CancellationToken.None));
        Assert.AreEqual(0, await gateway.WriteFreshAsync([], CancellationToken.None));
    }
}
