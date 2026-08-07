using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Owns the upload half of the indexing pipeline: upserts embedded DocumentChunks
// into Azure AI Search and takes a post-upload index stats/drift snapshot.
// Kept separate from EmbeddingService so the two concerns can evolve independently.
public class UploadService : IUploadService
{
    // Scopes the drift-baseline (IIndexStatsMonitor.RecordAndCheckDriftAsync) to this
    // doc-type - PDF and CSV must never compare against each other's baseline.
    private const string Source = "pdf";

    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly IIndexStatsMonitor         _indexStatsMonitor;
    private readonly ILogger<UploadService>     _logger;

    public UploadService(
        IIndexDocumentService  indexDocumentService,
        IIndexStatsMonitor     indexStatsMonitor,
        ILogger<UploadService> logger)
    {
        _indexDocumentService = indexDocumentService;
        _indexStatsMonitor    = indexStatsMonitor;
        _logger               = logger;
    }

    public async Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<DocumentChunk> documents, IReadOnlyList<string> staleDocumentIds, CancellationToken ct = default)
    {
        var docList = documents.ToList();

        // Maps down to the exact field set the Search schema knows about, right here, at
        // the last possible moment before handing off to the generic (doc-type-agnostic)
        // upload path - see SearchUploadChunk's own comment.
        var uploadBatch = docList.Select(SearchUploadChunk.From).ToList();
        var (succeeded, failed) = await _indexDocumentService.UpsertDocumentsAsync(uploadBatch, ct);

        _logger.LogInformation("Upload complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);

        // Only now, with replacement content already live, clean up what's actually orphaned:
        // chunk ids that existed for a stale (updated/removed) document but aren't among the
        // ids just uploaded. Anything we just touched - even a failed upsert - is kept, since a
        // failed upsert means the old content at that id is still the authoritative one.
        var chunksRemoved = 0;
        if (staleDocumentIds.Count > 0)
        {
            var uploadedChunkIds = docList.Select(d => d.Id).ToHashSet();
            var existingChunkIds = await _indexDocumentService.GetChunkIdsForDocumentsAsync(staleDocumentIds, ct);
            var orphanedChunkIds = existingChunkIds.Where(id => !uploadedChunkIds.Contains(id)).ToList();

            if (orphanedChunkIds.Count > 0)
                chunksRemoved = await _indexDocumentService.DeleteChunksByIdAsync(orphanedChunkIds, ct);

            Instrumentation.ChunksRemoved.Add(chunksRemoved);
            _logger.LogInformation(
                "Stale-chunk cleanup for {DocCount} document(s) — {Removed} orphaned chunk(s) deleted",
                staleDocumentIds.Count, chunksRemoved);
        }

        // Stats snapshot taken after upload. Azure Search stats lag live writes by minutes —
        // use for corpus drift checks only, not for "did this run add N chunks" (use succeeded/failed).
        long? indexDocCount = null, indexStorageBytes = null;
        var drift = IndexDriftCheck.None;
        try
        {
            var (docCount, storageBytes) = await _indexDocumentService.GetStatisticsAsync(ct);
            (indexDocCount, indexStorageBytes) = (docCount, storageBytes);
            drift = await _indexStatsMonitor.RecordAndCheckDriftAsync(Source, docCount, storageBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Index stats snapshot failed — upload results are unaffected");
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "stats_snapshot"));
        }

        return new UploadResult(
            DocsUploaded:                  succeeded,
            DocsFailed:                    failed,
            ChunksRemoved:                 chunksRemoved,
            IndexDocumentCountSnapshot:    indexDocCount,
            IndexStorageSizeBytesSnapshot: indexStorageBytes,
            RedFlags:                      drift.RedFlags,
            PreviousIndexDocumentCount:    drift.PreviousDocumentCount,
            PreviousIndexStorageSizeBytes: drift.PreviousStorageSizeBytes);
    }
}
