namespace AgenticRagApp.Infrastructure.Clients.Search;

// Doc-type-agnostic document-level CRUD against the shared Search index, plus logging.
// No Instrumentation/drift-check (that's an Observability concern; see
// AgenticRagApp.Observability.Reports.IIndexStatsMonitor). One shared instance, injected
// by both PDF's and CSV's own UploadService — neither owns a copy of this CRUD logic.
public interface IIndexDocumentService
{
    // Doc-type-specific mapping (which fields a chunk maps to) happens before documents
    // reach this call.
    Task<(int Succeeded, int Failed)> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default);

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

    // Whole-index aggregates (document count, storage size). Callers that also need
    // Instrumentation recording + drift-check should follow this with
    // IIndexStatsMonitor.RecordAndCheckDriftAsync.
    Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default);
}
