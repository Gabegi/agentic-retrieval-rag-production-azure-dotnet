using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Functions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Functions;

[TestClass]
public class PdfIndexingFunctionTests
{
    private sealed class Deps
    {
        public Mock<IExtractionService>      ExtractionService = new();
        public Mock<IChunkingService>        ChunkingService   = new();
        public Mock<IEmbeddingService>       EmbeddingService  = new();
        public Mock<IUploadService>          UploadService     = new();
        public Mock<IIndexService>           IndexService      = new();
        public Mock<IKnowledgeService>       KnowledgeService  = new();
        public Mock<IBlobStore>              BlobStore         = new();
        public Mock<IRunReportWriter>        ReportWriter      = new();
        public Mock<IPipelineArtifactWriter> ArtifactWriter    = new();
        public Mock<ISnapshotService>        SnapshotService   = new();
        public Mock<IVectorCache>            VectorCache       = new();
        public Mock<IRestoreService>         RestoreService    = new();

        public PdfIndexingFunction Build() => new(
            ExtractionService.Object, ChunkingService.Object, EmbeddingService.Object, UploadService.Object,
            IndexService.Object, KnowledgeService.Object, new Mock<BlobContainerClient>().Object, BlobStore.Object,
            ReportWriter.Object, ArtifactWriter.Object, SnapshotService.Object, VectorCache.Object,
            RestoreService.Object, NullLogger<PdfIndexingFunction>.Instance);
    }

    private static Mock<TaskOrchestrationContext> MockOrchestrationContext(string instanceId = "instance-1")
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.SetupGet(c => c.InstanceId).Returns(instanceId);
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);
        return context;
    }

    // ── RunOrchestrator ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task RunOrchestrator_AllStagesSucceed_SavesSuccessReportAndDoesNotThrow()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<PdfIndexRequest>()).Returns(new PdfIndexRequest(false));
        context.Setup(c => c.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ExtractStats());
        context.Setup(c => c.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ChunkingStageMetrics.Empty("v1"));
        context.Setup(c => c.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(EmbedStats());
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        var function = deps.Build();

        await function.RunOrchestrator(context.Object);

        context.Verify(c => c.CallActivityAsync("SaveIndexReportActivity",
            It.Is<PdfIndexRunReport>(r => r.Success), It.IsAny<TaskOptions>()), Times.Once);
    }

    [TestMethod]
    public async Task RunOrchestrator_ExtractActivityThrows_SavesFailureReportAndRethrows()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<PdfIndexRequest>()).Returns(new PdfIndexRequest(false));
        context.Setup(c => c.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ThrowsAsync(new InvalidOperationException("ExtractActivity failed: boom"));
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        var function = deps.Build();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RunOrchestrator(context.Object));

        context.Verify(c => c.CallActivityAsync("SaveIndexReportActivity",
            It.Is<PdfIndexRunReport>(r => !r.Success && r.ErrorMessage != null), It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(c => c.CallActivityAsync<ChunkingStageMetrics>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    // ── ExtractActivity ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExtractActivity_Success_EnsuresIndexWritesBlobAndReturnsStats()
    {
        var deps  = new Deps();
        var docs  = new List<PdfExtractionDocument> { Doc("doc1.pdf") };
        var stats = ExtractStats();
        deps.ExtractionService.Setup(s => s.ExtractAsync(false, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((docs, stats));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.ExtractActivity(new PdfExtractRequest(false, "extracted.json", "stale-ids.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(stats, result);
        deps.IndexService.Verify(s => s.EnsureIndexAsync(), Times.Once);
        deps.BlobStore.Verify(b => b.AssertContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        deps.BlobStore.Verify(b => b.UploadJsonAsync(It.IsAny<BlobContainerClient>(), "extracted.json", It.IsAny<IReadOnlyList<PdfExtractionDocument>>(), It.IsAny<System.Text.Json.JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.UploadJsonAsync(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<IReadOnlyList<string>>(), It.IsAny<System.Text.Json.JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExtractActivity_ExtractionServiceThrows_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.ExtractionService.Setup(s => s.ExtractAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.ExtractActivity(new PdfExtractRequest(false, "extracted.json", "stale-ids.json", "instance-1", DateTimeOffset.UtcNow), context));

        StringAssert.Contains(ex.Message, "ExtractActivity failed");
    }

    [TestMethod]
    public async Task ExtractActivity_OperationCanceled_PropagatesWithoutWrapping()
    {
        var deps = new Deps();
        deps.ExtractionService.Setup(s => s.ExtractAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            function.ExtractActivity(new PdfExtractRequest(false, "extracted.json", "stale-ids.json", "instance-1", DateTimeOffset.UtcNow), context));
    }

    // ── ChunkActivity ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ChunkActivity_Success_ReadsInputDeletesItAndWritesOutputBlob()
    {
        var deps  = new Deps();
        var docs  = new List<PdfExtractionDocument> { Doc("doc1.pdf") };
        var chunk = new DocumentChunk { Id = "c1", DocumentId = "doc1.pdf", Content = "hello" };
        var stats = ChunkingStageMetrics.Empty("v1");
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<PdfExtractionDocument>>(It.IsAny<BlobContainerClient>(), "extracted.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);
        deps.ChunkingService.Setup(c => c.ChunkDocuments(docs)).Returns(([chunk], stats));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.ChunkActivity(new PdfChunkRequest("extracted.json", "chunks.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(stats, result);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "extracted.json", It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.UploadJsonAsync(It.IsAny<BlobContainerClient>(), "chunks.json", It.Is<IReadOnlyList<DocumentChunk>>(l => l.Count == 1), It.IsAny<System.Text.Json.JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ChunkActivity_ChunkingServiceThrows_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<PdfExtractionDocument>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Doc("doc1.pdf")]);
        deps.ChunkingService.Setup(c => c.ChunkDocuments(It.IsAny<IReadOnlyList<PdfExtractionDocument>>())).Throws(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.ChunkActivity(new PdfChunkRequest("extracted.json", "chunks.json", "instance-1", DateTimeOffset.UtcNow), context));

        StringAssert.Contains(ex.Message, "ChunkActivity failed");
    }

    // ── EmbedAndUploadActivity ───────────────────────────────────────────────

    [TestMethod]
    public async Task EmbedAndUploadActivity_Success_EmbedsUploadsSnapshotsEvictsAndDeletesChunksBlob()
    {
        var deps   = new Deps();
        var chunks = new List<DocumentChunk> { new() { Id = "c1", DocumentId = "doc1.pdf", Content = "hello" } };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<DocumentChunk>>(It.IsAny<BlobContainerClient>(), "chunks.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["stale1"]);
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, ChunksTruncated: 0, EmbeddingRetries: 0, VectorDimErrors: 0, CacheHits: 1));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult(DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: []));
        deps.SnapshotService.Setup(s => s.UpdateAsync(
                "pdf", It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), "instance-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string> { "hash1" });
        deps.VectorCache.Setup(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(new PdfEmbedUploadRequest("chunks.json", "stale-ids.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(1, result.DocsUploaded);
        Assert.AreEqual(1, result.VectorCacheHits);
        deps.VectorCache.Verify(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "chunks.json", It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmbedAndUploadActivity_UploadServiceThrows_WrapsInInvalidOperationException()
    {
        var deps   = new Deps();
        var chunks = new List<DocumentChunk> { new() { Id = "c1", DocumentId = "doc1.pdf", Content = "hello" } };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<DocumentChunk>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.EmbedAndUploadActivity(new PdfEmbedUploadRequest("chunks.json", "stale-ids.json", "instance-1", DateTimeOffset.UtcNow), context));

        StringAssert.Contains(ex.Message, "EmbedAndUploadActivity failed");
    }

    // ── SaveIndexReportActivity ──────────────────────────────────────────────

    [TestMethod]
    public async Task SaveIndexReportActivity_ReportWriterEnabled_WritesReport()
    {
        var deps = new Deps();
        deps.ReportWriter.SetupGet(w => w.IsEnabled).Returns(true);
        deps.ReportWriter.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var report   = new PdfIndexRunReport { Run = new RunIdentity("instance-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, true) };

        await function.SaveIndexReportActivity(report, context);

        deps.ReportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("instance-1")), report, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveIndexReportActivity_ReportWriterDisabled_DoesNotWrite()
    {
        var deps = new Deps();
        deps.ReportWriter.SetupGet(w => w.IsEnabled).Returns(false);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var report   = new PdfIndexRunReport { Run = new RunIdentity("instance-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, true) };

        await function.SaveIndexReportActivity(report, context);

        deps.ReportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RunRestoreOrchestrator ───────────────────────────────────────────────

    [TestMethod]
    public async Task RunRestoreOrchestrator_Success_SavesSuccessReportAndDoesNotThrow()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        context.Setup(c => c.CallActivityAsync<RestoreResult>("RestoreFromSnapshotActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new RestoreResult("snap-1", 5, 0, 10, 100, "index", "text-embedding-3-large", "embedding-deployment"));
        context.Setup(c => c.CallActivityAsync("SaveRestoreReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>())).Returns(Task.CompletedTask);
        var function = deps.Build();

        await function.RunRestoreOrchestrator(context.Object);

        context.Verify(c => c.CallActivityAsync("SaveRestoreReportActivity",
            It.Is<PdfRestoreRunReport>(r => r.Success && r.ChunksRestored == 5), It.IsAny<TaskOptions>()), Times.Once);
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

    [TestMethod]
    public async Task RecreateIndexActivity_Success_DeletesKnowledgeBaseAndSourceBeforeIndexThenRebuildsAfter()
    {
        var deps = new Deps();
        var callOrder = new List<string>();
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeBaseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete-base")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeSourceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete-source")).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.RecreateIndexAsync())
            .Callback(() => callOrder.Add("recreate-index")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ensure-source")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ensure-base")).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        await function.RecreateIndexActivity(null, context);

        CollectionAssert.AreEqual(
            new[] { "delete-base", "delete-source", "recreate-index", "ensure-source", "ensure-base" }, callOrder);
    }

    [TestMethod]
    public async Task RecreateIndexActivity_Throws_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeBaseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeSourceAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.RecreateIndexAsync()).ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RecreateIndexActivity(null, context));

        StringAssert.Contains(ex.Message, "RecreateIndexActivity failed");
    }

    [TestMethod]
    public async Task RestoreFromSnapshotActivity_Success_ReturnsRestoreResult()
    {
        var deps   = new Deps();
        var result = new RestoreResult("snap-1", 5, 0, 10, 100, "index", "model", "deployment");
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
        var report   = new PdfRestoreRunReport(
            InstanceId: "instance-1", StartedAt: DateTimeOffset.UtcNow, FinishedAt: DateTimeOffset.UtcNow,
            Success: true, ErrorMessage: null, SnapshotInstanceId: "snap-1", ChunksRestored: 5,
            ChunksMissingVector: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100,
            SearchIndexName: "index", EmbeddingModel: "model", EmbeddingDeployment: "deployment");

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
        var report   = new PdfRestoreRunReport(
            InstanceId: "instance-1", StartedAt: DateTimeOffset.UtcNow, FinishedAt: DateTimeOffset.UtcNow,
            Success: true, ErrorMessage: null, SnapshotInstanceId: "snap-1", ChunksRestored: 5,
            ChunksMissingVector: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100,
            SearchIndexName: "index", EmbeddingModel: "model", EmbeddingDeployment: "deployment");

        await function.SaveRestoreReportActivity(report, context);

        deps.ReportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RunSetupKnowledgeBase ────────────────────────────────────────────────

    [TestMethod]
    public async Task RunSetupKnowledgeBase_EnsuresKnowledgeSourceAndBase_ReturnsOk()
    {
        var deps = new Deps();
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "");

        var response = (FakeHttpResponseData)await function.RunSetupKnowledgeBase(request, context);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>()), Times.Once);
        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static PdfExtractionDocument Doc(string sourceId) => new(
        SourceId:              sourceId,
        Ordinal:               0,
        Content:               "content",
        Title:                 "",
        Author:                null,
        CreatedAt:             null,
        ModDate:               null,
        PageCount:             null,
        LastModifiedDate:      null,
        ZenyaDocumentId:       null,
        ZenyaVersion:          null,
        ZenyaStatus:           null,
        ZenyaUrl:              null,
        Bookmarks:             [],
        Sections:              [],
        Breadcrumb:            null,
        Headings:              [],
        Boilerplate:           [],
        Tables:                [],
        Dimensions:            null,
        SelectionMarks:        [],
        Figures:               [],
        Lines:                 []);

    private static ExtractionStageMetrics ExtractStats() => new(
        Source: "pdf", DocsToProcess: 1, DocsSkipped: 0, DocsNew: 1, DocsUpdated: 0, DocsDeleted: 0,
        StaleDocumentIds: [], ValidationErrors: 0, ValidationWarnings: 0, ReconciliationProblems: 0,
        StaleDocCount: 0, MojibakeRepairedPages: 0, DetectedTableCount: 0, DocsWithoutHeadings: 0,
        MissingTitleCount: 0, MissingVersionCount: 0, MissingDepartmentCount: 0, TraceabilityGapCount: 0,
        Issues: [], RedFlags: [], SpotCheckSample: []);

    private static EmbedUploadStageMetrics EmbedStats() => new(
        DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunksTruncated: 0, EmbeddingRetries: 0,
        VectorDimErrors: 0, VectorCacheHits: 0, TotalEmbeddingDurationMs: 10, IndexDocumentCountSnapshot: 10,
        IndexStorageSizeBytesSnapshot: 100, RedFlags: [], ChunksEvicted: 0,
        PreviousIndexDocumentCount: null, PreviousIndexStorageSizeBytes: null);
}
