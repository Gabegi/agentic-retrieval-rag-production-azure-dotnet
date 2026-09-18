namespace AgenticRagApp.Infrastructure.Clients.Search;

// What one UpsertDocumentsAsync call did. The four-way Deconstruct keeps every
// `var (succeeded, failed, batches, durations) = ...` caller compiling; BytesSent was added
// beside them (2026-09-18, D203 §8) rather than as a fifth tuple member.
//
// BytesSent is the serialized request payload of this call's batches, read off the
// SearchRequestByteCounter before and after the batch loop. Null when it cannot be trusted:
// no counter wired (a client built without the policy), a batch whose length the SDK could not
// compute, or a request count that does not match the batch count (another caller pushing
// documents through the same client at the same time). Null, never an estimate.
public sealed record UpsertResult(int Succeeded, int Failed, int Batches, IReadOnlyList<long> BatchDurationsMs, long? BytesSent = null)
{
    public void Deconstruct(out int succeeded, out int failed, out int batches, out IReadOnlyList<long> batchDurationsMs)
    {
        succeeded        = Succeeded;
        failed           = Failed;
        batches          = Batches;
        batchDurationsMs = BatchDurationsMs;
    }
}

// Doc-type-agnostic document-level CRUD against the Search index, plus logging.
// No Instrumentation/drift-check (that's an Observability concern; see
// AgenticRagApp.Observability.Reports.IIndexStatsMonitor). One shared instance — the pipeline
// services that need it (UploadService, IndexDiffService) inject it rather than owning a copy
// of this CRUD logic.
public interface IIndexDocumentService
{
    // Doc-type-specific mapping (which fields a chunk maps to) happens before documents
    // reach this call. Batches = the 1000-doc push-API batches actually sent, counted where
    // the batching happens rather than re-derived by callers (observability plan 3.1).
    //
    // BatchDurationsMs: wall-clock of each UploadDocuments call, in send order, one per batch
    // (2026-09-18, D203 M7). Returned rather than metered here because this project does not
    // reference Observability; the caller (UploadService) records the histogram. The list is
    // what lets a reader tell four slow batches from one - a total could not.
    Task<UpsertResult> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default);

    // Partial update: overwrites ONLY the fields present in each payload, leaving the rest of the
    // row alone. For patching a field on rows whose content is unchanged and whose chunks the
    // caller does not hold - a document re-homed into a different family is the case this exists
    // for, since it is skipped at extraction and never re-chunked.
    //
    // Merge, not MergeOrUpload: a key that is not in the index means the index and whatever
    // produced the patch have diverged, and inventing a row from a two-field payload would write
    // a chunk with no content. Failing is the signal.
    //
    // Every field on the payload type is written, so pass a type carrying ONLY the key and the
    // fields being patched - never a partially-populated full projection, whose nulls would blank
    // the columns they land on.
    Task<(int Succeeded, int Failed)> MergeDocumentFieldsAsync<T>(IEnumerable<T> patches, CancellationToken ct = default);

    // Pages through the entire index selecting only document_id + last_modified_date.
    Task<Dictionary<string, DateTimeOffset>> GetCurrentlyIndexedDocsIdsNDatesAsync(CancellationToken ct = default);

    // The two halves of what used to be one "delete everything for these documents" call.
    // Split so a caller can diff the result against a "keep" set (e.g. chunks just
    // re-uploaded) before deciding what's actually stale - see each doc-type's UploadService.
    Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);
    Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default);

    // Whole-index aggregates (document count, storage size, vector index size). Callers that
    // also need Instrumentation recording + drift-check should follow this with
    // IIndexStatsMonitor.RecordAndCheckDriftAsync.
    //
    // VectorIndexSizeBytes is the vector field plus its HNSW graph - the figure that counts
    // against the tier's vector quota, and the only one the service reports for the vector side.
    // Null = not reported by the service, never 0.
    Task<(long DocumentCount, long StorageSizeBytes, long? VectorIndexSizeBytes)> GetStatisticsAsync(CancellationToken ct = default);
}
