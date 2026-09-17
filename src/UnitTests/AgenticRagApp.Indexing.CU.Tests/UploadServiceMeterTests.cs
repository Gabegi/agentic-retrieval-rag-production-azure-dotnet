using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

// indexer.chunks_withheld is asserted here rather than left to run-time observation because on
// the RESTORE path it is the only telemetry a withhold produces at all: the run report's
// VectorDimErrors and EmptyVectors count what the embedder produced, and a restore embeds
// nothing - it resolves vectors from the cache. An unasserted counter is one that can quietly
// stop firing, and this one has no second source to notice that against.
//
// First MeterListener in the suite (2026-09-17). Kept in its own file so the collection plumbing
// does not get mixed into UploadServiceTests' behavioural cases.
//
// [DoNotParallelize] is load-bearing, not tidiness. Instrumentation.ChunksWithheld is a STATIC
// counter on a static Meter, so a listener here observes increments from every UploadService in
// the process, not just this test's. The suite runs sequentially today - there is no
// [assembly: Parallelize] and no .runsettings - so this costs nothing now and is the reason these
// tests will not start flaking the day someone turns parallelization on.
[TestClass]
[DoNotParallelize]
public class UploadServiceMeterTests
{
    private const int Dims = 4;

    private static ChunkObject Document(string id, string documentId, float[]? vector) => new()
    {
        Content       = "content",
        ContentVector = vector,
        Metadata      = new ChunkMetadata { Id = id, DocumentId = documentId },
    };

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
        OpenAiEmbeddingDimensions = Dims,
    };

    private static UploadService BuildService()
    {
        var indexService = new Mock<IIndexDocumentService>();
        indexService.Setup(m => m.UpsertDocumentsAsync(It.IsAny<IEnumerable<SearchUploadChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((1, 0, 1));
        indexService.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0L, 0L, (long?)null));

        var monitor = new Mock<IIndexStatsMonitor>();
        monitor.Setup(m => m.RecordAndCheckDriftAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexDriftCheck([], null, null));

        return new UploadService(indexService.Object, monitor.Object, NullLogger<UploadService>.Instance);
    }

    // Collects indexer.chunks_withheld measurements with their verdict tag for the duration of
    // one call. The meter is a static on Instrumentation, so this listens to the real instrument
    // rather than injecting one - which is the point: it pins the wiring, not a double.
    private static async Task<List<string>> CollectWithheldVerdictsAsync(Func<Task> action)
    {
        var verdicts = new List<string>();
        var found    = false;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name != "indexer.chunks_withheld") return;

            // The callback below is typed <long>, and MeterListener matches it against the
            // instrument's generic parameter EXACTLY - a Counter<int> would publish here, never
            // invoke the callback, and leave every assertion comparing an empty list to an empty
            // list. Asserting the type is what stops a change to the counter turning these tests
            // into three that cannot fail.
            Assert.IsInstanceOfType<Counter<long>>(
                instrument, "the measurement callback is typed <long> and must match the counter");

            found = true;
            l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "verdict" && tag.Value is string v)
                    for (var i = 0; i < measurement; i++) verdicts.Add(v);
        });
        listener.Start();

        await action();

        // Same reason: a renamed or deleted counter must fail here rather than quietly report
        // that nothing was withheld.
        Assert.IsTrue(found, "indexer.chunks_withheld was never published — the counter was renamed or removed");

        return verdicts;
    }

    [TestMethod]
    public async Task WithheldChunks_AreMeteredWithTheirVerdict()
    {
        var service = BuildService();

        var verdicts = await CollectWithheldVerdictsAsync(() => service.UploadDocumentsAsync(
            [
                Document("c1", "doc1", [0.1f, 0.2f]),                      // wrong width
                Document("c2", "doc1", [0f, 0f, 0f, 0f]),                  // all-zero
                Document("c3", "doc2", [0.1f, float.NaN, 0.3f, 0.4f]),     // non-finite
                Document("c4", "doc2", [0.1f, 0.2f, 0.3f, 0.4f]),          // healthy, not metered
            ],
            staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));

        CollectionAssert.AreEquivalent(
            new[] { "wrong_width", "empty", "non_finite" }, verdicts);
    }

    // The absent-vector case has no verdict - Classify throws on null and never returns one - so
    // it is tagged through VerdictTag's nullable overload rather than by inventing an enum member.
    [TestMethod]
    public async Task WithheldForAbsentVector_IsMeteredAsNoVector()
    {
        var service = BuildService();

        var verdicts = await CollectWithheldVerdictsAsync(() => service.UploadDocumentsAsync(
            [Document("c1", "doc1", null), Document("c2", "doc1", [0.1f, 0.2f, 0.3f, 0.4f])],
            staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));

        CollectionAssert.AreEquivalent(new[] { "no_vector" }, verdicts);
    }

    // The loud case must not also be the silent one. A total withhold throws, and the throw is
    // what a configuration drift looks like - so the metering has to have happened already. If
    // the guard is ever moved back above LogWithheld, a drift takes the run down having recorded
    // nothing at all, which is the opposite of what the guard is for.
    [TestMethod]
    public async Task TotalWithhold_IsMeteredBeforeTheGuardThrows()
    {
        var service = BuildService();

        var verdicts = await CollectWithheldVerdictsAsync(async () =>
            await Assert.ThrowsExactlyAsync<TotalWithholdException>(
                () => service.UploadDocumentsAsync(
                    [Document("c1", "doc1", [0.1f, 0.2f]), Document("c2", "doc1", [0.3f])],
                    staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims)));

        CollectionAssert.AreEquivalent(new[] { "wrong_width", "wrong_width" }, verdicts);
    }
}
