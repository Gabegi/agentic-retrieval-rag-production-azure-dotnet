using System.Net;
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
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Services;

namespace AgenticRagApp.Functions;

// PDF indexing entrypoint - Durable Functions orchestrator driving the
// extract/chunk/embed-and-upload pipeline.
//
// Payload pattern: extracted docs and chunks are written to blob (container: indexing-pipeline,
// paths: {instanceId}/extracted.json and {instanceId}/chunks.json). Only the blob name string
// travels through Durable Table Storage, avoiding the 64KB row-size limit.
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
    private readonly IKnowledgeService         _knowledgeService;
    private readonly BlobContainerClient       _pipelineContainer;
    private readonly IBlobStore                _blobStore;
    private readonly IRunReportWriter          _reportWriter;
    private readonly IPipelineArtifactWriter   _artifactWriter;
    private readonly ISnapshotService          _snapshotService;
    private readonly IVectorCache              _vectorCache;
    private readonly IRestoreService           _restoreService;
    private readonly ILogger<PdfIndexingFunction> _logger;

    public PdfIndexingFunction(
        IExtractionService        extractionService,
        IChunkingService          chunkingService,
        IEmbeddingService         embeddingService,
        IUploadService            uploadService,
        IIndexService             indexService,
        IKnowledgeService         knowledgeService,
        [FromKeyedServices("pipeline-temp")] BlobContainerClient pipelineContainer,
        IBlobStore                blobStore,
        IRunReportWriter          reportWriter,
        IPipelineArtifactWriter   artifactWriter,
        ISnapshotService          snapshotService,
        IVectorCache              vectorCache,
        IRestoreService           restoreService,
        ILogger<PdfIndexingFunction> logger)
    {
        _extractionService = extractionService;
        _chunkingService   = chunkingService;
        _embeddingService  = embeddingService;
        _uploadService     = uploadService;
        _indexService      = indexService;
        _knowledgeService  = knowledgeService;
        _pipelineContainer = pipelineContainer;
        _blobStore         = blobStore;
        _reportWriter      = reportWriter;
        _artifactWriter    = artifactWriter;
        _snapshotService   = snapshotService;
        _vectorCache       = vectorCache;
        _restoreService    = restoreService;
        _logger            = logger;
    }

    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var forceReindex = req.Query["force"] == "true";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "IndexingOrchestrator", new IndexRequest(forceReindex));
        _logger.LogInformation("Indexing started — instance {InstanceId}", instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    // Manual-upload path (Zenya source connection isn't live yet) - the timer provides the
    // cadence, ExtractAsync's own new/updated diff is the "check for changes" step, so no
    // separate polling logic is needed here. Fixed instance ID makes this a singleton: a run
    // longer than the timer interval just causes the next tick(s) to skip rather than overlap,
    // which is what keeps SnapshotService's read-merge-write safe - runs never race each other.
    //
    // Commented out until Document Intelligence's private link is in place - without it every
    // tick fails immediately (PdfExtractionPipeline can't resolve IPdfExtractor), spamming
    // failed orchestration instances every 15 minutes for no benefit. Uncomment once DI is
    // reachable.
    // [Function("ScheduledIndexing")]
    // public async Task RunScheduled(
    //     [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
    //     [DurableClient] DurableTaskClient client)
    // {
    //     const string instanceId = "PdfIndexing";
    //
    //     var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: false);
    //     if (existing is null
    //         || existing.RuntimeStatus is OrchestrationRuntimeStatus.Completed
    //             or OrchestrationRuntimeStatus.Failed
    //             or OrchestrationRuntimeStatus.Terminated)
    //     {
    //         await client.ScheduleNewOrchestrationInstanceAsync(
    //             "IndexingOrchestrator", new IndexRequest(false),
    //             new StartOrchestrationOptions { InstanceId = instanceId });
    //     }
    // }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;
        var input     = context.GetInput<IndexRequest>()!;
        var docsBlob   = $"{context.InstanceId}/extracted.json";
        var chunksBlob = $"{context.InstanceId}/chunks.json";

        ExtractionResults?  extractResults = null;
        ChunkingResults?       chunkResults   = null;
        EmbedUploadingResults? embedResults   = null;
        bool    success = false;
        string? error   = null;

        try
        {
            extractResults = await context.CallActivityAsync<ExtractionResults>("ExtractActivity",        new ExtractRequest(input.ForceReindex, docsBlob, context.InstanceId, startedAt));
            chunkResults   = await context.CallActivityAsync<ChunkingResults>("ChunkActivity",               new ChunkRequest(docsBlob, chunksBlob, context.InstanceId, startedAt));
            embedResults   = await context.CallActivityAsync<EmbedUploadingResults>("EmbedAndUploadActivity", new EmbedUploadRequest(chunksBlob, extractResults.StaleDocumentIds, context.InstanceId, startedAt));
            success      = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        // Always call the activity — checking _reportWriter.IsEnabled here would be an
        // injected-dependency read inside orchestrator code, which Durable Functions'
        // determinism rules warn against. The activity itself is the right place to check.
        await context.CallActivityAsync("SaveIndexReportActivity",
            PdfIndexRunReport.FromResults(
                context.InstanceId, startedAt, context.CurrentUtcDateTime, input.ForceReindex,
                extractResults, chunkResults, embedResults, success, error));

        if (!success)
            throw new InvalidOperationException(error ?? "Indexing pipeline failed");
    }

    // Step 1 — ensure index exists, run the extractor, serialise docs to blob, return stats
    [Function("ExtractActivity")]
    public async Task<ExtractionResults> ExtractActivity([ActivityTrigger] ExtractRequest req, FunctionContext context)
    {
        try
        {
            await _indexService.EnsureIndexAsync();
            var (docs, stats) = await _extractionService.ExtractAsync(
                req.ForceReindex, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);

            await _artifactWriter.WriteArtifactAsync(
                $"{req.StartedAt:yyyy/MM/dd}/{req.InstanceId}/extraction.json", new { Docs = docs, Stats = stats }, context.CancellationToken);

            _logger.LogInformation("Extracted {Count} docs → {Blob}", docs.Count, req.OutputBlob);
            return stats;
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
    public async Task<ChunkingResults> ChunkActivity([ActivityTrigger] ChunkRequest req, FunctionContext context)
    {
        try
        {
            var docs           = await ReadBlobAsync<List<PdfExtractionDocument>>(req.InputBlob, context.CancellationToken);
            var (chunks, stats) = _chunkingService.ChunkDocuments(docs);
            await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);

            await _artifactWriter.WriteArtifactAsync(
                $"{req.StartedAt:yyyy/MM/dd}/{req.InstanceId}/chunking.json", new { Chunks = chunks, Stats = stats }, context.CancellationToken);

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
    public async Task<EmbedUploadingResults> EmbedAndUploadActivity([ActivityTrigger] EmbedUploadRequest req, FunctionContext context)
    {
        try
        {
            var chunks = await ReadBlobAsync<List<DocumentChunk>>(req.ChunksBlob, context.CancellationToken);
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
                $"{req.StartedAt:yyyy/MM/dd}/{req.InstanceId}/embedding.json",
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
                embeddedDocs, req.StaleDocumentIds, context.CancellationToken);
            LogProcessMemory("upload complete", chunks.Count);

            // Rolling full-corpus snapshot (source-scoped) + the vector-cache eviction that
            // rides along with it. Best-effort against uploadResult.DocsFailed - a chunk that
            // failed to upsert is still folded into the snapshot as if it succeeded
            // (UploadService doesn't report which specific chunks failed, only the count) -
            // rare, self-corrects whenever that document is next reprocessed.
            var liveHashes = await _snapshotService.UpdateAsync(
                Source, embeddedDocs, req.StaleDocumentIds, req.InstanceId, context.CancellationToken);
            var evictedCount = await _vectorCache.EvictOrphanedAsync(liveHashes, context.CancellationToken);
            if (evictedCount > 0)
                _logger.LogInformation("Vector cache eviction — {Count} orphaned entr{Suffix} deleted",
                    evictedCount, evictedCount == 1 ? "y" : "ies");

            await DeleteBlobAsync(req.ChunksBlob, context.CancellationToken);

            return new EmbedUploadingResults(
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
                RedFlags:                      uploadResult.RedFlags);
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
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"indexing/{report.StartedAt:yyyy/MM/dd}/{report.StartedAt:HH-mm-ss}.json", report, context.CancellationToken);
        _logger.LogInformation(
            "Index run report saved — instance={InstanceId}, docs={Docs}, chunks={Chunks}, success={Success}",
            report.InstanceId, report.DocsToProcess, report.ChunksProduced, report.Success);
    }

    // Recovery entrypoint, distinct from StartIndexing/force=true: wipes the index outright
    // (RecreateIndexActivity) and repopulates it from the rolling full-corpus snapshot
    // (RestoreFromSnapshotActivity) instead of re-extracting/re-chunking/re-embedding every
    // source document. Use when the index itself is suspected corrupt/incomplete, not just stale.
    [Function("StartRestore")]
    public async Task<HttpResponseData> StartRestore(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index/restore")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync("RestoreOrchestrator", new object());
        _logger.LogWarning("Index restore started — instance {InstanceId}. Index will be wiped and repopulated from the latest snapshot.", instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("RestoreOrchestrator")]
    public async Task RunRestoreOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;

        RestoreResult? result  = null;
        bool           success = false;
        string?        error   = null;

        try
        {
            await context.CallActivityAsync("RecreateIndexActivity");
            result  = await context.CallActivityAsync<RestoreResult>("RestoreFromSnapshotActivity");
            success = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        await context.CallActivityAsync("SaveRestoreReportActivity",
            BuildRestoreReport(context, startedAt, result, success, error));

        if (!success)
            throw new InvalidOperationException(error ?? "Index restore failed");
    }

    [Function("RecreateIndexActivity")]
    public async Task RecreateIndexActivity([ActivityTrigger] object? _, FunctionContext context)
    {
        try
        {
            await _indexService.RecreateIndexAsync();
        }
        catch (Exception ex)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "restore-recreate-index"));
            _logger.LogError(ex, "RecreateIndexActivity failed");
            throw new InvalidOperationException($"RecreateIndexActivity failed: {ex}");
        }
    }

    [Function("RestoreFromSnapshotActivity")]
    public async Task<RestoreResult> RestoreFromSnapshotActivity([ActivityTrigger] object? _, FunctionContext context)
    {
        try
        {
            return await _restoreService.RestoreFromLatestSnapshotAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "restore-upload"));
            _logger.LogError(ex, "RestoreFromSnapshotActivity failed");
            throw new InvalidOperationException($"RestoreFromSnapshotActivity failed: {ex}");
        }
    }

    [Function("SaveRestoreReportActivity")]
    public async Task SaveRestoreReportActivity([ActivityTrigger] PdfRestoreRunReport report, FunctionContext context)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"restore/{report.StartedAt:yyyy/MM/dd}/{report.StartedAt:HH-mm-ss}.json", report, context.CancellationToken);
        _logger.LogInformation(
            "Index restore report saved — instance={InstanceId}, restored={Restored}, success={Success}",
            report.InstanceId, report.ChunksRestored, report.Success);
    }

    private static PdfRestoreRunReport BuildRestoreReport(
        TaskOrchestrationContext context,
        DateTimeOffset           startedAt,
        RestoreResult?           result,
        bool                     success,
        string?                  error) => new(
            InstanceId:                    context.InstanceId,
            StartedAt:                     startedAt,
            FinishedAt:                    context.CurrentUtcDateTime,
            Success:                       success,
            ErrorMessage:                  error,
            SnapshotInstanceId:            result?.SnapshotInstanceId,
            ChunksRestored:                result?.ChunksRestored       ?? 0,
            ChunksMissingVector:           result?.ChunksMissingVector  ?? 0,
            IndexDocumentCountSnapshot:    result?.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot: result?.IndexStorageSizeBytesSnapshot,
            SearchIndexName:               result?.SearchIndexName      ?? "",
            EmbeddingModel:                result?.EmbeddingModel       ?? "",
            EmbeddingDeployment:           result?.EmbeddingDeployment  ?? "");

    [Function("SetupKnowledgeBase")]
    public async Task<HttpResponseData> RunSetupKnowledgeBase(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "setup-knowledge-base")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation("SetupKnowledgeBase triggered");
        await _knowledgeService.EnsureKnowledgeSourceAsync(context.CancellationToken);
        await _knowledgeService.EnsureKnowledgeBaseAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Knowledge source and knowledge base created or updated");
        return response;
    }

    private async Task WriteBlobAsync<T>(string blobPath, T data, CancellationToken ct)
    {
        await _blobStore.EnsureContainerExistsAsync(_pipelineContainer, ct);
        await _blobStore.UploadJsonAsync(_pipelineContainer, blobPath, data, ct);
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

public record IndexRequest(bool ForceReindex);
public record ExtractRequest(bool ForceReindex, string OutputBlob, string InstanceId, DateTimeOffset StartedAt);
public record ChunkRequest(string InputBlob, string OutputBlob, string InstanceId, DateTimeOffset StartedAt);
public record EmbedUploadRequest(string ChunksBlob, IReadOnlyList<string> StaleDocumentIds, string InstanceId, DateTimeOffset StartedAt);
