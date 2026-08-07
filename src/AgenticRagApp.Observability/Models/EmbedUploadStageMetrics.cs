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
    IReadOnlyList<string> RedFlags,

    // Cached vectors deleted because their content hash no longer appears anywhere in the live
    // corpus (VectorCache.EvictOrphanedAsync, run after each snapshot update). Previously
    // computed, logged, and dropped - it never reached any report, so "the cache is drifting
    // away from the live corpus" was invisible to anything reading a run report.
    int ChunksEvicted,

    // The drift baseline IndexStatsMonitor compared against - i.e. the previous run's values,
    // read from indexing/_last-stats-{source}.json immediately before that blob is overwritten
    // with this run's. Carried here because it is otherwise unrecoverable: by the time anything
    // reads the run report, the blob holds this run's numbers, not the previous run's.
    //
    // Null when no baseline existed (first run for this source). Note IndexStatsMonitor only
    // *reports* the comparison as a RedFlag when it breaches DriftThresholdPct - these fields
    // make the delta available on every run, at any magnitude.
    long? PreviousIndexDocumentCount,
    long? PreviousIndexStorageSizeBytes
);
