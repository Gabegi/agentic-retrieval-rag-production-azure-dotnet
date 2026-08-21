using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.DI.Services;

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
        IEnumerable<ChunkObject> documents,
        IReadOnlyList<string>    staleDocumentIds,
        IReadOnlyList<FamilyMove> familyMoves,
        CancellationToken ct = default)
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

        var familiesPatched = await PatchMovedFamiliesAsync(familyMoves, docList, ct);

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
            ChunkFamiliesPatched:          familiesPatched,
            IndexDocumentCountSnapshot:    indexDocCount,
            IndexStorageSizeBytesSnapshot: indexStorageBytes,
            RedFlags:                      drift.RedFlags,
            PreviousIndexDocumentCount:    drift.PreviousDocumentCount,
            PreviousIndexStorageSizeBytes: drift.PreviousStorageSizeBytes);
    }

    // Patches family_id onto the indexed rows of documents this run re-homed, without touching
    // their content.
    //
    // Why a patch and not a re-index: a family move is caused by OTHER documents changing the
    // clustering, so the moved document's own bytes are unchanged. ExtractionService's diff
    // therefore skips it, it never reaches chunking, and this run holds no ChunkObject for it -
    // while its indexed rows keep a family_id that is now wrong, in the field the knowledge agent
    // filters on. Nothing else in the pipeline can see this: the chunk text is identical, so a
    // content hash matches, and the document-level gate skipped it before chunking ran.
    //
    // The chunk ids come from the INDEX, for the same reason: we have no chunk list of our own
    // for these documents. Same call the stale-chunk cleanup above uses.
    //
    // Runs after the upsert, and after the cleanup, so it patches settled rows. Documents that
    // were uploaded this run are excluded - their rows already carry the new family_id from the
    // projection, and patching them again would be a second write saying the same thing.
    private async Task<int> PatchMovedFamiliesAsync(
        IReadOnlyList<FamilyMove> familyMoves, IReadOnlyList<ChunkObject> uploaded, CancellationToken ct)
    {
        if (familyMoves.Count == 0) return 0;

        var uploadedDocIds = uploaded.Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        var toPatch        = familyMoves.Where(m => !uploadedDocIds.Contains(m.SourceId)).ToList();

        if (toPatch.Count == 0)
        {
            _logger.LogInformation(
                "Family moves: all {Count} re-homed document(s) were uploaded this run, so their rows already carry the new family_id",
                familyMoves.Count);
            return 0;
        }

        // Queried one document at a time, deliberately. The batched form returns a flat id list
        // with no document_id attached, so pairing an id back to the family it should get would
        // mean parsing it - and ChunkIdBuilder runs sourceId through SafeKey, so the id is not
        // reliably a prefix match on the source id. One query per moved document is exact, and
        // moved documents are a handful per run at most (a whole corpus re-homing is a clustering
        // failure, not a workload).
        var patches = new List<ChunkFamilyPatch>();
        foreach (var move in toPatch)
        {
            var chunkIds = await _indexDocumentService.GetChunkIdsForDocumentsAsync([move.SourceId], ct);
            patches.AddRange(chunkIds.Select(id => new ChunkFamilyPatch(id, move.ToFamilyId)));
        }

        if (patches.Count == 0)
        {
            // The identity store says these documents moved; the index has no rows for them. Not
            // an error - a document resolved but never successfully indexed does this - but it
            // means the two are describing different corpora, which is worth saying out loud.
            _logger.LogWarning(
                "Family moves: {Count} document(s) were re-homed but have no rows in the index to patch",
                toPatch.Count);
            return 0;
        }

        var (patched, patchFailed) = await _indexDocumentService.MergeDocumentFieldsAsync(patches, ct);

        Instrumentation.ChunkFamiliesPatched.Add(patched);
        _logger.Log(patchFailed > 0 ? LogLevel.Warning : LogLevel.Information,
            "Family re-homing — {Patched} chunk row(s) across {Docs} document(s) moved to a new family_id, {Failed} failed",
            patched, toPatch.Count, patchFailed);

        return patched;
    }
}
