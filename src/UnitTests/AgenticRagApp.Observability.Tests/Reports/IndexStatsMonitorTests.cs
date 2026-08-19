using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class IndexStatsMonitorTests
{
    private static (IndexStatsMonitor Monitor, Mock<IRunReportWriter> ReportWriter) BuildMonitor()
    {
        var reportWriter = new Mock<IRunReportWriter>();
        var monitor = new IndexStatsMonitor(reportWriter.Object, NullLogger<IndexStatsMonitor>.Instance);
        return (monitor, reportWriter);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_NoBaseline_NoRedFlagsButStillSavesNewBaseline()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>())).ReturnsAsync(((long, long)?)null);

        var result = await monitor.RecordAndCheckDriftAsync("pdf", 100, 2048);

        Assert.AreEqual(0, result.RedFlags.Count);
        reportWriter.Verify(w => w.SaveLastIndexStatsAsync("pdf", 100, 2048, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_WithinThreshold_NoRedFlags()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(100L, 1000L));

        // +10% - within the 15% threshold.
        var result = await monitor.RecordAndCheckDriftAsync("pdf", 110, 1000);

        Assert.AreEqual(0, result.RedFlags.Count);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_BeyondThreshold_FlagsDrift()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(100L, 1000L));

        // -50% - well beyond the 15% threshold.
        var result = await monitor.RecordAndCheckDriftAsync("pdf", 50, 1000);

        Assert.AreEqual(1, result.RedFlags.Count);
        Assert.IsTrue(result.RedFlags[0].Contains("index_doc_count_drift"));
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_ZeroBaselineDocumentCount_SkipsComparisonToAvoidDivideByZero()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(0L, 0L));

        var result = await monitor.RecordAndCheckDriftAsync("pdf", 1000, 2048);

        Assert.AreEqual(0, result.RedFlags.Count);
    }

    // The baseline is returned specifically because SaveLastIndexStatsAsync overwrites it in
    // the same call - these two tests are what stop that value being dropped again.
    [TestMethod]
    public async Task RecordAndCheckDriftAsync_WithBaseline_ReturnsPreviousStatsEvenWhenWithinThreshold()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(100L, 1000L));

        // +10%: no red flag, but the delta must still be recoverable by the run report.
        var result = await monitor.RecordAndCheckDriftAsync("pdf", 110, 1100);

        Assert.AreEqual(0, result.RedFlags.Count);
        Assert.AreEqual(100L,  result.PreviousDocumentCount);
        Assert.AreEqual(1000L, result.PreviousStorageSizeBytes);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_NoBaseline_ReturnsNullPreviousStats()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>())).ReturnsAsync(((long, long)?)null);

        var result = await monitor.RecordAndCheckDriftAsync("pdf", 100, 2048);

        // Null, not 0 - "first run for this source", not "the index was empty".
        Assert.IsNull(result.PreviousDocumentCount);
        Assert.IsNull(result.PreviousStorageSizeBytes);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_ScopesBaselineLookupAndSaveToGivenSource()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("csv", It.IsAny<CancellationToken>())).ReturnsAsync(((long, long)?)null);

        await monitor.RecordAndCheckDriftAsync("csv", 10, 20);

        reportWriter.Verify(w => w.GetLastIndexStatsAsync("csv", It.IsAny<CancellationToken>()), Times.Once);
        reportWriter.Verify(w => w.SaveLastIndexStatsAsync("csv", 10, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The 260819 regression. UploadService reads index statistics immediately after upload
    // and Azure Search's stats lag those writes by minutes, so a run that had just uploaded
    // 2,932 chunks read 0 documents. Two consequences, and the second is the dangerous one.
    [TestMethod]
    public async Task RecordAndCheckDriftAsync_ZeroCurrentDocumentCount_DoesNotFlagDrift()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(2997L, 45389222L));

        // What the 260819 run actually read, having uploaded 2,932 chunks moments earlier.
        var result = await monitor.RecordAndCheckDriftAsync("pdf", 0, 0);

        Assert.AreEqual(0, result.RedFlags.Count, "a lagging stats read is not corpus loss");
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_ZeroCurrentDocumentCount_LeavesTheLastRealBaselineInPlace()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(2997L, 45389222L));

        await monitor.RecordAndCheckDriftAsync("pdf", 0, 0);

        // Persisting the 0 would overwrite the baseline, and the drift check only runs when
        // the baseline is > 0 - so the NEXT run's check would be silently skipped entirely.
        reportWriter.Verify(
            w => w.SaveLastIndexStatsAsync("pdf", 0, 0, It.IsAny<CancellationToken>()),
            Times.Never);
        reportWriter.Verify(
            w => w.SaveLastIndexStatsAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_ZeroCurrentDocumentCount_StillReturnsThePreviousStats()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(2997L, 45389222L));

        var result = await monitor.RecordAndCheckDriftAsync("pdf", 0, 0);

        // The run report still shows what the last known-good corpus size was.
        Assert.AreEqual(2997L,     result.PreviousDocumentCount);
        Assert.AreEqual(45389222L, result.PreviousStorageSizeBytes);
    }

    [TestMethod]
    public async Task RecordAndCheckDriftAsync_RealDropToNonZero_IsStillFlagged()
    {
        var (monitor, reportWriter) = BuildMonitor();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(2997L, 45389222L));

        // Only an exact zero is treated as a lagging read - genuine corpus loss still trips.
        var result = await monitor.RecordAndCheckDriftAsync("pdf", 1, 1000);

        Assert.AreEqual(1, result.RedFlags.Count);
        Assert.IsTrue(result.RedFlags[0].Contains("index_doc_count_drift"));
        reportWriter.Verify(w => w.SaveLastIndexStatsAsync("pdf", 1, 1000, It.IsAny<CancellationToken>()), Times.Once);
    }
}
