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
        // EvictOrphanedAsync returns a record since 2026-09-18 (D203 M5a); an unsetup Moq
        // returns null for it, which the activity would dereference. Every test starts from
        // "nothing listed, nothing evicted" and overrides where the eviction is the subject.
        public Deps()
        {
            VectorCache.Setup(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(VectorCacheEviction.None);
        }

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
        public Mock<IIndexDocumentService>   IndexDocumentService = new();
        // Reporting input only - the embedding list price the run report's cost figures use.
        // Left at its default rate; tests that assert on cost set it explicitly.
        public IndexerConfig                 IndexerConfig     = new();

        public IndexingFunction Build() => new(
            ExtractionService.Object, ChunkingService.Object, EmbeddingService.Object, UploadService.Object,
            IndexService.Object, new Mock<BlobContainerClient>().Object, BlobStore.Object,
            ReportWriter.Object, ArtifactWriter.Object, SnapshotService.Object, VectorCache.Object,
            IdentityStore.Object, IndexDocumentService.Object, IndexerConfig, NullLogger<IndexingFunction>.Instance);
    }

    // The preflight read every orchestrator path now makes before extraction (D201).
    private static IndexVectorConfig VectorConfig(int dims = 3072) => new(
        IndexName: "index", FieldName: "content_vector", FieldPresent: true,
        Dimensions: dims, ConfiguredDimensions: dims,
        Algorithm: "hnsw", Metric: "cosine", M: 4, EfConstruction: 400, EfSearch: 500,
        Compression: null, Vectorizer: null, VectorizerModel: null, VectorizerDeployment: null,
        ConfiguredModelName: "text-embedding-3-large", ReadAtUtc: DateTimeOffset.UnixEpoch);

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
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(false));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
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
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(false));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
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
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(ForceReindex: true, RecreateIndex: true));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
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
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(ForceReindex: true, RecreateIndex: true));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
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

    // A stage that returned its failure instead of throwing still fails the run - and, unlike the
    // throw path, KEEPS its metrics on the report. That is the whole point: a configuration drift
    // is the run where the embedding block is most diagnostic, and the throw path would leave it
    // null (D199 §8b item 3).
    [TestMethod]
    public async Task RunOrchestrator_EmbedStageReturnsFailure_MarksRunFailedAndKeepsTheEmbeddingBlock()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(ForceReindex: true));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
        context.Setup(c => c.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ExtractStats());
        context.Setup(c => c.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ChunkingStageMetrics.Empty("v1"));
        context.Setup(c => c.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(EmbedStats() with
            {
                DocsWithheld      = 3711,
                DocumentsWithheld = 51,
                Failure           = new StageFailure(typeof(TotalWithholdException).FullName!, "withheld everything")
                {
                    IsDimensionDrift = true,
                },
            });
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        var function = deps.Build();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => function.RunOrchestrator(context.Object));

        context.Verify(c => c.CallActivityAsync("SaveIndexReportActivity",
            It.Is<PdfIndexRunReport>(r =>
                !r.Success
                && r.Run!.ErrorType == typeof(TotalWithholdException).FullName
                && r.Embedding != null
                && r.Embedding.DocsWithheld == 3711
                && r.Embedding.DocumentsWithheld == 51),
            It.IsAny<TaskOptions>()), Times.Once);
    }

    // The on-demand path (POST /api/index without ?recreate=true) must leave the live index
    // in place - a run that wipes it when it was not asked to is the expensive mistake here.
    [TestMethod]
    public async Task RunOrchestrator_RecreateIndexNotRequested_DoesNotRecreateIndex()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(ForceReindex: true));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig());
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

        var result = await function.ExtractActivity(new ExtractRequest(false, "extracted.json", "stale-ids.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow), context);

        // Returned unchanged EXCEPT for the two fields this activity fills in on the way out:
        // the stale id list is stripped (Durable row-size limit) and its count is kept in its
        // place, so a report can tell "the orphan cleanup found nothing" from "nothing was
        // stale, so it never ran" (2026-09-17).
        Assert.AreEqual(stats with { StaleDocumentIds = [], StaleDocumentCount = 0 }, result);
        Assert.AreEqual(0, result.StaleDocumentCount);

        // The processed-document list is written for the snapshot's drop set (D200 R1). Without
        // it the snapshot never drops superseded rows and orphan eviction is silently dead.
        deps.BlobStore.Verify(b => b.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "processed-ids.json", It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<System.Text.Json.JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Index provisioning moved to PreflightActivity (D201) and must not happen here as well -
        // two writers of the same object is what that change removed, and a second get-or-create
        // here is also what could quietly resurrect an index a recreate had just dropped.
        deps.IndexService.Verify(s => s.EnsureIndexAsync(), Times.Never);
        // Three writes now: extracted docs, stale ids, and the processed-document list the
        // snapshot uses as its drop set (D200 R1). One container assert each.
        deps.BlobStore.Verify(b => b.AssertContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
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
            function.ExtractActivity(new ExtractRequest(false, "extracted.json", "stale-ids.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow), context));

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
            function.ExtractActivity(new ExtractRequest(false, "extracted.json", "stale-ids.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow), context));
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

        var result = await function.ChunkActivity(new ChunkRequest("extracted.json", "chunks.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context);

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
            function.ChunkActivity(new ChunkRequest("extracted.json", "chunks.json", "family-moves.json", "instance-1", DateTimeOffset.UtcNow), context));

        StringAssert.Contains(ex.Message, "ChunkActivity failed");
    }

    // ── PreflightActivity (D201) ─────────────────────────────────────────────

    // Provisioning must come FIRST: ReadVectorConfigAsync cannot describe an index that does not
    // exist, which is the first run in a fresh environment - the path nobody re-runs.
    [TestMethod]
    public async Task PreflightActivity_EnsuresTheIndexBeforeReadingItsWidth()
    {
        var deps  = new Deps();
        var order = new List<string>();
        deps.IndexService.Setup(s => s.EnsureIndexAsync()).Callback(() => order.Add("ensure")).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.ReadVectorConfigAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("read")).ReturnsAsync(VectorConfig());

        var result = await deps.Build().PreflightActivity("instance-1", new FakeFunctionContext());

        CollectionAssert.AreEqual(new[] { "ensure", "read" }, order);
        Assert.AreEqual(3072, result.Dimensions);
    }

    // Fails rather than falling back to OPENAI_EMBEDDING_DIMENSIONS. A fallback would silently
    // reinstate the config-as-proxy path this replaced, and the run would be validated against a
    // claim rather than against the index - indistinguishable afterwards from a correct run.
    [TestMethod]
    public async Task PreflightActivity_VectorFieldAbsent_FailsTheRun()
    {
        var deps = new Deps();
        deps.IndexService.Setup(s => s.EnsureIndexAsync()).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.ReadVectorConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VectorConfig() with { FieldPresent = false, Dimensions = null });

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            deps.Build().PreflightActivity("instance-1", new FakeFunctionContext()));

        StringAssert.Contains(ex.Message, "PreflightActivity failed");
    }

    // Same rule for a present field with no readable width - "0 wide" is not a width to judge
    // vectors against.
    [TestMethod]
    public async Task PreflightActivity_WidthUnreadable_FailsTheRun()
    {
        var deps = new Deps();
        deps.IndexService.Setup(s => s.EnsureIndexAsync()).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.ReadVectorConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VectorConfig() with { Dimensions = 0 });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            deps.Build().PreflightActivity("instance-1", new FakeFunctionContext()));
    }

    // The width the embed/upload stage is judged against is the one preflight read, not the
    // configured one - that is the whole point of D201, so it is pinned at the seam.
    [TestMethod]
    public async Task RunOrchestrator_PassesThePreflightWidthToTheEmbedStage()
    {
        var deps    = new Deps();
        var context = MockOrchestrationContext();
        context.Setup(c => c.GetInput<IndexRequest>()).Returns(new IndexRequest(false));
        context.Setup(c => c.CallActivityAsync<IndexVectorConfig>("PreflightActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(VectorConfig(dims: 1536));
        context.Setup(c => c.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ExtractStats());
        context.Setup(c => c.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(ChunkingStageMetrics.Empty("v1"));
        context.Setup(c => c.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(EmbedStats());
        context.Setup(c => c.CallActivityAsync("SaveIndexReportActivity", It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        await deps.Build().RunOrchestrator(context.Object);

        context.Verify(c => c.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity",
            It.Is<EmbedUploadRequest>(r => r.VectorDimensions == 1536), It.IsAny<TaskOptions>()), Times.Once);

        // And the report carries that same read rather than a second one taken later.
        context.Verify(c => c.CallActivityAsync("SaveIndexReportActivity",
            It.Is<PdfIndexRunReport>(r => r.VectorConfig != null && r.VectorConfig.Dimensions == 1536),
            It.IsAny<TaskOptions>()), Times.Once);
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
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, ChunksTruncated: 0, EmbeddingRetries: 0, VectorDimErrors: 0, CacheHits: 1));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult(DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: []));
        deps.SnapshotService.Setup(s => s.UpdateAsync(
                "pdf", It.IsAny<IReadOnlyList<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), "instance-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotLiveSet(
                new HashSet<string> { "hash1" },
                new HashSet<string> { "doc1.pdf" }));
        deps.VectorCache.Setup(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorCacheEviction(Listed: 5, Deleted: 2, ListMs: 30, DeleteMs: 40, BlobBytesTotal: 200_000, BlobBytesP50: 40_000,
                DeleteLatency: new LatencySummary(Count: 2, P50Ms: 15.0, P95Ms: 25.0, MaxMs: 25.0)));
        deps.IdentityStore.Setup(s => s.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(new EmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow, 4), context);

        Assert.AreEqual(1, result.DocsUploaded);
        Assert.AreEqual(1, result.VectorCacheHits);
        deps.VectorCache.Verify(c => c.EvictOrphanedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        // The eviction split rides the report as the cache reported it (D203 M5a): ChunksEvicted
        // keeps meaning "deleted", the listing and clocks land beside it, and the identity store
        // gets its own clock rather than hiding inside EvictionDurationMs.
        Assert.AreEqual(2, result.ChunksEvicted);
        Assert.AreEqual(5, result.VectorCacheListedBlobs);
        Assert.AreEqual(30L, result.VectorCacheListMs);
        Assert.AreEqual(40L, result.VectorCacheDeleteMs);
        Assert.AreEqual(200_000L, result.VectorCacheBlobBytesTotal);
        Assert.AreEqual(40_000L, result.VectorCacheBlobBytesP50);
        Assert.IsNotNull(result.IdentityEvictionMs);
        Assert.IsNotNull(result.EvictionDurationMs);
        // The per-op latency block is always present and carries the cache's delete summary
        // (D203 §6c); the embed-side kinds are null here because the mocked embed result has none.
        Assert.IsNotNull(result.VectorCacheOpLatency);
        Assert.AreEqual(2, result.VectorCacheOpLatency!.Delete!.Count);
        Assert.AreEqual(15.0, result.VectorCacheOpLatency.Delete.P50Ms);
        Assert.IsNull(result.VectorCacheOpLatency.GetHit);

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
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult(DocsUploaded: 1, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0, IndexDocumentCountSnapshot: 10, IndexStorageSizeBytesSnapshot: 100, RedFlags: []));
        deps.SnapshotService.Setup(s => s.UpdateAsync(
                "pdf", It.IsAny<IReadOnlyList<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), "instance-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotLiveSet(new HashSet<string>(), new HashSet<string>()));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(new EmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow, 4), context);

        Assert.AreEqual(1, result.DocsUploaded);
        deps.UploadService.Verify(s => s.UploadDocumentsAsync(
            It.IsAny<IEnumerable<ChunkObject>>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.Is<IReadOnlyList<FamilyMove>>(moves => moves.Count == 0),
            It.IsAny<int>(),
            It.IsAny<bool>(),
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
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            function.EmbedAndUploadActivity(new EmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow, 4), context));

        StringAssert.Contains(ex.Message, "EmbedAndUploadActivity failed");
    }

    // The one failure this stage can DESCRIBE is returned as data rather than thrown, because the
    // detail would not survive the Durable boundary otherwise: in the isolated worker the
    // orchestrator receives TaskFailedException with FailureDetails, where the type is a string
    // and custom properties are gone (D199 §8b item 3).
    [TestMethod]
    public async Task EmbedAndUploadActivity_TotalWithhold_ReturnsPopulatedMetricsInsteadOfThrowing()
    {
        var deps   = new Deps();
        var chunks = new List<ChunkObject> { Chunk() };
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<ChunkObject>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        deps.BlobStore.Setup(b => b.DownloadJsonAsync<List<string>>(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        deps.EmbeddingService.Setup(s => s.EmbedDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingRunResult(chunks, 0, 0, 0, 0));
        deps.ArtifactWriter.Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.UploadService.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TotalWithholdException(
                totalChunks: 3711, distinctDocuments: 51, expectedDimensions: 3072,
                verdictCounts: new Dictionary<string, int> { ["WrongWidth"] = 3711 },
                isDimensionDrift: true));
        var function = deps.Build();
        var context  = new FakeFunctionContext();

        var result = await function.EmbedAndUploadActivity(
            new EmbedUploadRequest("chunks.json", "stale-ids.json", "family-moves.json", "processed-ids.json", "instance-1", DateTimeOffset.UtcNow, 4), context);

        // The payoff: a drift reports like an ordinary run that withheld everything, so it is
        // visible in the report's own columns rather than only inside a stringified exception.
        Assert.AreEqual(0, result.DocsUploaded);
        Assert.AreEqual(3711, result.DocsFailed);
        Assert.AreEqual(3711, result.DocsWithheld);
        Assert.AreEqual(51, result.DocumentsWithheld);

        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(typeof(TotalWithholdException).FullName, result.Failure!.ExceptionType,
            "must match the fully-qualified form the other failure path writes from FailureDetails");
        Assert.IsTrue(result.Failure.IsDimensionDrift);
        Assert.AreEqual(3072, result.Failure.ExpectedDimensions);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, int> { ["WrongWidth"] = 3711 },
            (Dictionary<string, int>)result.Failure.VerdictCounts!);
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

        // Not the exact instance any more: the activity enriches the report with the
        // report-time stats readback (observability plan 4.1) before writing it.
        deps.ReportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("instance-1")),
            It.Is<PdfIndexRunReport>(r => r.Run.InstanceId == "instance-1" && r.StatsReadback != null),
            It.IsAny<CancellationToken>()), Times.Once);
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
        PageSpans:             [new PageSpan(1, 0, "content".Length, null)],
        PageBreadcrumbs:       new Dictionary<int, string>(),
        Sections:              [],
        Headings:              [],
        Boilerplate:           [],
        Tables:                [],
        Figures:               [],
        Annotations:      [],
        Hyperlinks:       [],
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
