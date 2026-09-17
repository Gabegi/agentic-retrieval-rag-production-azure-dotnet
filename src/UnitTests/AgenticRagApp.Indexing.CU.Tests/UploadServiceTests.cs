using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class UploadServiceTests
{
    // Id lives on the metadata now - ChunkObject.Id is a read-only pass-through onto it.
    //
    // Carries a healthy vector by default (2026-09-17): since A1, a chunk with no vector is
    // WITHHELD on the indexing path, so a vectorless helper would silently make every test here
    // a test of the withhold. Tests that want an unhealthy or absent vector say so explicitly.
    private static ChunkObject Document(string id, float[]? vector = null) => new()
    {
        Content       = "content",
        ContentVector = vector ?? [0.1f, 0.2f, 0.3f, 0.4f],
        Metadata      = new ChunkMetadata { Id = id },
    };

    // A chunk whose vector the cache could not resolve - what RestoreService uploads on purpose.
    private static ChunkObject VectorlessDocument(string id) => new()
    {
        Content       = "content",
        ContentVector = null,
        Metadata      = new ChunkMetadata { Id = id },
    };

    private static Mock<IIndexDocumentService> MockIndexDocumentService(
        int succeeded, int failed,
        IReadOnlyList<string>? existingChunkIds = null,
        int deletedCount = 0,
        (long DocCount, long StorageBytes, long? VectorBytes)? stats = null,
        Exception? statsException = null)
    {
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.UpsertDocumentsAsync(It.IsAny<IEnumerable<SearchUploadChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((succeeded, failed, 1));
        mock.Setup(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingChunkIds ?? []);
        mock.Setup(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        if (statsException is not null)
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(statsException);
        else
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stats ?? (0L, 0L, (long?)null));

        return mock;
    }

    private static Mock<IIndexStatsMonitor> MockIndexStatsMonitor(
        IReadOnlyList<string>? driftRedFlags = null,
        long? previousDocumentCount = null,
        long? previousStorageSizeBytes = null)
    {
        var mock = new Mock<IIndexStatsMonitor>();
        mock.Setup(m => m.RecordAndCheckDriftAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexDriftCheck(driftRedFlags ?? [], previousDocumentCount, previousStorageSizeBytes));
        return mock;
    }

    // Dims is the width every vector in these tests is built at, so the default config makes
    // them all publishable unless a test deliberately does otherwise.
    private const int Dims = 4;

    private static IndexerConfig Config(int dimensions = Dims) => new()
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
        OpenAiEmbeddingDimensions = dimensions,
    };

    private static UploadService BuildService(
        Mock<IIndexDocumentService> indexDocumentService,
        Mock<IIndexStatsMonitor>? indexStatsMonitor = null) =>
        new(indexDocumentService.Object, (indexStatsMonitor ?? MockIndexStatsMonitor()).Object,
            NullLogger<UploadService>.Instance);

    [TestMethod]
    public async Task UploadDocumentsAsync_ReturnsSucceededAndFailedCountsFromIndexService()
    {
        var indexService = MockIndexDocumentService(succeeded: 3, failed: 1);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(3, result.DocsUploaded);
        Assert.AreEqual(1, result.DocsFailed);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NoStaleDocuments_SkipsCleanupEntirely()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_OrphanedChunks_AreDeleted()
    {
        // doc1's old chunk ids: c1 (re-uploaded, keep) and c2 (no longer produced, orphaned).
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1", "c2"],
            deletedCount: 1);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(1, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "c2" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_AllOldChunksWereReuploaded_NoDeleteCallMade()
    {
        // Every previously-existing chunk id for the stale doc is among what was just uploaded.
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1"]);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Family moves ─────────────────────────────────────────────────────────
    // A document is re-homed into a different family because OTHER documents changed the
    // clustering. Its own bytes are unchanged, so extraction skipped it, it never reached
    // chunking, and nothing in `documents` belongs to it - while its indexed rows still carry the
    // family_id it had before. These pin that the rows get patched rather than re-indexed.

    private static Mock<IIndexDocumentService> MockForFamilyMoves(
        IReadOnlyList<string> chunkIdsInIndex, (int Succeeded, int Failed)? mergeResult = null)
    {
        var mock = MockIndexDocumentService(succeeded: 1, failed: 0);
        mock.Setup(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunkIdsInIndex);
        mock.Setup(m => m.MergeDocumentFieldsAsync(It.IsAny<IEnumerable<ChunkFamilyPatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mergeResult ?? (chunkIdsInIndex.Count, 0));
        return mock;
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NoFamilyMoves_MakesNoMergeCall()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.ChunkFamiliesPatched);
        indexService.Verify(m => m.MergeDocumentFieldsAsync(
            It.IsAny<IEnumerable<ChunkFamilyPatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_MovedDocument_PatchesItsIndexedRowsWithTheNewFamilyId()
    {
        var indexService = MockForFamilyMoves(["moved::s0::0", "moved::s1::0"]);
        var service      = BuildService(indexService);
        var moves        = new[] { new FamilyMove("moved.pdf", "fam-OLD", "fam-NEW") };

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves, indexVectorDimensions: Dims);

        Assert.AreEqual(2, result.ChunkFamiliesPatched);
        indexService.Verify(m => m.MergeDocumentFieldsAsync(
            It.Is<IEnumerable<ChunkFamilyPatch>>(p =>
                p.Count() == 2 &&
                p.All(x => x.FamilyId == "fam-NEW") &&
                p.Select(x => x.Id).SequenceEqual(new[] { "moved::s0::0", "moved::s1::0" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_MovedDocumentThatWasAlsoUploaded_IsNotPatchedAgain()
    {
        // Its rows already carry the new family_id from the projection, so patching would be a
        // second write saying the same thing.
        var indexService = MockForFamilyMoves(["c1"]);
        var service      = BuildService(indexService);
        var moves        = new[] { new FamilyMove("doc1", "fam-OLD", "fam-NEW") };

        // Document("c1")'s DocumentId comes off its metadata - matching the moved SourceId. The
        // vector matters only in that it has to be publishable: a withheld chunk would not be in
        // the upload at all, which is a different test.
        var uploaded = new ChunkObject
        {
            Content       = "content",
            ContentVector = [0.1f, 0.2f, 0.3f, 0.4f],
            Metadata      = new ChunkMetadata { Id = "c1", DocumentId = "doc1" },
        };

        var result = await service.UploadDocumentsAsync([uploaded], staleDocumentIds: [], familyMoves: moves, indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.ChunkFamiliesPatched);
        indexService.Verify(m => m.MergeDocumentFieldsAsync(
            It.IsAny<IEnumerable<ChunkFamilyPatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_MovedDocumentWithNoRowsInTheIndex_PatchesNothing()
    {
        var indexService = MockForFamilyMoves([]);
        var service      = BuildService(indexService);
        var moves        = new[] { new FamilyMove("ghost.pdf", "fam-OLD", "fam-NEW") };

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves, indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.ChunkFamiliesPatched);
        indexService.Verify(m => m.MergeDocumentFieldsAsync(
            It.IsAny<IEnumerable<ChunkFamilyPatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_TwoMovedDocuments_EachGetsItsOwnFamilyId()
    {
        // The pairing that matters: ids are fetched per document, so a row can never be given
        // another document's new family.
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        indexService
            .Setup(m => m.GetChunkIdsForDocumentsAsync(
                It.Is<IEnumerable<string>>(ids => ids.Contains("a.pdf")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["a::s0::0"]);
        indexService
            .Setup(m => m.GetChunkIdsForDocumentsAsync(
                It.Is<IEnumerable<string>>(ids => ids.Contains("b.pdf")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["b::s0::0"]);
        indexService
            .Setup(m => m.MergeDocumentFieldsAsync(It.IsAny<IEnumerable<ChunkFamilyPatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, 0));

        var service = BuildService(indexService);
        var moves   = new[]
        {
            new FamilyMove("a.pdf", "old", "fam-A"),
            new FamilyMove("b.pdf", "old", "fam-B"),
        };

        await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves, indexVectorDimensions: Dims);

        indexService.Verify(m => m.MergeDocumentFieldsAsync(
            It.Is<IEnumerable<ChunkFamilyPatch>>(p =>
                p.Single(x => x.Id == "a::s0::0").FamilyId == "fam-A" &&
                p.Single(x => x.Id == "b::s0::0").FamilyId == "fam-B"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_FamilyPatchPartlyFails_ReportsOnlyWhatSucceeded()
    {
        var indexService = MockForFamilyMoves(["c1", "c2"], mergeResult: (1, 1));
        var service      = BuildService(indexService);
        var moves        = new[] { new FamilyMove("moved.pdf", "fam-OLD", "fam-NEW") };

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves, indexVectorDimensions: Dims);

        Assert.AreEqual(1, result.ChunkFamiliesPatched);
        Assert.AreEqual(1, result.DocsUploaded, "a failed patch does not fail the upload it followed");
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotSucceeds_PopulatesSnapshotAndRedFlags()
    {
        var indexService     = MockIndexDocumentService(succeeded: 1, failed: 0, stats: (100L, 2048L, 1024L));
        var indexStatsMonitor = MockIndexStatsMonitor(driftRedFlags: ["index_doc_count_drift:+50.0% (50 -> 100)"]);
        var service = BuildService(indexService, indexStatsMonitor);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(100L, result.IndexDocumentCountSnapshot);
        Assert.AreEqual(2048L, result.IndexStorageSizeBytesSnapshot);
        CollectionAssert.Contains(result.RedFlags.ToList(), "index_doc_count_drift:+50.0% (50 -> 100)");
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotFails_UploadResultStillReturnedWithNullSnapshot()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 5, failed: 0,
            statsException: new InvalidOperationException("search unavailable"));
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(5, result.DocsUploaded);
        Assert.IsNull(result.IndexDocumentCountSnapshot);
        Assert.IsNull(result.IndexStorageSizeBytesSnapshot);
        Assert.AreEqual(0, result.RedFlags.Count);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotCancelled_ExceptionPropagates()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            statsException: new OperationCanceledException());
        var service = BuildService(indexService);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));
    }

    // ── Withholding unhealthy vectors (D199 A1) ──────────────────────────────

    private static float[] WrongWidth  => [0.1f, 0.2f];
    private static float[] AllZero     => [0f, 0f, 0f, 0f];
    private static float[] NonFinite   => [0.1f, float.NaN, 0.3f, 0.4f];

    // THE test that must never be deleted.
    //
    // It fails the moment someone builds the orphan-protection set from the chunks actually
    // uploaded rather than from every chunk this run accounted for. That change looks like a
    // tidy-up and is the one edit in UploadService that makes the pipeline destroy working
    // content: IndexDiffService puts every updated document into staleDocumentIds, so a withheld
    // chunk's id missing from the protected set would delete the previous GOOD row at that id -
    // strictly worse than the dead row the withhold exists to prevent.
    [TestMethod]
    public async Task UploadDocumentsAsync_WithheldChunkId_IsStillProtectedFromOrphanDelete()
    {
        // c1 is withheld, c2 uploads. Both already exist in the index for the stale document, so
        // neither is orphaned - and c1's survival is the point.
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0, existingChunkIds: ["c1", "c2"]);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [Document("c1", AllZero), Document("c2")], staleDocumentIds: ["doc1"], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(1, result.DocsWithheld);
        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The §2.5 carve-out, named after what depends on it. This pins the ORDERING as much as the
    // outcome: Classify throws on null, so the absent-vector branch has to run before it. If the
    // two were ever reordered this test fails with ArgumentNullException rather than an
    // assertion, which is still the right answer.
    [TestMethod]
    public async Task UploadDocumentsAsync_NullVector_IsUploaded_ForRestore()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [VectorlessDocument("c1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims, allowVectorless: true);

        Assert.AreEqual(0, result.DocsWithheld);
        Assert.AreEqual(1, result.DocsUploaded);
    }

    // The inverse: allowVectorless exempts an ABSENT vector only. A restored chunk that carries a
    // vector is judged like any other, so a cached vector that outlived a dimension change is
    // still withheld on the restore path.
    [TestMethod]
    public async Task UploadDocumentsAsync_AllowVectorless_StillWithholdsAnUnhealthyVector()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [VectorlessDocument("c1"), Document("c2", WrongWidth), Document("c3")],
            staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims, allowVectorless: true);

        Assert.AreEqual(1, result.DocsWithheld);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NullVectorOnTheIndexingPath_IsWithheld()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [VectorlessDocument("c1"), Document("c2")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(1, result.DocsWithheld);
    }

    [TestMethod]
    [DynamicData(nameof(UnhealthyVectors))]
    public async Task UploadDocumentsAsync_UnhealthyVector_IsWithheldAndNotUploaded(float[] vector, string label)
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [Document("c1", vector), Document("c2")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(1, result.DocsWithheld, label);

        // Only the healthy chunk is handed to Search - the withhold is about the batch, not about
        // what this run accounted for.
        indexService.Verify(m => m.UpsertDocumentsAsync(
            It.Is<IEnumerable<SearchUploadChunk>>(b => b.Count() == 1 && b.Single().Id == "c2"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public static IEnumerable<object[]> UnhealthyVectors =>
    [
        [new[] { 0.1f, 0.2f },                     "wrong width"],
        [new[] { 0f, 0f, 0f, 0f },                 "all-zero"],
        [new[] { 0.1f, float.NaN, 0.3f, 0.4f },    "non-finite"],
    ];

    // DocsFailed stays "Search refused it". The fold into the report's DocsFailed happens in
    // IndexingFunction, not here, so that RestoreService can report the two separately.
    [TestMethod]
    public async Task UploadDocumentsAsync_Withheld_IsNotCountedAsDocsFailed()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync(
            [Document("c1", AllZero), Document("c2")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.DocsFailed);
        Assert.AreEqual(1, result.DocsWithheld);
    }

    // A total withhold is a configuration fault, not a data-quality event: without the throw the
    // run reports success with DocsUploaded = 0 while every stale row is retained, which is
    // indistinguishable from "nothing to do".
    [TestMethod]
    public async Task UploadDocumentsAsync_EveryChunkWithheld_Throws()
    {
        var indexService = MockIndexDocumentService(succeeded: 0, failed: 0);
        var service      = BuildService(indexService);

        var ex = await Assert.ThrowsExactlyAsync<TotalWithholdException>(
            () => service.UploadDocumentsAsync(
                [Document("c1", WrongWidth), Document("c2", WrongWidth)], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));

        // All-WrongWidth is the dimension-drift signature, so the message has to send the reader
        // to the configuration rather than to the embedding deployment.
        StringAssert.Contains(ex.Message, "OPENAI_EMBEDDING_DIMENSIONS");
        StringAssert.Contains(ex.Message, "WrongWidth=2");

        // The properties are the contract, not the prose: whatever reports this failure should
        // read these rather than regex the message, which is written to be reworded.
        Assert.IsTrue(ex.IsDimensionDrift);
        Assert.AreEqual(2, ex.TotalChunks);
        Assert.AreEqual(Dims, ex.ExpectedDimensions);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, int> { ["WrongWidth"] = 2 }, (Dictionary<string, int>)ex.VerdictCounts);
    }

    // Same guard, different diagnosis. Sending this reader to the dimension setting would waste
    // the outage - the width is fine and the deployment is the problem.
    [TestMethod]
    public async Task UploadDocumentsAsync_EveryChunkWithheldButNotOnWidth_ThrowsPointingAtTheDeployment()
    {
        var indexService = MockIndexDocumentService(succeeded: 0, failed: 0);
        var service      = BuildService(indexService);

        var ex = await Assert.ThrowsExactlyAsync<TotalWithholdException>(
            () => service.UploadDocumentsAsync(
                [Document("c1", AllZero), Document("c2", NonFinite)], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));

        StringAssert.Contains(ex.Message, "embedding deployment");
        Assert.IsFalse(ex.Message.Contains("OPENAI_EMBEDDING_DIMENSIONS"));

        Assert.IsFalse(ex.IsDimensionDrift, "not a width problem — the widths were all correct");
        CollectionAssert.AreEquivalent(
            new Dictionary<string, int> { ["Empty"] = 1, ["NonFinite"] = 1 },
            (Dictionary<string, int>)ex.VerdictCounts);
    }

    // The absent-vector case has no verdict, so it must still be countable by name rather than
    // dropped from the breakdown a reporter reads.
    [TestMethod]
    public async Task UploadDocumentsAsync_EveryChunkWithheldForNoVector_CountsThemAsNoVector()
    {
        var indexService = MockIndexDocumentService(succeeded: 0, failed: 0);
        var service      = BuildService(indexService);

        var ex = await Assert.ThrowsExactlyAsync<TotalWithholdException>(
            () => service.UploadDocumentsAsync(
                [VectorlessDocument("c1")], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims));

        Assert.IsFalse(ex.IsDimensionDrift);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, int> { ["NoVector"] = 1 }, (Dictionary<string, int>)ex.VerdictCounts);
    }

    // Nothing in, nothing withheld - the guard must not read 0 == 0 as a total withhold.
    [TestMethod]
    public async Task UploadDocumentsAsync_NoDocuments_DoesNotTripTheTotalWithholdGuard()
    {
        var indexService = MockIndexDocumentService(succeeded: 0, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([], staleDocumentIds: [], familyMoves: [], indexVectorDimensions: Dims);

        Assert.AreEqual(0, result.DocsWithheld);
    }
}
