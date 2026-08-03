namespace AgenticRagApp.Observability.Reports;

public record EmbedUploadStageMetrics(
    int   DocsUploaded,
    int   DocsFailed,
    // Orphaned chunks cleaned up after upload succeeded - see UploadService.UploadDocumentsAsync.
    int   ChunksRemoved,
    int   ChunksTruncated,
    int   EmbeddingRetries,
    int   VectorDimErrors,
    // Chunks whose vector was reused from the vector cache instead of a paid embedding call.
    int   VectorCacheHits,
    long  TotalEmbeddingDurationMs,
    // Snapshot taken after upload. Azure Search stats lag live writes by minutes —
    // use this for corpus drift checks, not for "did this run add N chunks" (use DocsUploaded for that).
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    // Populated when doc-count drift exceeds the threshold in UploadService. Merged into
    // IndexRunReport.RedFlags alongside extraction-stage flags.
    IReadOnlyList<string> RedFlags
);
