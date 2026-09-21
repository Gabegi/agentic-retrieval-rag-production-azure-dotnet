using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability;
using AgenticRagApp.Functions.RunAnalysis;
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
public class IndexingFunction
{
    // Source scope of the rolling snapshot and the drift baseline. "pdf" is the only source
    // today - the CSV pipeline that once shared these is archived (docs/archive) - but the
    // scoping stays, so a second doc type can never merge into this one's snapshot or baseline.
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
    private readonly IIndexDocumentService     _indexDocumentService;
    private readonly IndexerConfig             _indexerConfig;
    private readonly ILogger<IndexingFunction> _logger;

    public IndexingFunction(
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
        IIndexDocumentService     indexDocumentService,
        // Reporting input only - the embedding list price the run report's cost figures are
        // computed at. Nothing here bills from it; see IndexerConfig.
        IndexerConfig             indexerConfig,
        ILogger<IndexingFunction> logger)
    {
        _indexerConfig     = indexerConfig;
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
        _indexDocumentService = indexDocumentService;
        _logger            = logger;
    }

    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var forceReindex = req.Query["force"] == "true";
        // Drop and rebuild the index first (RecreateIndexActivity), then repopulate it in the
        // same run. Until 2026-09-17 the ScheduledIndexing timer sent exactly this every day at
        // 17:00; the timer is gone (D200 §6i - the diff-only path works, so a daily full rebuild
        // at 871 CU pages no longer earns its cost) and this flag is now the only way to
        // recreate-and-refill. No ?confirm= guard like FullIndexRecreation's: that one leaves
        // the index empty, this one repopulates it.
        var recreateIndex = req.Query["recreate"] == "true";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "IndexingOrchestrator", new IndexRequest(forceReindex, recreateIndex));
        _logger.LogInformation("Indexing started — instance {InstanceId}, force={Force}, recreate={Recreate}",
            instanceId, forceReindex, recreateIndex);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    // ScheduledIndexing - a 17:00 TimerTrigger sending ForceReindex: true, RecreateIndex: true
    // as the singleton instance "PdfIndexing" - lived here from the manual-upload days until
    // 2026-09-17. Its rebuild-from-scratch semantics were a stand-in for change detection that
    // did not work: the index-state read had returned an empty map since 2026-07-30, so the
    // diff-only path had never once run (D200 §6h). With the read fixed and the incremental run
    // verified (§6i: 51 skipped, nothing billed, 6 s), a daily 871-page Content Understanding
    // rebuild that also left the index empty from 17:00 until refill had nothing left to pay
    // for. Cadence now comes from whatever calls POST /api/index; there is no timer.
    //
    // WEBSITE_TIME_ZONE in function_app.tf existed for that cron and is kept for now - see the
    // comment there.
    //
    // 2026-09-21: a timer is back, but not that one. The old TODO on the removed version named the
    // shape exactly - "the cheap steady-state shape is ForceReindex: false, RecreateIndex: false
    // (diff-only), with the recreate reserved for schema changes via
    // POST /api/index?force=true&recreate=true" - and that is what this is. What the 2026-09-17
    // removal rejected was the daily 871-page rebuild, not having a cadence; with the corpus now
    // arriving by itself from the Zenya sync, no cadence means new documents sit unindexed until
    // someone remembers to POST.
    [Function("ScheduledIndexing")]
    public async Task RunScheduled(
        // 21:00 Dutch wall-clock, not UTC - WEBSITE_TIME_ZONE = "W. Europe Standard Time"
        // (function_app.tf) is what makes that true, so it must not be dropped without moving this.
        // Timed to land after the Zenya sync, which is an ADO cron and therefore UTC-only: 18:00
        // UTC is 20:00 Amsterdam under CEST and 19:00 under CET (zenya-document-sync.yml). 21:00
        // clears both by an hour without needing a seasonal edit on either side.
        [TimerTrigger("0 0 21 * * *")] TimerInfo timer,
        [DurableClient] DurableTaskClient client)
    {
        const string instanceId = "PdfIndexing";

        // Fixed instance ID makes this a singleton: a run still going at the next tick makes that
        // tick skip rather than overlap. That is not tidiness - SnapshotService does a
        // read-merge-write on one blob, so two concurrent runs would lose one side's changes.
        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: false);
        if (existing is null
            || existing.RuntimeStatus is OrchestrationRuntimeStatus.Completed
                or OrchestrationRuntimeStatus.Failed
                or OrchestrationRuntimeStatus.Terminated)
        {
            // Diff-only, both flags false - the whole point of this timer.
            //   ForceReindex: false   already-indexed documents whose blob is not newer are
            //                         skipped, so Content Understanding is billed for new and
            //                         changed documents only (D200 §6i measured the skip path at
            //                         51 skipped, nothing billed, 6 s).
            //   RecreateIndex: false  the index is never dropped, so it keeps answering queries
            //                         throughout the run - the other half of what made the old
            //                         17:00 rebuild expensive.
            // "New only" is the intent but not quite the behaviour, and the difference matters:
            // IndexDiffService also reprocesses documents whose blob is NEWER than the indexed
            // date, and deletes chunks for documents that have left the listing. Change detection
            // is by blob LastModified, not Zenya's zenya_version - that is D202 §5, design only.
            await client.ScheduleNewOrchestrationInstanceAsync(
                "IndexingOrchestrator", new IndexRequest(ForceReindex: false, RecreateIndex: false),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
    }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;
        var input     = context.GetInput<IndexRequest>()!;
        // Dated like the artifact/report paths below so today's run's temp files can be found
        // by browsing without already knowing the instance ID.
        var docsBlob     = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/extracted.json";
        var chunksBlob   = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/chunks.json";
        var staleIdsBlob = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/stale-document-ids.json";
        // Documents this run actually re-extracted. Travels by blob for the same reason the stale
        // list does - only the name goes through Durable Table Storage. Consumed by the snapshot
        // merge as the drop set (D200 R1).
        var processedIdsBlob = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/processed-document-ids.json";
        // Written by chunking, read by upload: documents whose family_id changed because OTHER
        // documents' clustering moved them. Same payload-by-blob-name pattern as the two above,
        // for the same reason - though this list is small, it travels the way its siblings do.
        var familyMovesBlob = $"{startedAt:yyyy/MM/dd}/{context.InstanceId}/family-moves.json";

        ExtractionStageMetrics?  extractResults = null;
        ChunkingStageMetrics?       chunkResults   = null;
        EmbedUploadStageMetrics? embedResults   = null;
        bool    success = false;
        string? error   = null;
        string? errorType = null;
        // The one read of the live index configuration for this run (D201). Threaded into the
        // embed/upload stage as the width every vector is judged against, and onto the report as
        // what the index actually was - rather than being read a second time at report time,
        // where the two could disagree.
        IndexVectorConfig? vectorConfig = null;

        // Wall-clock per stage from CurrentUtcDateTime deltas - the replay-safe clock, so a
        // replayed orchestration recomputes the same numbers from the same history events
        // (observability plan 2.3). A stage that never ran has no key. The histogram recording
        // happens in SaveIndexReportActivity, never here: orchestrator-body meter writes would
        // re-record on every replay.
        var stageDurations = new Dictionary<string, long>();

        // Stage-boundary progress, readable while the run is in flight via GET /api/index/status
        // (or the raw statusQueryGetUri's "customStatus"). SetCustomStatus is replay-safe -
        // Durable overwrites the value rather than accumulating, so a replayed orchestration
        // just rewrites the same sequence. Counts are carried forward from the stage that
        // measured them, so a terminal run's status still shows what it produced.
        context.SetCustomStatus(new IndexingProgress(
            input.RecreateIndex ? IndexingProgress.RecreatingIndex : IndexingProgress.Extracting, startedAt));

        try
        {
            // Shared with RestoreOrchestrator, which declares it (IndexRestoreFunction) -
            // Durable resolves activities by name across the whole app, so both pipelines go
            // through the one IIndexRebuildService.RecreateEmptyAsync teardown/rebuild order
            // rather than a second copy of it. Deliberately inside the try: a failed recreate
            // has to land in the run report like any other stage failure, and it has to abort
            // before extraction, since ExtractActivity's own EnsureIndexAsync would otherwise
            // quietly recreate the index this just dropped and the run would continue.
            if (input.RecreateIndex)
            {
                await context.CallActivityAsync("RecreateIndexActivity");
                context.SetCustomStatus(new IndexingProgress(IndexingProgress.Extracting, startedAt));
            }

            // Before extraction, because extraction is the only step that costs money and this is
            // the step that can say "the index is not readable, stop". After any recreate, because
            // it reads the index the rest of the run will publish to. See PreflightActivity.
            vectorConfig = await context.CallActivityAsync<IndexVectorConfig>("PreflightActivity", context.InstanceId);

            var extractStart = context.CurrentUtcDateTime;
            extractResults = await context.CallActivityAsync<ExtractionStageMetrics>("ExtractActivity",        new ExtractRequest(input.ForceReindex, docsBlob, staleIdsBlob, processedIdsBlob, context.InstanceId, startedAt));
            stageDurations["extract"] = (long)(context.CurrentUtcDateTime - extractStart).TotalMilliseconds;
            context.SetCustomStatus(new IndexingProgress(IndexingProgress.Chunking, startedAt,
                DocsExtracted: extractResults.DocsToProcess));

            var chunkStart = context.CurrentUtcDateTime;
            chunkResults   = await context.CallActivityAsync<ChunkingStageMetrics>("ChunkActivity",               new ChunkRequest(docsBlob, chunksBlob, familyMovesBlob, context.InstanceId, startedAt));
            stageDurations["chunk"] = (long)(context.CurrentUtcDateTime - chunkStart).TotalMilliseconds;
            context.SetCustomStatus(new IndexingProgress(IndexingProgress.EmbedAndUpload, startedAt,
                DocsExtracted: extractResults.DocsToProcess, ChunksProduced: chunkResults.ChunksProduced));

            var embedStart = context.CurrentUtcDateTime;
            embedResults   = await context.CallActivityAsync<EmbedUploadStageMetrics>("EmbedAndUploadActivity", new EmbedUploadRequest(chunksBlob, staleIdsBlob, familyMovesBlob, processedIdsBlob, context.InstanceId, startedAt, vectorConfig!.Dimensions!.Value));
            stageDurations["embed_upload"] = (long)(context.CurrentUtcDateTime - embedStart).TotalMilliseconds;

            // A stage that could describe its own failure returns it instead of throwing, so that
            // the detail survives the Durable boundary as data (see EmbedAndUploadActivity). The
            // run is still a failure - it just keeps its metrics.
            if (embedResults?.Failure is { } failure)
            {
                error     = failure.Message;
                errorType = failure.ExceptionType;
            }
            else
            {
                success = true;
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            // In the isolated worker this catch sees TaskFailedException, never the activity's
            // own exception, so ex.GetType() would name the wrapper on every failed run.
            // FailureDetails.ErrorType is where the real type survives - as a string.
            errorType = ex is Microsoft.DurableTask.TaskFailedException tfe
                ? tfe.FailureDetails.ErrorType
                : ex.GetType().Name;
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
                    input.ForceReindex, success, error, errorType, input.RecreateIndex),
                Extraction = extractResults,
                Chunking   = chunkResults,
                Embedding  = embedResults,
                StageDurationsMs = stageDurations,
                // The preflight read, not a second one at report time (D201): one value per run,
                // and it is the value the embed/upload stage was actually judged against.
                VectorConfig = vectorConfig,
            });

        // After the report is saved, since this reads it back: assembles the run-analysis blob
        // (summary + flags + model assessment) next to the report. Best-effort inside the
        // activity - it catches everything and never fails the run - and called before the
        // failure rethrow below on purpose: a failed run is exactly the run worth analysing.
        // The retry options cover what the activity deliberately does NOT catch: the host
        // recycling mid-activity.
        try
        {
            await context.CallActivityAsync("SaveRunAnalysisActivity",
                new SaveRunAnalysisRequest(RunReportKind.Index, context.InstanceId, startedAt),
                TaskOptions.FromRetryPolicy(new RetryPolicy(
                    maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(30))));
        }
        catch (TaskFailedException ex)
        {
            // Only reachable when all three attempts died at the infrastructure level - the
            // activity itself never throws. The run report is already saved by this point, and
            // a missing analysis blob must never turn a good indexing run into a failed one.
            context.CreateReplaySafeLogger<IndexingFunction>().LogWarning(ex,
                "SaveRunAnalysisActivity failed after retries for {InstanceId} — run analysis blob not written.",
                context.InstanceId);
        }

        if (!success)
            throw new InvalidOperationException(error ?? "Indexing pipeline failed");
    }

    // Step 1 — ensure index exists, run the extractor, serialise docs to blob, return stats
    //
    // This whole step is ONE Durable activity, and ExtractionService fans out internally via
    // Parallel.ForEachAsync (ExtractionService.MaxExtractionParallelism) rather than one
    // CallActivityAsync per document. Durable only checkpoints at activity-call boundaries in the
    // orchestrator, so a host death partway through this activity (EP1 scale-in/recycle,
    // deployment, OOM) causes Durable to redeliver and rerun the whole activity from scratch -
    // every document's Content Understanding analysis already completed in that invocation gets
    // re-submitted and re-billed, not just whatever was in flight at the moment of death. This is
    // a deliberate POC trade-off,
    // not an oversight: for a low-frequency-restart POC, the cost is an occasional rerun's worth
    // of pages, which is cheap against restructuring the orchestrator. The fix, if this ever
    // needs revisiting, is per-document fan-out in the orchestrator (Task.WhenAll over one
    // CallActivityAsync per document instead of Parallel.ForEachAsync here), which also changes
    // the output shape to per-document and needs RetryOptions + maxConcurrentActivityFunctions
    // decided deliberately - the design is in docs/2607/260729/extraction-fanout-proposal.md (D018).
    [Function("ExtractActivity")]
    public async Task<ExtractionStageMetrics> ExtractActivity([ActivityTrigger] ExtractRequest req, FunctionContext context)
    {
        // Scope + span pattern, repeated on each pipeline activity (plan 5.1/2.4): the scope
        // stamps InstanceId/Source onto every log line the stage emits, so one App Insights
        // query returns a run's full story; the span makes the stage a node in the run's
        // transaction view (the ActivitySource was registered and exported from day one -
        // this is the first code that ever starts an activity on it).
        using var _    = _logger.BeginScope(new Dictionary<string, object?> { ["InstanceId"] = req.InstanceId, ["Source"] = Source });
        using var span = Instrumentation.ActivitySource.StartActivity("indexing.extract");
        span?.SetTag("indexing.instance_id", req.InstanceId);
        try
        {
            // Index provisioning moved to PreflightActivity (D201): the live field width has to be
            // read before the paid extraction, and it cannot be read before the index exists, so
            // the two belong together and ahead of this stage. A second EnsureIndexAsync here
            // would also be a second writer of the same object.
            //
            // req.InstanceId threaded through so this run's file-facts/diff/failure
            // reports are named by instance, not just by wall clock - see StageReportPath.
            var (docs, stats) = await _extractionService.ExtractAsync(
                req.ForceReindex, req.InstanceId, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);
            await WriteBlobAsync(req.StaleIdsBlob, stats.StaleDocumentIds, context.CancellationToken);
            // Every document this run re-extracted, INCLUDING any that produced no chunks: the
            // snapshot drops their previous rows, and a document with no chunks left should lose
            // its rows rather than keep superseded ones (D200 R1). Sourced from the extracted
            // docs rather than from the chunks for exactly that reason.
            await WriteBlobAsync(req.ProcessedIdsBlob, docs.Select(d => d.SourceId).ToList(), context.CancellationToken);

            await _artifactWriter.WriteArtifactAsync(
                ReportPath.Build(req.StartedAt, "extraction-artifact", req.InstanceId), new { Docs = docs, Stats = stats }, context.CancellationToken);

            _logger.LogInformation("Extracted {Count} docs → {Blob}", docs.Count, req.OutputBlob);

            // Stale IDs already went to req.StaleIdsBlob above; stripped here so they do not
            // also ride along on this activity's own Durable-persisted return value - see
            // the class comment and finding #3 of the 2026-07-29 extraction review. The COUNT
            // survives (2026-09-17): without it a report cannot tell "the orphan delete found
            // nothing" from "nothing was stale, so it never ran".
            return stats with { StaleDocumentIds = [], StaleDocumentCount = stats.StaleDocumentIds.Count };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "extract"));
            _logger.LogError(ex, "ExtractActivity failed");
            throw new InvalidOperationException($"ExtractActivity failed: {ex}");
        }
    }

    // Step 2 — read ExtractionDocuments, chunk, serialise ChunkObjects to blob; return stats
    [Function("ChunkActivity")]
    public async Task<ChunkingStageMetrics> ChunkActivity([ActivityTrigger] ChunkRequest req, FunctionContext context)
    {
        // Same scope + span pattern as ExtractActivity - see the comment there.
        using var _    = _logger.BeginScope(new Dictionary<string, object?> { ["InstanceId"] = req.InstanceId, ["Source"] = Source });
        using var span = Instrumentation.ActivitySource.StartActivity("indexing.chunk");
        span?.SetTag("indexing.instance_id", req.InstanceId);
        try
        {
            var docs           = await ReadBlobAsync<List<PdfExtractionDocument>>(req.InputBlob, context.CancellationToken);

            // The chunking-artifact report is written by ChunkingService itself, not here:
            // it covers the whole stage (identity resolution, routing, heading location,
            // chunks) and has to be written even when the stage throws, which this method
            // cannot do - the exception passes straight through it. Hence instanceId and
            // startedAt travelling in.
            var (chunks, stats, familyMoves) = await _chunkingService.ChunkDocumentsAsync(
                docs, req.InstanceId, req.StartedAt, context.CancellationToken);

            await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);

            // Always written, empty list included. The upload activity does fall back to "no
            // moves" when the blob is missing (deployment-transition replays), but it logs a
            // warning - so keeping this write unconditional is what makes that warning mean
            // "the write failed" rather than "nothing moved".
            await WriteBlobAsync(req.FamilyMovesBlob, familyMoves, context.CancellationToken);

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

    // Step 3 — read ChunkObjects, embed then upload to Azure AI Search; return combined stats
    [Function("EmbedAndUploadActivity")]
    public async Task<EmbedUploadStageMetrics> EmbedAndUploadActivity([ActivityTrigger] EmbedUploadRequest req, FunctionContext context)
    {
        // Same scope + span pattern as ExtractActivity - see the comment there.
        using var _    = _logger.BeginScope(new Dictionary<string, object?> { ["InstanceId"] = req.InstanceId, ["Source"] = Source });
        using var span = Instrumentation.ActivitySource.StartActivity("indexing.embed_upload");
        span?.SetTag("indexing.instance_id", req.InstanceId);
        try
        {
            var chunks         = await ReadBlobAsync<List<ChunkObject>>(req.ChunksBlob, context.CancellationToken);
            var staleDocumentIds = await ReadBlobAsync<List<string>>(req.StaleIdsBlob, context.CancellationToken);
            // The drop set for the snapshot merge - see SnapshotService.UpdateAsync (D200 R1).
            var processedDocumentIds = await ReadBlobAsync<List<string>>(req.ProcessedIdsBlob, context.CancellationToken);
            // Documents the chunking stage re-homed into a different family. Usually empty, and
            // usually about documents that are NOT in `chunks` - see UploadService.
            var familyMoves    = await ReadFamilyMovesAsync(req.FamilyMovesBlob, context.CancellationToken);
            LogProcessMemory("chunks loaded", chunks.Count);

            var sw              = System.Diagnostics.Stopwatch.StartNew();
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(chunks, req.VectorDimensions, context.CancellationToken);
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
                        embeddingResult.EmptyVectors,
                        embeddingResult.CacheHits,
                        embeddingResult.CacheHitTokens,
                        embeddingResult.ApiPhaseMs,
                        embeddingResult.CachePhaseMs,
                    },
                },
                context.CancellationToken);

            // D200 R2. embed_upload was 75.4 s on run 9/260917/2, of which the embed step
            // accounted for 24.9 s (cache 23.9, API 0.7) and nothing measured the other ~50 s.
            // Two days of cache tuning went into the smaller half because the smaller half was
            // the only one instrumented. These four clocks close that.
            var uploadClock = System.Diagnostics.Stopwatch.StartNew();
            var uploadResult = await _uploadService.UploadDocumentsAsync(
                embeddedDocs, staleDocumentIds, familyMoves, req.VectorDimensions, ct: context.CancellationToken);
            uploadClock.Stop();
            LogProcessMemory("upload complete", chunks.Count);

            // Rolling full-corpus snapshot (source-scoped) + the two evictions that ride along
            // with it. Best-effort against uploadResult.DocsFailed - a chunk that failed to
            // upsert is still folded into the snapshot as if it succeeded (UploadService
            // doesn't report which specific chunks failed, only the count) - rare,
            // self-corrects whenever that document is next reprocessed.
            var snapshotClock = System.Diagnostics.Stopwatch.StartNew();
            var live = await _snapshotService.UpdateAsync(
                Source, embeddedDocs, staleDocumentIds, processedDocumentIds, req.InstanceId, req.StartedAt, context.CancellationToken);
            snapshotClock.Stop();

            // EvictionDurationMs keeps wrapping both stores, as it has since D200 R2; the vector
            // cache's list/delete split and the identity store's own clock ride beside it
            // (2026-09-18, D203 M5a) so the two stop hiding behind one number.
            var evictClock = System.Diagnostics.Stopwatch.StartNew();
            var eviction   = await _vectorCache.EvictOrphanedAsync(live.ContentHashes, context.CancellationToken);
            var evictedCount = eviction.Deleted;
            if (evictedCount > 0)
                _logger.LogInformation("Vector cache eviction — {Count} orphaned entr{Suffix} deleted in {DeleteMs} ms after listing {Listed} in {ListMs} ms",
                    evictedCount, evictedCount == 1 ? "y" : "ies", eviction.DeleteMs, eviction.Listed, eviction.ListMs);

            // Same treatment for the identity store, which until now was the one corpus-scoped
            // store that never forgot a deleted document. A ghost identity record keeps
            // clustering: single-linkage means one sitting between two live documents merges
            // their families, and it can even be the family's id.
            var identityEvictClock = System.Diagnostics.Stopwatch.StartNew();
            var evictedIdentities  = await _identityStore.EvictOrphanedAsync(live.DocumentIds, context.CancellationToken);
            identityEvictClock.Stop();
            evictClock.Stop();
            if (evictedIdentities > 0)
                _logger.LogInformation("Identity store eviction — {Count} orphaned record(s) deleted",
                    evictedIdentities);

            await DeleteBlobAsync(req.ChunksBlob, context.CancellationToken);
            await DeleteBlobAsync(req.StaleIdsBlob, context.CancellationToken);
            await DeleteBlobAsync(req.ProcessedIdsBlob, context.CancellationToken);
            await DeleteBlobAsync(req.FamilyMovesBlob, context.CancellationToken);

            return new EmbedUploadStageMetrics(
                DocsUploaded:                  uploadResult.DocsUploaded,
                // Refused by Search PLUS withheld by us (2026-09-17, D199 A1). Folded rather than
                // reported separately because the report's identity is
                // DocsUploaded + DocsFailed == ChunksProduced, and a chunk we withheld is as
                // absent from the index as one Search rejected. Which of the two it was is on the
                // same report already: VectorDimErrors and EmptyVectors sit beside this.
                DocsFailed:                    uploadResult.DocsFailed + uploadResult.DocsWithheld,
                ChunksRemoved:                 uploadResult.ChunksRemoved,
                ChunkFamiliesPatched:          uploadResult.ChunkFamiliesPatched,
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
                PreviousIndexStorageSizeBytes: uploadResult.PreviousIndexStorageSizeBytes)
            {
                TotalEmbeddingTokens   = embeddingResult.TotalInputTokens,
                EmptyVectors           = embeddingResult.EmptyVectors,
                // The split of the folded DocsFailed above, so the report states it rather than
                // leaving a reader to infer it from VectorDimErrors + EmptyVectors.
                DocsWithheld           = uploadResult.DocsWithheld,
                DocumentsWithheld      = uploadResult.DocumentsWithheld,
                // D200 R2 - the previously unmeasured half of this stage.
                SearchUploadDurationMs = uploadClock.ElapsedMilliseconds,
                SnapshotDurationMs     = snapshotClock.ElapsedMilliseconds,
                SnapshotRows           = live.ContentHashes.Count,
                EvictionDurationMs     = evictClock.ElapsedMilliseconds,
                EmbeddingApiDurationMs = embeddingResult.ApiPhaseMs,
                VectorCacheDurationMs  = embeddingResult.CachePhaseMs,
                VectorCacheHitTokens   = embeddingResult.CacheHitTokens,
                // The 429 subset of EmbeddingRetries: on a full re-embed this and the durations
                // are what the rebuild actually costs, not the dollars.
                RateLimitedRetries     = embeddingResult.ThrottledRetries,
                IndexVectorIndexSizeBytesSnapshot = uploadResult.IndexVectorIndexSizeBytesSnapshot,
                // The divisors for VectorCacheDurationMs, plus the binary that produced them
                // (D197 action 4). BuildId is taken from the assembly the embed stage lives in,
                // not this one: it is the code whose changes these timings are used to judge.
                MaxCacheParallelism   = embeddingResult.CacheParallelism,
                VectorCacheOperations = embeddingResult.CacheOperations,
                BuildId               = BuildIdentity.For(typeof(IEmbeddingService).Assembly),
                // The cache phase in pieces, the eviction split, and the upload batches (D203 §3).
                HashMs                    = embeddingResult.HashMs,
                VectorCacheReadMs         = embeddingResult.CacheReadMs,
                VectorCacheBytesRead      = embeddingResult.CacheBytesRead,
                VectorDeserializeMs       = embeddingResult.VectorDeserializeMs,
                VectorClassifyMs          = embeddingResult.VectorClassifyMs,
                VectorCacheWriteMs        = embeddingResult.CacheWriteMs,
                VectorCacheBytesWritten   = embeddingResult.CacheBytesWritten,
                VectorCacheWritesSkipped  = embeddingResult.CacheWritesSkipped,
                VectorCacheListMs         = eviction.ListMs,
                VectorCacheListedBlobs    = eviction.Listed,
                VectorCacheDeleteMs       = eviction.DeleteMs,
                VectorCacheBlobBytesTotal = eviction.BlobBytesTotal,
                VectorCacheBlobBytesP50   = eviction.BlobBytesP50,
                IdentityEvictionMs        = identityEvictClock.ElapsedMilliseconds,
                SearchUploadBatches       = uploadResult.SearchUploadBatches,
                SearchUploadBatchMaxMs    = uploadResult.SearchUploadBatchMaxMs,
                SearchUploadBytes         = uploadResult.SearchUploadBytes,
                // The histogram's content, on the report, because the histogram is unreadable
                // to this team (D203 §6c).
                VectorCacheOpLatency      = new VectorCacheOpLatency(
                    GetHit:  embeddingResult.GetHitLatency,
                    GetMiss: embeddingResult.GetMissLatency,
                    Put:     embeddingResult.PutLatency,
                    Delete:  eviction.DeleteLatency),
            };
        }
        // Returned as data, not thrown (2026-09-17, D199 §8b item 3). The total-withhold guard is
        // the one failure this stage can fully DESCRIBE - it knows the verdict breakdown, the
        // configured width, and how many chunks and documents were involved - and throwing would
        // destroy all of it: in the isolated worker the orchestrator receives TaskFailedException
        // with FailureDetails, where the type survives as a string and custom properties do not
        // survive at all. So the detail is converted here, where it still exists, and the
        // orchestrator marks the run failed from the returned Failure instead.
        //
        // The payoff is that a configuration drift reports like an ordinary run that withheld
        // everything: DocsWithheld == the chunk count, DocumentsWithheld == the document count,
        // DocsUploaded 0. The drift is visible in the report's own columns rather than only in a
        // stringified exception.
        catch (TotalWithholdException ex)
        {
            _logger.LogError(ex, "EmbedAndUploadActivity withheld every chunk for '{ChunksBlob}'", req.ChunksBlob);
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "embed_upload"));

            return new EmbedUploadStageMetrics(
                DocsUploaded:                  0,
                DocsFailed:                    ex.TotalChunks,
                ChunksRemoved:                 0,
                ChunkFamiliesPatched:          0,
                ChunksTruncated:               0,
                EmbeddingRetries:              0,
                VectorDimErrors:               0,
                VectorCacheHits:               0,
                TotalEmbeddingDurationMs:      0,
                IndexDocumentCountSnapshot:    null,
                IndexStorageSizeBytesSnapshot: null,
                RedFlags:                      [],
                ChunksEvicted:                 0,
                PreviousIndexDocumentCount:    null,
                PreviousIndexStorageSizeBytes: null)
            {
                DocsWithheld      = ex.TotalChunks,
                DocumentsWithheld = ex.DistinctDocuments,
                // FULLY QUALIFIED, to match the other path. The catch below writes
                // FailureDetails.ErrorType, which Durable gives as the full type name
                // ("System.InvalidOperationException"), so nameof() here would put two spellings
                // in one column and any filter on it would miss half the failures.
                Failure = new StageFailure(typeof(TotalWithholdException).FullName!, ex.Message)
                {
                    IsDimensionDrift   = ex.IsDimensionDrift,
                    ExpectedDimensions = ex.ExpectedDimensions,
                    VerdictCounts      = ex.VerdictCounts,
                },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "EmbedAndUploadActivity failed for '{ChunksBlob}'", req.ChunksBlob);
            throw new InvalidOperationException($"EmbedAndUploadActivity failed: {ex}");
        }
    }

    // Step 0 — provision the index if missing, then read back what it ACTUALLY is. One read per
    // run, threaded down from here (D201).
    //
    // Why the index and not OPENAI_EMBEDDING_DIMENSIONS: the configured value is a provisioning
    // input, not a fact. Nothing sends it to the embedding model - the deployment returns its
    // native width regardless - so validating a vector against config asks "does the model agree
    // with a setting", when the only question that decides an upload is "does the model agree with
    // the index". FlagEvaluator has said so since 2026-09-15: "VectorDimErrors cannot see this
    // one: it compares each vector against the same configuration value, so a config change
    // without a re-index passes it and fails at upload."
    //
    // Why here and not inside the embed stage: this fails the run, and it has to fail it before
    // the only paid step. Extraction bills Content Understanding per page; a Search outage found
    // at preflight costs nothing, the same outage found at upload has already spent it.
    //
    // Why failing rather than falling back to config: a fallback silently reinstates the
    // config-as-proxy path this exists to remove, and a run validated against the wrong claim
    // would be indistinguishable from a correct one afterwards. A run that cannot read the index
    // cannot publish to it either.
    [Function("PreflightActivity")]
    public async Task<IndexVectorConfig> PreflightActivity([ActivityTrigger] string instanceId, FunctionContext context)
    {
        using var _    = _logger.BeginScope(new Dictionary<string, object?> { ["InstanceId"] = instanceId, ["Source"] = Source });
        using var span = Instrumentation.ActivitySource.StartActivity("indexing.preflight");
        span?.SetTag("indexing.instance_id", instanceId);
        try
        {
            // Get-or-create, and it must come first: ReadVectorConfigAsync cannot describe an
            // index that does not exist yet, which is the first run in a fresh environment.
            await _indexService.EnsureIndexAsync();

            var vectorConfig = await _indexService.ReadVectorConfigAsync(context.CancellationToken);

            if (!vectorConfig.FieldPresent || vectorConfig.Dimensions is not > 0)
                throw new InvalidOperationException(
                    $"Preflight could not read the width of the index's '{vectorConfig.FieldName}' field. " +
                    "The embed and upload stages validate every vector against it, so the run stops here rather " +
                    "than falling back to OPENAI_EMBEDDING_DIMENSIONS, which is what the index was asked for and " +
                    "not necessarily what it is.");

            _logger.LogInformation(
                "Preflight — index '{Index}' vector field is {Dims} wide (configured {Configured}); vectors are judged against the live width",
                _indexerConfig.SearchIndexName, vectorConfig.Dimensions, vectorConfig.ConfiguredDimensions);

            return vectorConfig;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "preflight"));
            _logger.LogError(ex, "PreflightActivity failed");
            throw new InvalidOperationException($"PreflightActivity failed: {ex}");
        }
    }

    // Adds two optional token counts without letting a null blank the other out: null + 5 is 5,
    // not null. Both null stays null - "neither stage reported usage", which is not zero tokens.
    private static long? Sum(long? a, long? b) => (a, b) switch
    {
        (null, null) => null,
        var (x, y)   => (x ?? 0) + (y ?? 0),
    };

    [Function("SaveIndexReportActivity")]
    public async Task SaveIndexReportActivity([ActivityTrigger] PdfIndexRunReport report, FunctionContext context)
    {
        using var _ = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["InstanceId"] = report.Run.InstanceId,
            ["Source"]     = Source,
        });

        LogRunSummary(report);

        // Recorded here rather than in the orchestrator body: this activity runs exactly once
        // per run, orchestrator code replays. BlobsProcessed is the run heartbeat (plan 3.2 -
        // defined from the start, never recorded); the stage histogram is plan 2.3's meter
        // half, the report's StageDurationsMs is the per-run half.
        if (report.Success)
            Instrumentation.BlobsProcessed.Add(1);
        foreach (var (stage, ms) in report.StageDurationsMs ?? new Dictionary<string, long>())
            Instrumentation.StageDuration.Record(ms / 1000.0, new KeyValuePair<string, object?>("stage", stage));

        // Second stats sample (plan 4.1): the post-upload snapshot regularly reads 0 because
        // Azure Search stats lag live writes; this one lands seconds later and rides the report
        // as verification. Get-and-verify only - the drift baseline stays owned by
        // IndexStatsMonitor, which this deliberately does not call. Best-effort: a failed read
        // must not lose the report.
        try
        {
            var (docCount, storageBytes, vectorBytes) = await _indexDocumentService.GetStatisticsAsync(context.CancellationToken);
            report = report with { StatsReadback = new IndexStatsReadback(docCount, storageBytes, DateTimeOffset.UtcNow) { VectorIndexSizeBytes = vectorBytes } };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Index stats readback failed — report saved without StatsReadback.");
        }

        // The live vector configuration, read back the same way (2026-09-15): which metric,
        // width, HNSW parameters and vectorizer the index ACTUALLY has, as the service reports
        // them - including the defaults the code never set (BuildVectorSearch passes no HNSW
        // parameters, so "cosine" there is the service's default, not a decision anyone made).
        // Get-and-verify: the run analysis compares it against configuration; nothing here
        // changes the index. Best-effort for the same reason as the block above.
        try
        {
            report = report with { VectorConfig = await _indexService.ReadVectorConfigAsync(context.CancellationToken) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Index vector-config readback failed — report saved without VectorConfig.");
        }

        // Cost and storage rollups (2026-09-15). Derived here, after both readbacks, because this
        // is the first point where the stage records, the live vector width and the vector index
        // size all exist together - no stage owns that join. Nothing is re-measured: every input
        // is a number an earlier stage already reported.
        report = report with
        {
            EmbeddingCost = EmbeddingCostMetrics.From(
                // What the API was actually sent, kept as its two parts: chunk embeddings bill
                // on the embed stage, identity embeddings on the chunking stage, and
                // report-schema.md deliberately does not fold one into the other.
                chunkTokens:         report.Embedding?.TotalEmbeddingTokens,
                identityTokens:      report.Chunking?.IdentityTokens?.TotalEmbedded,
                // A cold-cache rebuild: every chunk and every identity text this run handled,
                // cached or not. The number that matters for a chunker or model change, because
                // such a change invalidates the vector cache by definition.
                fullReEmbedTokens:   Sum(report.Chunking?.Tokens?.Total, report.Chunking?.IdentityTokens?.TotalThisRun),
                prefixTokens:        report.Chunking?.Tokens?.PrefixTokensTotal,
                prefixShareOfTokens: report.Chunking?.Tokens?.PrefixShareOfTotal,
                rateUsdPer1M:        _indexerConfig.EmbeddingInputPriceUsdPer1MTokens),

            VectorStorage = VectorStorageMetrics.From(
                // The LIVE width, not the configured one: EnsureIndexAsync is get-or-create, so
                // the index can be narrower than configuration says indefinitely, and the bytes
                // on disk follow the index.
                dimensions:            report.VectorConfig?.Dimensions,
                compressionConfigured: report.VectorConfig?.Compression is not null,
                chunksProduced:        report.ChunksProduced,
                // Prefer the readback over the post-upload snapshot: the snapshot is taken
                // seconds after writing, which is exactly when Azure Search's stats lag most.
                vectorIndexSizeBytes:  report.StatsReadback?.VectorIndexSizeBytes
                                       ?? report.Embedding?.IndexVectorIndexSizeBytesSnapshot,
                // Dead weight from counts that already exist and mean something: below the
                // chunking budget's own body floor, and content-hash duplicates. No invented
                // "empty" threshold.
                thinChunks:            report.Chunking?.Tokens?.UnderMinBodyBudget,
                duplicateChunks:       report.Chunking?.DuplicateChunks ?? 0),
        };

        // Runs since the index's vector definition last changed. Best-effort and last: it is the
        // only part of this activity that WRITES state, so a failure here must not cost the
        // report everything derived above.
        if (report.VectorConfig is { } vectorConfig)
        {
            try
            {
                var hash     = IndexDefinitionCounter.ComputeHash(vectorConfig);
                var previous = await _reportWriter.GetIndexDefinitionAsync(Source, context.CancellationToken);
                var counter  = IndexDefinitionCounter.Advance(previous, hash, DateTimeOffset.UtcNow);

                await _reportWriter.SaveIndexDefinitionAsync(Source, counter, context.CancellationToken);
                report = report with { IndexDefinition = counter };

                if (counter.DefinitionChangedThisRun)
                    _logger.LogWarning(
                        "Index vector definition changed ({Previous} -> {Current}) — runs on the previous " +
                        "definition are not comparable to this one.",
                        counter.PreviousDefinitionHash, counter.DefinitionHash);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Index-definition counter failed — report saved without IndexDefinition.");
            }
        }

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

    // ChunkActivity always writes family-moves.json, empty list included, so on a current
    // deployment a missing blob means the write itself failed - hence the warning rather
    // than silence. But an orchestration whose ChunkActivity ran under a deployment that
    // predates the blob replays EmbedAndUpload with no blob to read, and failing the whole
    // run over that transition is worse than proceeding with no moves.
    private async Task<List<FamilyMove>> ReadFamilyMovesAsync(string blobPath, CancellationToken ct)
    {
        try
        {
            return await ReadBlobAsync<List<FamilyMove>>(blobPath, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Family-moves blob {Blob} not found — treating as no moves", blobPath);
            return [];
        }
    }

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
