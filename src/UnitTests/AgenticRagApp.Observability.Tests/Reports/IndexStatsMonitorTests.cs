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
}
