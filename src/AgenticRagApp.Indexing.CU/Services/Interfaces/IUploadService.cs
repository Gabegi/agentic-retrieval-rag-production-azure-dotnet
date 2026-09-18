using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IUploadService
{
    // staleDocumentIds: documents whose old chunks may now be orphaned (updated or removed
    // upstream). Cleanup runs AFTER upload succeeds and only removes chunk ids that aren't
    // part of what was just uploaded - see UploadService.
    //
    // familyMoves: documents this run re-homed into a different family. These are typically NOT
    // in documents - a family move is caused by other documents changing the clustering, so the
    // moved document's own bytes are unchanged and extraction skipped it - which is exactly why
    // they need naming separately. Patched, not re-uploaded; see UploadService.
    //
    // allowVectorless: publish chunks whose ContentVector is null instead of withholding them.
    // False everywhere except RestoreService, which rebuilds rows from a snapshot and uploads the
    // ones whose vector it could not resolve from the cache on purpose. On the indexing path a
    // null vector would mean an embed failure, so it is refused. It exempts a NULL vector only -
    // a vector that is present is judged by VectorHealth.Classify either way.
    //
    // indexVectorDimensions: the LIVE index field width, read once at preflight (D201). Every
    // vector is judged against it rather than against OPENAI_EMBEDDING_DIMENSIONS, because the
    // index is the only thing that can actually reject one - configuration is what the index was
    // asked for when it was created, which is not necessarily what it is.
    Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkObject>      documents,
        IReadOnlyList<string>         staleDocumentIds,
        IReadOnlyList<FamilyMove>     familyMoves,
        int                           indexVectorDimensions,
        bool                          allowVectorless = false,
        CancellationToken             ct = default);
}

public record UploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksRemoved,
    // Index rows whose family_id was patched without their content being touched. Zero on a run
    // where no document changed family, which is most runs.
    int   ChunkFamiliesPatched,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    IReadOnlyList<string> RedFlags,
    // The drift baseline this run was compared against - i.e. the previous run's index stats,
    // captured before IndexStatsMonitor overwrote them. Null when no baseline existed, or when
    // the stats snapshot itself failed. See IndexDriftCheck.
    long? PreviousIndexDocumentCount    = null,
    long? PreviousIndexStorageSizeBytes = null
)
{
    // Chunks this run refused to send to Search because their vector failed VectorHealth.Classify
    // (2026-09-17, D199 A1). Distinct from DocsFailed, which is "Search rejected it": the two have
    // the same consequence but different causes, and on the restore path the report's
    // VectorDimErrors/EmptyVectors are structurally 0, so nothing else would say why the chunk is
    // missing. Consumers differ deliberately - IndexingFunction folds this into the report's
    // DocsFailed to keep DocsUploaded + DocsFailed == ChunksProduced, RestoreService reports it
    // separately. Init property so every existing construction site and test stays untouched.
    public int DocsWithheld { get; init; }

    // Distinct source documents the withheld chunks belong to. Chunk counts alone cannot show
    // persistence: per-id logging is capped, so once a run withholds more than the cap a
    // permanently stuck document is invisible behind whatever else withheld that run. A document
    // count that stays non-zero run after run is what says "stuck", and the standing cost of
    // stuck is one paid Content Understanding extraction per run per document (D199 §3/A2).
    public int DocumentsWithheld { get; init; }

    // The vector field plus its HNSW graph at snapshot time (2026-09-15) - what counts against
    // the tier's vector quota, and the basis the run report's storage breakdown subtracts the raw
    // vector bytes from to infer graph overhead. Init property so every existing construction
    // site and test stays untouched. Null = the service did not report it, or stats failed.
    public long? IndexVectorIndexSizeBytesSnapshot { get; init; }

    // The Search push-API batches this run sent and the slowest of them (2026-09-18, D203 M7).
    // SearchUploadDurationMs on the report wraps the whole UploadService call - upsert, stale
    // cleanup, family patching, stats - and was 28 s on every force run that measured it, with
    // nothing inside it. Batches × ~1,000 is the send shape; MaxMs against SearchUploadDurationMs
    // says whether one batch or all of them carried the time. Per-batch values are on the
    // indexer.search_upload_batch_ms histogram. Null MaxMs = nothing was sent.
    public int   SearchUploadBatches    { get; init; }
    public long? SearchUploadBatchMaxMs { get; init; }

    // Serialized bytes the upsert batches put on the wire, counted by the SearchClient's
    // request-size policy (2026-09-18, D203 §8). The payload half of the upload question:
    // SearchUploadDurationMs was 28-31 s on every force run, uniform across four batches, and
    // this is the first reading of what those batches weighed. Null = not attributable, see
    // UpsertResult.BytesSent; never an estimate.
    public long? SearchUploadBytes { get; init; }
};
