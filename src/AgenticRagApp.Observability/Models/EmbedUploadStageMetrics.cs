namespace AgenticRagApp.Observability.Reports;

public record EmbedUploadStageMetrics(
    int   DocsUploaded,
    int   DocsFailed,
    // Orphaned chunks cleaned up after upload succeeded - see UploadService.UploadDocumentsAsync.
    int   ChunksRemoved,
    // Existing indexed chunks whose family_id was rewritten in place because OTHER documents'
    // clustering re-homed their document (UploadService.PatchMovedFamiliesAsync). Absent from
    // reports before 2026-08-19; older runs read back as 0.
    int   ChunkFamiliesPatched,
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
)
{
    // Billed embedding input tokens this run, service-reported (see EmbeddingRunResult
    // .TotalInputTokens - this is that value riding the report). Null = blank, not zero.
    // Identity-resolution embeddings (chunking stage) are metered but not in this field.
    public long? TotalEmbeddingTokens { get; init; }

    // Fresh vectors of the right length whose values are all zero or non-finite. They pass the
    // dimension check and upload cleanly, then never match a query - the one defect the two
    // counts above cannot see (EmbeddingService.IsEmptyVector, 2026-09-15). Should always be 0.
    // Null = the report predates the counter; not measured, not zero.
    public int? EmptyVectors { get; init; }

    // Cost and throughput split (2026-09-15). TotalEmbeddingDurationMs above is the whole embed
    // step - cache reads, the batched API calls, cache writes (not upload). These two are its
    // parts: the API phase is wall time of the 4-wide batches (what a full re-embed's duration
    // scales from), the cache phase is the blob GET/PUT time around it. Null = predates the fields.
    public long? EmbeddingApiDurationMs { get; init; }
    public long? VectorCacheDurationMs  { get; init; }

    // Stored token counts of the chunks served from the vector cache - what they would have
    // billed. VectorCacheHits says how many chunks the cache saved; this says how many tokens.
    // TotalEmbeddingTokens + this ≈ what the run would have billed with a cold cache.
    public long? VectorCacheHitTokens { get; init; }

    // The 429 subset of EmbeddingRetries (2026-09-15). EmbeddingRetries alone cannot answer "were
    // we rate-limited" - it also counts 5xx, dropped connections and request timeouts, which call
    // for different action (raise TPM / lower parallelism, versus wait for the service). On a full
    // re-embed this and the duration fields are what the rebuild actually costs; the dollars are
    // noise at this corpus size. Null = the report predates the counter, not zero throttling.
    public int? RateLimitedRetries { get; init; }

    // The vector field plus its HNSW graph, as the service reports it, sampled with the other two
    // snapshots after upload. This is the figure that counts against the tier's VECTOR quota,
    // which is the one that runs out before StorageSize does. Null = not reported.
    public long? IndexVectorIndexSizeBytesSnapshot { get; init; }
}
