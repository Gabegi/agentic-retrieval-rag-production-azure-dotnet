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

    // Fresh vectors of the right length that are nonetheless unusable - the one defect the two
    // counts above cannot see (VectorHealth.Classify, 2026-09-15). Two conditions, counted
    // together and separated in the host log (2026-09-17): all-zero passes the dimension check
    // and uploads cleanly, then never matches a query; a NaN/infinity cannot be serialised at all
    // and would fail its whole upload batch. Should always be 0.
    // Null = the report predates the counter; not measured, not zero.
    public int? EmptyVectors { get; init; }

    // The part of DocsFailed that this pipeline withheld rather than Azure AI Search refusing it
    // (2026-09-17, D199 A1/A3).
    //
    // INCLUDED IN DocsFailed, not additional to it. DocsFailed folds the two causes so that
    // DocsUploaded + DocsFailed == ChunksProduced stays an identity; this field is the split of
    // that total, so "refused by Search" is DocsFailed - DocsWithheld, which is the subtraction
    // the flag does. Summing the two double-counts every withheld chunk.
    //
    // Inferring it was the alternative and it was rejected: VectorDimErrors + EmptyVectors does
    // equal this today, because both sides ask VectorHealth.Classify about the same chunks and
    // the absent-vector case is structurally zero on the indexing path - but nothing enforces
    // that, and a reader of the flag would be trusting an arithmetic coincidence between two
    // stages. The same "equal by convention" shape A0 removed from the predicate itself.
    //
    // Null = the report predates the field; not measured, not zero.
    public int? DocsWithheld { get; init; }

    // Distinct source documents those withheld chunks belong to. Chunk counts cannot show
    // persistence - a document stuck withholding forever costs one paid Content Understanding
    // extraction per run (D199 §3/A2), and this is the field that makes "still stuck" readable
    // across runs without going to the host log for ids.
    //
    // A cardinality, not a quantity: it is additive to nothing on this report and never sums with
    // DocsWithheld. Expect it <= DocsWithheld, equal only when no document withheld two chunks.
    // Null = the report predates the field; not measured, not zero.
    public int? DocumentsWithheld { get; init; }

    // Set when the stage failed in a way it could DESCRIBE rather than merely die from
    // (2026-09-17, D199 §8b item 3). Today that is the total-withhold guard, and the point is
    // that the rest of this record is still filled in from what the failure knew - DocsWithheld,
    // DocumentsWithheld, DocsFailed - so a configuration drift shows up in the report's own
    // columns instead of only inside a stringified exception. Null on a successful run and on any
    // failure the stage could not describe, which leaves the existing catch path unchanged.
    public StageFailure? Failure { get; init; }

    // ── Where the embed_upload stage's time actually goes (2026-09-17, D200 R2) ──────────────
    //
    // The stage was 75.4 s on run 9/260917/2. TotalEmbeddingDurationMs accounted for 24.9 s of it
    // (EmbeddingApiDurationMs 0.7 + VectorCacheDurationMs 23.9) and NOTHING measured the other
    // ~50 s, so two days of cache tuning went into the smaller half on the grounds that it was
    // the only half with a number. These are the missing pieces, and together with the embed
    // clocks they should now sum to roughly the stage duration - a gap between them is itself
    // the finding.
    //
    // Null = the report predates the fields; not measured, not zero.

    // The Azure AI Search upsert, plus the stale-chunk cleanup and family patching that run
    // inside UploadService.
    public long? SearchUploadDurationMs { get; init; }

    // The rolling full-corpus snapshot: read, merge, write, as one number because
    // ISnapshotService performs them as one operation. D200 R1 is about what this is spending it
    // on - 65,728 rows / 167 MB that never drops superseded entries.
    public long? SnapshotDurationMs { get; init; }

    // Rows in the snapshot AFTER the merge - the live set size. The row count is what makes
    // SnapshotDurationMs interpretable, and it is the number D200 R1's growth shows up in.
    public int? SnapshotRows { get; init; }

    // Both orphan evictions together: vector cache by content hash, identity store by document
    // id. One number because they are the same kind of work against two stores.
    public long? EvictionDurationMs { get; init; }

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

    // --- What VectorCacheDurationMs has to be divided by (2026-09-17, D197 action 4) ---
    //
    // The cache timings landed on 2026-09-15 with no divisor beside them, so every reading of
    // them so far has been ms/op = VectorCacheDurationMs × P / (ChunksProduced + misses) with
    // BOTH terms assumed. The 2026-09-17 review is what these three fix: it could show only that
    // 17,224 ms is 34.5 ms/op at P=8 and 138 at P=32, and had to pick between "the change was
    // never deployed" and "the blob store stopped scaling" on which answer looked ordinary.
    // With these it is arithmetic: ms/op = VectorCacheDurationMs × MaxCacheParallelism /
    // VectorCacheOperations, and BuildId says whether two runs are even the same experiment.
    //
    // Null on every report written before 2026-09-17 - not measured, not zero, same convention
    // as EmptyVectors and RateLimitedRetries above.

    // The concurrency the two cache passes ran at (VectorCacheGateway.MaxCacheParallelism as
    // that build compiled it), not the value in today's source.
    public int? MaxCacheParallelism { get; init; }

    // Blob round trips the cache passes actually made, counted at the call. NOT derivable from
    // chunk counts: a PUT was two round trips before 2026-09-16 and one after (D197 action 1c),
    // and action 2 would remove probes entirely, so the derived version changes basis under
    // exactly the changes it is used to judge.
    public int? VectorCacheOperations { get; init; }

    // Which binary produced this run - see BuildIdentity for why it carries an MVID and not just
    // a version string. Runs with the same BuildId are comparable; a difference means the code
    // moved, whatever the report's other numbers look like.
    public string? BuildId { get; init; }

    // ── The cache phase in pieces (2026-09-18, D203 §3) ─────────────────────────────────────
    //
    // VectorCacheDurationMs was one number, and the only latency anyone could read off it was
    // VectorCacheDurationMs × MaxCacheParallelism / VectorCacheOperations - a wall-clock average
    // that cannot say what an operation IS made of. These split the phase into the five things
    // the pass does: hash the text, probe the store, parse and judge what came back, write the
    // fresh vectors, evict the stale ones. Two sums are the acceptance test for the clocks
    // themselves: HashMs is inside VectorCacheReadMs, and VectorCacheReadMs + VectorCacheWriteMs
    // == VectorCacheDurationMs; VectorCacheListMs + VectorCacheDeleteMs + IdentityEvictionMs ≈
    // EvictionDurationMs. If they do not hold, the clocks bracket the wrong thing.
    //
    // Null on every report written before 2026-09-18 - not measured, not zero.

    // Read pass. HashMs is one forced SHA-256 of every chunk's embedded text, timed before the
    // probes start; ContentHash is a computed property, so the run pays this at least four
    // times (split, write, snapshot, artifact) and this is the price of one. VectorCacheBytesRead
    // is the content length of every GET that returned a body, i.e. what the hits weighed.
    // VectorDeserializeMs and VectorClassifyMs are CPU time summed across the parallel probes -
    // on the 1-vCPU EP1 host they cannot exceed the read clock, and a large share of it in them
    // means the store is not what the pass is waiting on.
    public long? HashMs              { get; init; }
    public long? VectorCacheReadMs   { get; init; }
    public long? VectorCacheBytesRead { get; init; }
    public long? VectorDeserializeMs { get; init; }
    public long? VectorClassifyMs    { get; init; }

    // Write pass. VectorCacheWritesSkipped is the fresh vectors the write gate refused because
    // the embedder judged them wrong-width or unusable - should read 0 whenever VectorDimErrors
    // and EmptyVectors do, and a non-zero here against zeros there is a bug in the gate.
    public long? VectorCacheWriteMs      { get; init; }
    public long? VectorCacheBytesWritten { get; init; }
    public int?  VectorCacheWritesSkipped { get; init; }

    // Eviction, split. One listing of the cache prefix (VectorCacheListedBlobs is the live cache
    // size), then one sequential delete per orphan - VectorCacheDeleteMs / ChunksEvicted is the
    // measured per-delete cost D203 O3 is judged against (13-16 ms on 260917/4-5, derived; this
    // is the direct reading). The identity-store eviction that shares EvictionDurationMs gets its
    // own clock so the two stores stop hiding behind one number. VectorCacheBlobBytesTotal and
    // BlobBytesP50 come from the listing's content lengths - the first measurement of what a
    // cache entry weighs on disk, against D203 §2's 35-40 KB estimate for JSON-encoded floats.
    public long? VectorCacheListMs         { get; init; }
    public int?  VectorCacheListedBlobs    { get; init; }
    public long? VectorCacheDeleteMs       { get; init; }
    public long? VectorCacheBlobBytesTotal { get; init; }
    public long? VectorCacheBlobBytesP50   { get; init; }
    public long? IdentityEvictionMs        { get; init; }

    // Upload (D203 M7). SearchUploadDurationMs was the largest number in this stage - 28 s on
    // both force runs that carried it - and the only one with nothing inside. Batches is how many
    // push-API calls it took; BatchMaxMs is the slowest of them, which against the total says
    // whether one batch or all of them carried the time. Per-batch values are on the
    // indexer.search_upload_batch_ms histogram. Null BatchMaxMs = nothing was sent.
    public int?  SearchUploadBatches    { get; init; }
    public long? SearchUploadBatchMaxMs { get; init; }

    // The upload payload: serialized request bytes of the upsert batches, counted where they left
    // by a pipeline policy on the SearchClient (2026-09-18, D203 §8). SearchUploadDurationMs was
    // 28-31 s on every force run with nothing inside it; against this field it becomes bytes per
    // second, and against DocsUploaded bytes per document - the vector alone is ~39 KB as JSON
    // floats on the wire, the same encoding the cache stopped using the same day. Null =
    // predates the field, or the counter could not attribute the bytes to this call's batches
    // (concurrent pusher, or a length the SDK could not compute) - never an estimate.
    public long? SearchUploadBytes { get; init; }

    // Per-operation latency by kind, p50 / p95 / max (2026-09-18, D203 §6c). The same samples
    // the indexer.vector_cache_op_ms histogram receives, summarised here because that histogram
    // exports to Azure Monitor only and nobody on this pipeline can read it there. GetHit.P50
    // against GetMiss.P50 is the payload's share of a round trip. Null = predates the field.
    public VectorCacheOpLatency? VectorCacheOpLatency { get; init; }
}
