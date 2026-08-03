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
