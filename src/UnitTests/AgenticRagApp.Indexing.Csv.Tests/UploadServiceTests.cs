using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.Csv.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class UploadServiceTests
{
    private static ChunkStatsSource Document(string id) => new() { Id = id, Content = "content" };

    private static Mock<IIndexDocumentService> MockIndexDocumentService(
        int succeeded, int failed,
        IReadOnlyList<string>? existingChunkIds = null,
        int deletedCount = 0,
        (long DocCount, long StorageBytes)? stats = null,
        Exception? statsException = null)
    {
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.UpsertDocumentsAsync(It.IsAny<IEnumerable<ChunkStatsSource>>(), It.IsAny<CancellationToken>()))
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

    private static Mock<IIndexStatsMonitor> MockIndexStatsMonitor(IReadOnlyList<string>? driftRedFlags = null)
    {
        var mock = new Mock<IIndexStatsMonitor>();
        mock.Setup(m => m.RecordAndCheckDriftAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driftRedFlags ?? []);
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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(3, result.DocsUploaded);
        Assert.AreEqual(1, result.DocsFailed);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NoStaleDocuments_SkipsCleanupEntirely()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_OrphanedChunks_AreDeleted()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1", "c2"],
            deletedCount: 1);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"]);

        Assert.AreEqual(1, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "c2" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_AllOldChunksWereReuploaded_NoDeleteCallMade()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1"]);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"]);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotSucceeds_PopulatesSnapshotAndRedFlags()
    {
        var indexService      = MockIndexDocumentService(succeeded: 1, failed: 0, stats: (100L, 2048L));
        var indexStatsMonitor = MockIndexStatsMonitor(driftRedFlags: ["index_doc_count_drift:+50.0% (50 -> 100)"]);
        var service = BuildService(indexService, indexStatsMonitor);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

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

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

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

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []));
    }
}
