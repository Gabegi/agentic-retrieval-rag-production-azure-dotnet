using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class UploadServiceTests
{
    // Id lives on the metadata now - ChunkObject.Id is a read-only pass-through onto it.
    private static ChunkObject Document(string id) => new()
    {
        Content  = "content",
        Metadata = new ChunkMetadata { Id = id },
    };

    private static Mock<IIndexDocumentService> MockIndexDocumentService(
        int succeeded, int failed,
        IReadOnlyList<string>? existingChunkIds = null,
        int deletedCount = 0,
        (long DocCount, long StorageBytes)? stats = null,
        Exception? statsException = null)
    {
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.UpsertDocumentsAsync(It.IsAny<IEnumerable<SearchUploadChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((succeeded, failed));
        mock.Setup(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingChunkIds ?? []);
        mock.Setup(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        if (statsException is not null)
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(statsException);
        else
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stats ?? (0L, 0L));

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

    private static UploadService BuildService(
        Mock<IIndexDocumentService> indexDocumentService, Mock<IIndexStatsMonitor>? indexStatsMonitor = null) =>
        new(indexDocumentService.Object, (indexStatsMonitor ?? MockIndexStatsMonitor()).Object, NullLogger<UploadService>.Instance);

    [TestMethod]
    public async Task UploadDocumentsAsync_ReturnsSucceededAndFailedCountsFromIndexService()
    {
        var indexService = MockIndexDocumentService(succeeded: 3, failed: 1);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []);

        Assert.AreEqual(3, result.DocsUploaded);
        Assert.AreEqual(1, result.DocsFailed);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NoStaleDocuments_SkipsCleanupEntirely()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []);

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

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"], familyMoves: []);

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

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"], familyMoves: []);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves);

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

        // Document("c1")'s DocumentId comes off its metadata - matching the moved SourceId.
        var uploaded = new ChunkObject
        {
            Content  = "content",
            Metadata = new ChunkMetadata { Id = "c1", DocumentId = "doc1" },
        };

        var result = await service.UploadDocumentsAsync([uploaded], staleDocumentIds: [], familyMoves: moves);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves);

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

        await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: moves);

        Assert.AreEqual(1, result.ChunkFamiliesPatched);
        Assert.AreEqual(1, result.DocsUploaded, "a failed patch does not fail the upload it followed");
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotSucceeds_PopulatesSnapshotAndRedFlags()
    {
        var indexService     = MockIndexDocumentService(succeeded: 1, failed: 0, stats: (100L, 2048L));
        var indexStatsMonitor = MockIndexStatsMonitor(driftRedFlags: ["index_doc_count_drift:+50.0% (50 -> 100)"]);
        var service = BuildService(indexService, indexStatsMonitor);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []);

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
            () => service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: [], familyMoves: []));
    }
}
