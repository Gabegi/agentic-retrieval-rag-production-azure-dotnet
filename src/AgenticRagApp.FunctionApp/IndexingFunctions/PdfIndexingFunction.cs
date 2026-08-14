using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Functions;

// PDF indexing entrypoint - Durable Functions orchestrator driving the
// extract/chunk/embed-and-upload pipeline.
//
// Sibling classes hold the operations around this pipeline rather than the pipeline itself:
// IndexingStatusFunction (progress of a run in flight), IndexRestoreFunction (wipe and
// repopulate from snapshot), IndexAdminFunction (destructive recreate, knowledge-base setup).
//
// Payload pattern: extracted docs, chunks, and stale document IDs are all written to blob
// (container: indexing-pipeline, paths: {date}/{instanceId}/extracted.json,
// {date}/{instanceId}/chunks.json, {date}/{instanceId}/stale-document-ids.json). Only the blob name string travels through
// Durable Table Storage, avoiding the 64KB row-size limit - ExtractActivity's own return value
// is stripped of the raw stale-ID list for the same reason (see ExtractActivity).
public class PdfIndexingFunction
{
    // Scopes the rolling snapshot and drift baseline to this doc-type - PDF and CSV must
    // never share or merge either one.
    private const string Source = "pdf";

    private readonly IExtractionService        _extractionService;
    private readonly IChunkingService          _chunkingService;
    private readonly IEmbeddingService         _embeddingService;
    private readonly IUploadService            _uploadService;
    private readonly IIndexService             _indexService;
    private readonly BlobContainerClient       _pipelineContainer;
    private readonly IBlobStore                _blobStore;
    private readonly IRunReportWriter          _reportWriter;
    private readonly IPipelineArtifactWriter   _artifactWriter;
    private readonly ISnapshotService          _snapshotService;
    private readonly IVectorCache              _vectorCache;
    private readonly IDocumentIdentityStore    _identityStore;
    private readonly ILogger<PdfIndexingFunction> _logger;

    public PdfIndexingFunction(
        IExtractionService        extractionService,
        IChunkingService          chunkingService,
        IEmbeddingService         embeddingService,
        IUploadService            uploadService,
        IIndexService             indexService,
        [FromKeyedServices("pipeline-temp")] BlobContainerClient pipelineContainer,
        IBlobStore                blobStore,
        IRunReportWriter          reportWriter,
        IPipelineArtifactWriter   artifactWriter,
        ISnapshotService          snapshotService,
        IVectorCache              vectorCache,
        IDocumentIdentityStore    identityStore,
        ILogger<PdfIndexingFunction> logger)
    {
        _extractionService = extractionService;
        _chunkingService   = chunkingService;
        _embeddingService  = embeddingService;
        _uploadService     = uploadService;
        _indexService      = indexService;
        _pipelineContainer = pipelineContainer;
        _blobStore         = blobStore;
        _reportWriter      = reportWriter;
        _artifactWriter    = artifactWriter;
        _snapshotService   = snapshotService;
        _vectorCache       = vectorCache;
        _identityStore     = identityStore;
        _logger            = logger;
    }

    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var forceReindex = req.Query["force"] == "true";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "IndexingOrchestrator", new PdfIndexRequest(forceReindex));
        _logger.LogInformation("Indexing started — instance {InstanceId}", instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    // Manual-upload path (Zenya source connection isn't live yet) - the timer provides the
    // cadence, ExtractAsync's own new/updated diff is the "check for changes" step, so no
    // separate polling logic is needed here. Fixed instance ID makes this a singleton: a run
    // longer than a day just causes the next tick to skip rather than overlap, which is what
    // keeps SnapshotService's read-merge-write safe - runs never race each other.
    //
    // Once daily at 22:00 - relies on WEBSITE_TIME_ZONE = "W. Europe Standard Time"
    // (function_app.tf) so this means 22:00 Dutch wall-clock time, not UTC.
    [Function("ScheduledIndexing")]
    public async Task RunScheduled(
        [TimerTrigger("0 0 22 * * *")] TimerInfo timer,
        [DurableClient] DurableTaskClient client)
    {
        const string instanceId = "PdfIndexing";

        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: false);
        if (existing is null
            || existing.RuntimeStatus is OrchestrationRuntimeStatus.Completed
                or OrchestrationRuntimeStatus.Failed
                or OrchestrationRuntimeStatus.Terminated)
        {
            // TODO: remove once this is in production - forcing a full reindex on every
            // scheduled run is a development-only setting; the daily run should use the
            // new/updated diff (ForceReindex: false) once the index is stable.
            await client.ScheduleNewOrchestrationInstanceAsync(
                "IndexingOrchestrator", new PdfIndexRequest(ForceReindex: true),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
    }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;
        var input     = context.GetInput<PdfIndexRequest>()!;
        // Dated like the artifact/report paths below so today's run's temp files can be found
        // by browsing without already knowing the instance ID.
        var docsBlob     = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/extracted.json";
        var chunksBlob   = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/chunks.json";
        var staleIdsBlob = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/stale-document-ids.json";

        ExtractionStageMetrics?  extractResults = null;
        ChunkingStageMetrics?       chunkResults   = null;
        EmbedUploadStageMetrics? embedResults   = null;
        bool    success = false;
        string? error   = null;

        // Stage-boundary progress, readable while the run is in flight via GET /api/index/status
        // (or the raw statusQueryGetUri's "customStatus"). SetCustomStatus is replay-safe -
        // Durable overwrites the value rather than accumulating, so a replayed orchestration
        // just rewrites the same sequence. Counts are carried forward from the stage that
        // measured them, so a terminal run's status still shows what it produced.
        context.SetCustomStatus(new IndexingProgress(IndexingProgress.Extracting, startedAt));

        try
        {
            extractResults = await context.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity",        new PdfExtractRequest(input.ForceReindex, docsBlob, staleIdsBlob, context.InstanceId, startedAt));
            context.SetCustomStatus(new IndexingProgress(IndexingProgress.Chunking, startedAt,
                DocsExtracted: extractResults.DocsToProcess));

            chunkResults   = await context.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity",               new PdfChunkRequest(docsBlob, chunksBlob, context.InstanceId, startedAt));
            context.SetCustomStatus(new IndexingProgress(IndexingProgress.EmbedAndUpload, startedAt,
                DocsExtracted: extractResults.DocsToProcess, ChunksProduced: chunkResults.ChunksProduced));

            embedResults   = await context.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", new PdfEmbedUploadRequest(chunksBlob, staleIdsBlob, context.InstanceId, startedAt));
            success      = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        context.SetCustomStatus(new IndexingProgress(
            success ? IndexingProgress.Completed : IndexingProgress.Failed, startedAt,
            DocsExtracted:  extractResults?.DocsToProcess,
            ChunksProduced: chunkResults?.ChunksProduced,
            DocsUploaded:   embedResults?.DocsUploaded));

        // Always call the activity — checking _reportWriter.IsEnabled here would be an
        // injected-dependency read inside orchestrator code, which Durable Functions'
        // determinism rules warn against. The activity itself is the right place to check.
        await context.CallActivityAsync("SaveIndexReportActivity",
            new PdfIndexRunReport
            {
                Run = new RunIdentity(
                    context.InstanceId, startedAt, context.CurrentUtcDateTime,
                    input.ForceReindex, success, error),
                Extraction = extractResults,
                Chunking   = chunkResults,
                Embedding  = embedResults,
            });

        if (!success)
            throw new InvalidOperationException(error ?? "Indexing pipeline failed");
    }

    // Step 1 — ensure index exists, run the extractor, serialise docs to blob, return stats
    //
    // This whole step is ONE Durable activity, and PdfExtractionPipeline fans out internally
    // via Parallel.ForEachAsync (MaxExtractionParallelism = 8) rather than one CallActivityAsync
    // per document. Durable only checkpoints at activity-call boundaries in the orchestrator, so
    // a host death partway through this activity (EP1 scale-in/recycle, deployment, OOM) causes
    // Durable to redeliver and rerun the whole activity from scratch - every document's Document
    // Intelligence analysis already completed in that invocation gets re-submitted and re-billed,
    // not just whatever was in flight at the moment of death. This is a deliberate POC trade-off,
    // not an oversight: for a low-frequency-restart POC, the cost is an occasional rerun's worth
    // of pages, which is cheap against restructuring the orchestrator. The fix, if this ever
    // needs revisiting, is per-document fan-out in the orchestrator (Task.WhenAll over one
    // CallActivityAsync per document instead of Parallel.ForEachAsync here), which also changes
    // the output shape to per-document and needs RetryOptions + maxConcurrentActivityFunctions
    // decided deliberately - see the extraction-optimisation review thread for the full design.
    [Function("ExtractActivity")]
    public async Task<ExtractionStageMetrics> ExtractActivity([ActivityTrigger] PdfExtractRequest req, FunctionContext context)
    {
        try
        {
            await _indexService.EnsureIndexAsync();
            // req.InstanceId threaded through so this run's validation/file-facts/diff/failure
            // reports are named by instance, not just by wall clock - see StageReportPath.
            var (docs, stats) = await _extractionService.ExtractAsync(
                req.ForceReindex, req.InstanceId, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);
            await WriteBlobAsync(req.StaleIdsBlob, stats.StaleDocumentIds, context.CancellationToken);

            await _artifactWriter.WriteArtifactAsync(
                ReportPath.Build(req.StartedAt, "extraction-artifact", req.InstanceId), new { Docs = docs, Stats = stats }, context.CancellationToken);

            _logger.LogInformation("Extracted {Count} docs → {Blob}", docs.Count, req.OutputBlob);

            // Stale IDs already went to req.StaleIdsBlob above; stripped here so they don't
            // also ride along on this activity's own Durable-persisted return value - see
            // the class comment and finding #3 of the 2026-07-29 extraction review.
            return stats with { StaleDocumentIds = [] };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "extract"));
            _logger.LogError(ex, "ExtractActivity failed");
            throw new InvalidOperationException($"ExtractActivity failed: {ex}");
        }
    }

    // Step 2 — read ExtractionDocuments, chunk, serialise DocumentChunks to blob; return stats
    [Function("ChunkActivity")]
    public async Task<ChunkingStageMetrics> ChunkActivity([ActivityTrigger] PdfChunkRequest req, FunctionContext context)
    {
        try
        {
            var docs           = await ReadBlobAsync<List<PdfExtractionDocument>>(req.InputBlob, context.CancellationToken);

            // The chunking-artifact report is written by ChunkingService itself, not here:
            // it covers the whole stage (identity resolution, routing, heading location,
            // chunks) and has to be written even when the stage throws, which this method
            // cannot do - the exception passes straight through it. Hence instanceId and
            // startedAt travelling in.
            var (chunks, stats) = await _chunkingService.ChunkDocumentsAsync(
                docs, req.InstanceId, req.StartedAt, context.CancellationToken);

            await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);

            _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks → {Blob}", docs.Count, chunks.Count, req.OutputBlob);
            return stats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "chunk"));
            _logger.LogError(ex, "ChunkActivity failed for '{InputBlob}'", req.InputBlob);
            throw new InvalidOperationException($"ChunkActivity failed: {ex}");
        }
    }

    // Step 3 — read DocumentChunks, embed then upload to Azure AI Search; return combined stats
    [Function("EmbedAndUploadActivity")]
    public async Task<EmbedUploadStageMetrics> EmbedAndUploadActivity([ActivityTrigger] PdfEmbedUploadRequest req, FunctionContext context)
    {
        try
        {
            var chunks         = await ReadBlobAsync<List<DocumentChunk>>(req.ChunksBlob, context.CancellationToken);
            var staleDocumentIds = await ReadBlobAsync<List<string>>(req.StaleIdsBlob, context.CancellationToken);
            LogProcessMemory("chunks loaded", chunks.Count);

            var sw              = System.Diagnostics.Stopwatch.StartNew();
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(chunks, context.CancellationToken);
            sw.Stop();
            LogProcessMemory("embedding complete", chunks.Count);

            // Materialized once (Documents is a lazy cached+fresh concat) - reused below for
            // upload and the rolling snapshot, not re-enumerated per use.
            var embeddedDocs = embeddingResult.Documents.ToList();

            // Metadata only, never the raw vectors (~12KB+ per chunk, and not useful to read
            // back as JSON anyway) - the actual vector for a given hash lives once in the
            // vector cache (VectorCache), not duplicated here.
            var chunkSummaries = embeddedDocs
                .Select(d => new { d.Id, d.DocumentId, d.ContentHash, Dims = d.ContentVector?.Length });
            await _artifactWriter.WriteArtifactAsync(
                ReportPath.Build(req.StartedAt, "embedding-artifact", req.InstanceId),
                new
                {
                    Chunks = chunkSummaries,
                    Stats  = new
                    {
                        embeddingResult.ChunksTruncated,
                        embeddingResult.EmbeddingRetries,
                        embeddingResult.VectorDimErrors,
                        embeddingResult.CacheHits,
                    },
                },
                context.CancellationToken);

            var uploadResult = await _uploadService.UploadDocumentsAsync(
                embeddedDocs, staleDocumentIds, context.CancellationToken);
            LogProcessMemory("upload complete", chunks.Count);

            // Rolling full-corpus snapshot (source-scoped) + the two evictions that ride along
            // with it. Best-effort against uploadResult.DocsFailed - a chunk that failed to
            // upsert is still folded into the snapshot as if it succeeded (UploadService
            // doesn't report which specific chunks failed, only the count) - rare,
            // self-corrects whenever that document is next reprocessed.
            var live = await _snapshotService.UpdateAsync(
                Source, embeddedDocs, staleDocumentIds, req.InstanceId, req.StartedAt, context.CancellationToken);

            var evictedCount = await _vectorCache.EvictOrphanedAsync(live.ContentHashes, context.CancellationToken);
            if (evictedCount > 0)
                _logger.LogInformation("Vector cache eviction — {Count} orphaned entr{Suffix} deleted",
                    evictedCount, evictedCount == 1 ? "y" : "ies");

            // Same treatment for the identity store, which until now was the one corpus-scoped
            // store that never forgot a deleted document. A ghost identity record keeps
            // clustering: single-linkage means one sitting between two live documents merges
            // their families, and it can even be the family's id.
            var evictedIdentities = await _identityStore.EvictOrphanedAsync(live.DocumentIds, context.CancellationToken);
            if (evictedIdentities > 0)
                _logger.LogInformation("Identity store eviction — {Count} orphaned record(s) deleted",
                    evictedIdentities);

            await DeleteBlobAsync(req.ChunksBlob, context.CancellationToken);
            await DeleteBlobAsync(req.StaleIdsBlob, context.CancellationToken);

            return new EmbedUploadStageMetrics(
                DocsUploaded:                  uploadResult.DocsUploaded,
                DocsFailed:                    uploadResult.DocsFailed,
                ChunksRemoved:                 uploadResult.ChunksRemoved,
                ChunksTruncated:               embeddingResult.ChunksTruncated,
                EmbeddingRetries:              embeddingResult.EmbeddingRetries,
                VectorDimErrors:               embeddingResult.VectorDimErrors,
                VectorCacheHits:               embeddingResult.CacheHits,
                TotalEmbeddingDurationMs:      sw.ElapsedMilliseconds,
                IndexDocumentCountSnapshot:    uploadResult.IndexDocumentCountSnapshot,
                IndexStorageSizeBytesSnapshot: uploadResult.IndexStorageSizeBytesSnapshot,
                RedFlags:                      uploadResult.RedFlags,
                ChunksEvicted:                 evictedCount,
                PreviousIndexDocumentCount:    uploadResult.PreviousIndexDocumentCount,
                PreviousIndexStorageSizeBytes: uploadResult.PreviousIndexStorageSizeBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "EmbedAndUploadActivity failed for '{ChunksBlob}'", req.ChunksBlob);
            throw new InvalidOperationException($"EmbedAndUploadActivity failed: {ex}");
        }
    }

    [Function("SaveIndexReportActivity")]
    public async Task SaveIndexReportActivity([ActivityTrigger] PdfIndexRunReport report, FunctionContext context)
    {
        LogRunSummary(report);

        if (!_reportWriter.IsEnabled) return;

        // Path built by RunReportPath, which also owns the parser the email trigger uses to
        // recognise this blob - writer and reader must agree exactly, so they share one file.
        // Historical reports stay at the old indexing/{date}/ prefix; nothing migrates them.
        await _reportWriter.WriteReportAsync(
            RunReportPath.Build(RunReportKind.Index, report.Run.StartedAt, report.Run.InstanceId),
            report, context.CancellationToken);
        _logger.LogInformation(
            "Index run report saved — instance={InstanceId}, docs={Docs}, chunks={Chunks}, success={Success}",
            report.InstanceId, report.DocsToProcess, report.ChunksProduced, report.Success);
    }

    // One greppable, alertable line per run, emitted from the final activity so it lands
    // exactly once (orchestrator-body logging would repeat on every replay). Deliberately
    // ahead of the IsEnabled guard above: the run finished either way, and the fact that it
    // finished shouldn't depend on report writing being switched on.
    //
    // Logged at Error on failure so an App Insights alert rule can key off severity rather
    // than having to parse success= out of the message.
    private void LogRunSummary(PdfIndexRunReport report)
    {
        const string template =
            "INDEXING RUN FINISHED — instance={InstanceId} success={Success} duration={DurationSeconds}s " +
            "force={ForceReindex} docs={Docs} chunks={Chunks} uploaded={Uploaded} failed={Failed} " +
            "redFlags={RedFlags} error={Error}";

        var duration = (report.Run.FinishedAt - report.Run.StartedAt).TotalSeconds;
        // Both stages' red flags, since either can be null when that stage never ran.
        var redFlags = (report.Extraction?.RedFlags.Count ?? 0) + (report.Embedding?.RedFlags.Count ?? 0);
        var failed   = report.Embedding?.DocsFailed ?? 0;

        if (report.Success)
            _logger.LogInformation(template,
                report.InstanceId, true, duration, report.Run.ForceReindex,
                report.DocsToProcess, report.ChunksProduced, report.DocsUploaded, failed, redFlags, null);
        else
            _logger.LogError(template,
                report.InstanceId, false, duration, report.Run.ForceReindex,
                report.DocsToProcess, report.ChunksProduced, report.DocsUploaded, failed, redFlags,
                report.ErrorMessage);
    }

    private async Task WriteBlobAsync<T>(string blobPath, T data, CancellationToken ct)
    {
        await _blobStore.AssertContainerExistsAsync(_pipelineContainer, ct);
        await _blobStore.UploadJsonAsync(_pipelineContainer, blobPath, data, ct: ct);
    }

    private Task<T> ReadBlobAsync<T>(string blobPath, CancellationToken ct) =>
        _blobStore.DownloadJsonAsync<T>(_pipelineContainer, blobPath, ct);

    private Task DeleteBlobAsync(string blobPath, CancellationToken ct) =>
        _blobStore.DeleteIfExistsAsync(_pipelineContainer, blobPath, ct);

    // WorkingSet is the whole process's OS-level footprint (managed heap + native +
    // embedding vector arrays) - what actually counts against the EP1 plan's 3.5GB
    // ceiling. GC.GetTotalMemory is logged alongside it only to show how much of that
    // is the managed heap specifically, e.g. to tell "vectors held in memory" apart
    // from "native/runtime overhead" if the working set number looks high.
    private void LogProcessMemory(string stage, int chunkCount) =>
        _logger.LogInformation(
            "Memory @ {Stage} — {Chunks} chunks, working set {WorkingSetMb} MB, managed heap {HeapMb} MB",
            stage, chunkCount, Environment.WorkingSet / 1024 / 1024, GC.GetTotalMemory(false) / 1024 / 1024);
}
