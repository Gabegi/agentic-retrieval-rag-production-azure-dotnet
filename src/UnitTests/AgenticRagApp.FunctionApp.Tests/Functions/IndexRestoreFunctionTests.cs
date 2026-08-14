using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Functions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Functions;

[TestClass]
public class IndexRestoreFunctionTests
{
    private sealed class Deps
    {
        public Mock<IIndexRebuildService> IndexRebuildService = new();
        public Mock<IRestoreService>      RestoreService      = new();
        public Mock<IRunReportWriter>     ReportWriter        = new();

        public IndexRestoreFunction Build() => new(
            IndexRebuildService.Object, RestoreService.Object,
            ReportWriter.Object, NullLogger<IndexRestoreFunction>.Instance);
    }

    private static Mock<TaskOrchestrationContext> MockOrchestrationContext(string instanceId = "instance-1")
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.SetupGet(c => c.InstanceId).Returns(instanceId);
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);
        return context;
    }

    // ── RunRestoreOrchestrator ───────────────────────────────────────────────

    [TestMethod]
    public async Task RunRestoreOrchestrator_Success_SavesSuccessReportAndDoesNotThrow()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        context.Setup(c => c.CallActivityAsync<RestoreResult>("RestoreFromSnapshotActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new RestoreResult("snap-1", 5, 0, 0, 10, 100, "index", "text-embedding-3-large", "embedding-deployment"));
        context.Setup(c => c.CallActivityAsync("SaveRestoreReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        var function = deps.Build();

        await function.RunRestoreOrchestrator(context.Object);

        context.Verify(c => c.CallActivityAsync("SaveRestoreReportActivity",
            It.Is<PdfRestoreRunReport>(r => r.Success && r.ChunksRestored == 5), It.IsAny<TaskOptions>()), Times.Once);
    }

    [TestMethod]
    public async Task RunRestoreOrchestrator_ChunksFailed_SavesFailureReportAndThrows()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        context.Setup(c => c.CallActivityAsync<RestoreResult>("RestoreFromSnapshotActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new RestoreResult("snap-1", 5, 3, 0, 10, 100, "index", "text-embedding-3-large", "embedding-deployment"));
        context.Setup(c => c.CallActivityAsync("SaveRestoreReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        var function = deps.Build();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RunRestoreOrchestrator(context.Object));

        context.Verify(c => c.CallActivityAsync("SaveRestoreReportActivity",
            It.Is<PdfRestoreRunReport>(r => !r.Success && r.ChunksRestored == 5 && r.ChunksFailed == 3), It.IsAny<TaskOptions>()), Times.Once);
    }

    [TestMethod]
    public async Task RunRestoreOrchestrator_RecreateIndexThrows_SavesFailureReportAndRethrows()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ThrowsAsync(new InvalidOperationException("RecreateIndexActivity failed: boom"));
        context.Setup(c => c.CallActivityAsync("SaveRestoreReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        var function = deps.Build();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RunRestoreOrchestrator(context.Object));

        context.Verify(c => c.CallActivityAsync("SaveRestoreReportActivity",
            It.Is<PdfRestoreRunReport>(r => !r.Success && r.ChunksRestored == 0), It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(c => c.CallActivityAsync<RestoreResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    // ── RecreateIndexActivity / RestoreFromSnapshotActivity ─────────────────

    // The teardown/rebuild ORDER is IndexRebuildService's contract and is tested there
    // (IndexRebuildServiceTests) - all this activity owes is delegation and error wrapping.
    [TestMethod]
    public async Task RecreateIndexActivity_Success_DelegatesToIndexRebuildService()
    {
        var deps = new Deps();
        deps.IndexRebuildService.Setup(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        await function.RecreateIndexActivity(null, context);

        deps.IndexRebuildService.Verify(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RecreateIndexActivity_Throws_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.IndexRebuildService.Setup(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RecreateIndexActivity(null, context));

        StringAssert.Contains(ex.Message, "RecreateIndexActivity failed");
    }

    [TestMethod]
    public async Task RestoreFromSnapshotActivity_Success_ReturnsRestoreResult()
    {
        var deps   = new Deps();
        var result = new RestoreResult("snap-1", 5, 0, 0, 10, 100, "index", "model", "deployment");
        deps.RestoreService.Setup(s => s.RestoreFromLatestSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var actual = await function.RestoreFromSnapshotActivity(null, context);

        Assert.AreEqual(result, actual);
    }

    [TestMethod]
    public async Task RestoreFromSnapshotActivity_Throws_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.RestoreService.Setup(s => s.RestoreFromLatestSnapshotAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RestoreFromSnapshotActivity(null, context));

        StringAssert.Contains(ex.Message, "RestoreFromSnapshotActivity failed");
    }

    // ── SaveRestoreReportActivity ────────────────────────────────────────────

    [TestMethod]
    public async Task SaveRestoreReportActivity_ReportWriterEnabled_WritesReport()
    {
        var deps = new Deps();
        deps.ReportWriter.SetupGet(w => w.IsEnabled).Returns(true);
        deps.ReportWriter.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var report   = RestoreReport();

        await function.SaveRestoreReportActivity(report, context);

        deps.ReportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("instance-1")), report, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveRestoreReportActivity_ReportWriterDisabled_DoesNotWrite()
    {
        var deps = new Deps();
        deps.ReportWriter.SetupGet(w => w.IsEnabled).Returns(false);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        await function.SaveRestoreReportActivity(RestoreReport(), context);

        deps.ReportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static PdfRestoreRunReport RestoreReport() => new(
        InstanceId: "instance-1", StartedAt: DateTimeOffset.UtcNow, FinishedAt: DateTimeOffset.UtcNow,
        Success: true, ErrorMessage: null, SnapshotInstanceId: "snap-1", ChunksRestored: 5,
        ChunksFailed: 0, ChunksMissingVector: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100,
        SearchIndexName: "index", EmbeddingModel: "model", EmbeddingDeployment: "deployment");
}
