using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Functions;

// Index recovery - wipe the index and repopulate it from the rolling full-corpus snapshot,
// as opposed to PdfIndexingFunction's re-extract/re-chunk/re-embed pipeline.
public class IndexRestoreFunction
{
    private readonly IIndexRebuildService _indexRebuildService;
    private readonly IRestoreService      _restoreService;
    private readonly IRunReportWriter     _reportWriter;
    private readonly ILogger<IndexRestoreFunction> _logger;

    public IndexRestoreFunction(
        IIndexRebuildService indexRebuildService,
        IRestoreService      restoreService,
        IRunReportWriter     reportWriter,
        ILogger<IndexRestoreFunction> logger)
    {
        _indexRebuildService = indexRebuildService;
        _restoreService      = restoreService;
        _reportWriter        = reportWriter;
        _logger              = logger;
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

        context.SetCustomStatus(new IndexingProgress(IndexingProgress.RecreatingIndex, startedAt));

        try
        {
            await context.CallActivityAsync("RecreateIndexActivity");
            context.SetCustomStatus(new IndexingProgress(IndexingProgress.Restoring, startedAt));

            result  = await context.CallActivityAsync<RestoreResult>("RestoreFromSnapshotActivity");

            // Gate on the upsert's own per-document result, not IndexDocumentCountSnapshot -
            // that stats call lags live writes by minutes (see UploadService) and reporting
            // Success:true next to a stale 0 there masked a real empty-index incident.
            if (result.ChunksFailed > 0)
                error = $"{result.ChunksFailed} of {result.ChunksFailed + result.ChunksRestored} chunk(s) failed to upload during restore.";
            else
                success = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        context.SetCustomStatus(new IndexingProgress(
            success ? IndexingProgress.Completed : IndexingProgress.Failed, startedAt,
            DocsUploaded: result?.ChunksRestored));

        await context.CallActivityAsync("SaveRestoreReportActivity",
            BuildRestoreReport(context, startedAt, result, success, error));

        if (!success)
            throw new InvalidOperationException(error ?? "Index restore failed");
    }

    // Wipes the index and the knowledge stack on top of it - IIndexRebuildService owns the
    // order that has to happen in. Leaves the index empty; RestoreFromSnapshotActivity is
    // what puts documents back.
    [Function("RecreateIndexActivity")]
    public async Task RecreateIndexActivity([ActivityTrigger] object? _, FunctionContext context)
    {
        try
        {
            await _indexRebuildService.RecreateEmptyAsync(context.CancellationToken);
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

        // Under runs/ for the same reason as the index run report (see
        // PdfIndexingFunction.SaveIndexReportActivity), so one Event Grid subject filter
        // covers both. The restore/ segment keeps the two distinguishable - the email handler
        // branches on it to pick the renderer, since a restore has no extraction/chunking/
        // embedding stages to report.
        await _reportWriter.WriteReportAsync(
            RunReportPath.Build(RunReportKind.Restore, report.StartedAt, report.InstanceId),
            report, context.CancellationToken);
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
            ChunksFailed:                  result?.ChunksFailed         ?? 0,
            ChunksMissingVector:           result?.ChunksMissingVector  ?? 0,
            IndexDocumentCountSnapshot:    result?.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot: result?.IndexStorageSizeBytesSnapshot,
            SearchIndexName:               result?.SearchIndexName      ?? "",
            EmbeddingModel:                result?.EmbeddingModel       ?? "",
            EmbeddingDeployment:           result?.EmbeddingDeployment  ?? "");
}
