using Azure.Storage.Blobs;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Functions;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
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
        public Mock<IBlobStore>              BlobStore         = new();
        public Mock<IRunReportWriter>        ReportWriter      = new();
        public Mock<IPipelineArtifactWriter> ArtifactWriter    = new();
        public Mock<ISnapshotService>        SnapshotService   = new();
        public Mock<IVectorCache>            VectorCache       = new();
        public Mock<IDocumentIdentityStore>  IdentityStore     = new();

        public PdfIndexingFunction Build() => new(
            ExtractionService.Object, ChunkingService.Object, EmbeddingService.Object, UploadService.Object,
            IndexService.Object, new Mock<BlobContainerClient>().Object, BlobStore.Object,
            ReportWriter.Object, ArtifactWriter.Object, SnapshotService.Object, VectorCache.Object,
            IdentityStore.Object, NullLogger<PdfIndexingFunction>.Instance);
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

    // The daily scheduled run's shape: the index is dropped and rebuilt empty before
    // extraction, not indexed into as-is.
    [TestMethod]
    public async Task RunOrchestrator_RecreateIndexRequested_RecreatesBeforeExtracting()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        var order   = new List<string>();
        context.Setup(c => c.GetInput<PdfIndexRequest>()).Returns(new PdfIndexRequest(ForceReindex: true, RecreateIndex: true));
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Callback(() => order.Add("recreate")).Returns(Task.CompletedTask);
        context.Setup(c => c.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Callback(() => order.Add("extract")).ReturnsAsync(ExtractStats());
        context.Setup(c => c.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ChunkingStageMetrics.Empty("v1"));
        context.Setup(c => c.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(EmbedStats());
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        var function = deps.Build();

        await function.RunOrchestrator(context.Object);

        CollectionAssert.AreEqual(new[] { "recreate", "extract" }, order);
    }

    // A failed recreate must abort the run rather than fall through to extraction, where
    // ExtractActivity's EnsureIndexAsync would recreate the index and hide the failure.
    [TestMethod]
    public async Task RunOrchestrator_RecreateIndexActivityThrows_SavesFailureReportAndSkipsExtraction()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<PdfIndexRequest>()).Returns(new PdfIndexRequest(ForceReindex: true, RecreateIndex: true));
        context.Setup(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ThrowsAsync(new InvalidOperationException("RecreateIndexActivity failed: boom"));
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        var function = deps.Build();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RunOrchestrator(context.Object));

        context.Verify(c => c.CallActivityAsync("SaveIndexReportActivity",
            It.Is<PdfIndexRunReport>(r => !r.Success && r.ErrorMessage != null), It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(c => c.CallActivityAsync<ExtractionStageMetrics>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    // The on-demand path (POST /api/index without ?recreate=true) must leave the live index
    // in place - a run that wipes it when it was not asked to is the expensive mistake here.
    [TestMethod]
    public async Task RunOrchestrator_RecreateIndexNotRequested_DoesNotRecreateIndex()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<PdfIndexRequest>()).Returns(new PdfIndexRequest(ForceReindex: true));
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

        context.Verify(c => c.CallActivityAsync("RecreateIndexActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
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
        var chunk = Chunk();
        var stats = ChunkingStageMetrics.Empty("v1");
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<PdfExtractionDocument>>(It.IsAny<BlobContainerClient>(), "extracted.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);
        // The chunking stage writes its own report now, so the activity passes the run's
        // instance id and start time down rather than writing an artifact itself.
        deps.ChunkingService
            .Setup(c => c.ChunkDocumentsAsync(docs, "instance-1", It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([chunk], stats, (IReadOnlyList<FamilyMove>)[]));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.ChunkActivity(new PdfChunkRequest("extracted.json", "chunks.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(stats, result);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "extracted.json", It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.UploadJsonAsync(It.IsAny<BlobContainerClient>(), "chunks.json", It.Is<IReadOnlyList<ChunkObject>>(l => l.Count == 1), It.IsAny<System.Text.Json.JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ChunkActivity_ChunkingServiceThrows_WrapsInInvalidOperationException()
    {
        var deps = new Deps();
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<PdfExtractionDocument>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Doc("doc1.pdf")]);
        deps.ChunkingService
            .Setup(c => c.ChunkDocumentsAsync(It.IsAny<IReadOnlyList<PdfExtractionDocument>>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.ChunkActivity(new PdfChunkRequest("extracted.json", "chunks.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context));

        StringAssert.Contains(ex.Message, "ChunkActivity failed");
    }

    // ── EmbedAndUploadActivity ───────────────────────────────────────────────

    [TestMethod]
    public async Task EmbedAndUploadActivity_Success_EmbedsUploadsSnapshotsEvictsAndDeletesChunksBlob()
    {
        var deps   = new Deps();
        var chunks = new List<ChunkObject> { Chunk() };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<ChunkObject>>(It.IsAny<BlobContainerClient>(), "chunks.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["stale1"]);
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, ChunksTruncated: 0, EmbeddingRetries: 0, VectorDimErrors: 0, CacheHits: 1));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult(DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: []));
        deps.SnapshotService.Setup(s => s.UpdateAsync(
                "pdf", It.IsAny<IReadOnlyList<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), "instance-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotLiveSet(
                new HashSet<string> { "hash1" },
                new HashSet<string> { "doc1.pdf" }));
        deps.VectorCache.Setup(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        deps.IdentityStore.Setup(s => s.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(new PdfEmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(1, result.DocsUploaded);
        Assert.AreEqual(1, result.VectorCacheHits);
        deps.VectorCache.Verify(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        // The identity store is evicted against the snapshot's live DOCUMENT ids, not its
        // content hashes - the two stores are keyed differently and passing the wrong grain
        // would delete every identity record.
        deps.IdentityStore.Verify(s => s.EvictOrphanedAsync(
            It.Is<IReadOnlySet<string>>(ids => ids.Contains("doc1.pdf")), It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "chunks.json", It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<CancellationToken>()), Times.Once);
        deps.BlobStore.Verify(b => b.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "family-moves.json", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmbedAndUploadActivity_FamilyMovesBlobMissing_TreatsAsNoMovesAndSucceeds()
    {
        // An orchestration whose ChunkActivity ran under a deployment that predates
        // family-moves.json replays EmbedAndUpload with no blob to read - that must not
        // fail the run.
        var deps   = new Deps();
        var chunks = new List<ChunkObject> { Chunk() };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<ChunkObject>>(It.IsAny<BlobContainerClient>(), "chunks.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), "stale-ids.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<FamilyMove>>(It.IsAny<BlobContainerClient>(), "family-moves.json", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Azure.RequestFailedException(404, "BlobNotFound"));
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult(DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: []));
        deps.SnapshotService.Setup(s => s.UpdateAsync(
                "pdf", It.IsAny<IReadOnlyList<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), "instance-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotLiveSet(new HashSet<string>(), new HashSet<string>()));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(new PdfEmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context);

        Assert.AreEqual(1, result.DocsUploaded);
        deps.UploadService.Verify(s => s.UploadDocumentsAsync(
            It.IsAny<IEnumerable<ChunkObject>>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.Is<IReadOnlyList<FamilyMove>>(moves => moves.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmbedAndUploadActivity_UploadServiceThrows_WrapsInInvalidOperationException()
    {
        var deps   = new Deps();
        var chunks = new List<ChunkObject> { Chunk() };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<ChunkObject>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.EmbedAndUploadActivity(new PdfEmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context));

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

    // Restore orchestration and knowledge-base setup moved out with the functions
    // themselves - see IndexRestoreFunctionTests and IndexAdminFunctionTests.

    // ── fixtures ─────────────────────────────────────────────────────────────

    // A chunk carries its identity on Metadata - ChunkObject.Id and .DocumentId are read-only
    // pass-throughs to it. These tests only ever need those two fields and the content.
    private static ChunkObject Chunk(string id = "c1", string documentId = "doc1.pdf", string content = "hello") =>
        new() { Content = content, Metadata = new ChunkMetadata { Id = id, DocumentId = documentId } };

    private static PdfExtractionDocument Doc(string sourceId) => new(
        SourceId:              sourceId,
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
        PageSpans:             [new PageSpan(1, 0, "content".Length, null, false)],
        PageBreadcrumbs:       new Dictionary<int, string>(),
        Sections:              [],
        Headings:              [],
        Boilerplate:           [],
        Tables:                [],
        SelectionMarks:        [],
        Figures:               [],
        Lines:                 [],
        Profile:               null,
        Language:              null);

    private static ExtractionStageMetrics ExtractStats() => new(
        Source: "pdf", DocsToProcess: 1, DocsSkipped: 0, DocsNew: 1, DocsUpdated: 0, DocsDeleted: 0,
        StaleDocumentIds: [], ValidationErrors: 0, ValidationWarnings: 0, ReconciliationProblems: 0,
        StaleDocCount: 0, MojibakeRepairedPages: 0, DetectedTableCount: 0, DocsWithoutHeadings: 0,
        MissingTitleCount: 0, MissingVersionCount: 0, MissingDepartmentCount: 0, TraceabilityGapCount: 0,
        Issues: [], RedFlags: [], SpotCheckSample: []);

    private static EmbedUploadStageMetrics EmbedStats() => new(
        DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0, ChunksTruncated: 0,
        EmbeddingRetries: 0, VectorDimErrors: 0, VectorCacheHits: 0, TotalEmbeddingDurationMs: 10,
        IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: [], ChunksEvicted: 0,
        PreviousIndexDocumentCount: null, PreviousIndexStorageSizeBytes: null);
}
