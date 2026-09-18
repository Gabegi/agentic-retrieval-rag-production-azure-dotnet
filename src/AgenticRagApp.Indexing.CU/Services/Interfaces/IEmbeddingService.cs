using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IEmbeddingService
{
    // indexVectorDimensions: the LIVE index field width, read once at preflight (D201). Not
    // OPENAI_EMBEDDING_DIMENSIONS - the model is never sent a width, so the only question that
    // decides an upload is whether what it returned matches the index.
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ChunkObject> documents, int indexVectorDimensions, CancellationToken ct = default);
}

public record EmbeddingRunResult(
    IEnumerable<ChunkObject> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors,
    // Chunks whose vector came from VectorCache instead of a paid embedding call.
    int CacheHits
)
{
    // Billed embedding input tokens, summed from service-reported response usage across this
    // run's fresh batches (cache hits bill nothing). Null = no response carried usage - blank,
    // never a local estimate (observability plan 1.6, decided 2026-08-26). Init property so
    // existing constructors stay untouched.
    public long? TotalInputTokens { get; init; }

    // Fresh vectors of the right length that are nonetheless unusable - all-zero, or carrying a
    // NaN/infinity (VectorHealth.Classify: Empty and NonFinite, counted together here). The two
    // fail very differently and the host log distinguishes them: all-zero uploads cleanly and
    // then never matches a query, while a non-finite component cannot be serialised at all.
    // Init property for the same reason as TotalInputTokens.
    public int EmptyVectors { get; init; }

    // The split of the embed step's wall-clock (2026-09-15): the batched API phase (batches run
    // MaxParallelism-wide, so this is wall time, not summed call time) and the cache read + write
    // phases around it. Together they are most of the caller's TotalEmbeddingDurationMs.
    public long ApiPhaseMs   { get; init; }
    public long CachePhaseMs { get; init; }

    // Stored token counts of the chunks whose vector came from the cache - what they would have
    // billed. CacheHits says how many; this says how much.
    public long CacheHitTokens { get; init; }

    // The two numbers CachePhaseMs has to be divided by to mean anything (2026-09-17, D197
    // action 4): the blob round trips the two cache passes actually made, counted at the call
    // site, and the parallelism they ran at. ms/op = CachePhaseMs × CacheParallelism /
    // CacheOperations, with nothing inferred - before these, a reader had to assume BOTH the
    // ops model (which action 1c changed: a PUT was two round trips, then one) and which build
    // had been deployed. Zero operations is a real reading: a run with no chunks.
    public int CacheOperations  { get; init; }
    public int CacheParallelism { get; init; }

    // The 429 subset of EmbeddingRetries (2026-09-15). The total also counts 5xx, dropped
    // connections and request timeouts, and those point at different remedies: throttling means
    // the deployment's TPM is the binding constraint, a 5xx spike means the service was unwell.
    // What a re-embed actually costs is wall-clock and throttling, so this is the number to watch
    // on a full rebuild - not the dollars, which are noise at this corpus size.
    public int ThrottledRetries { get; init; }

    // ── What CachePhaseMs is made of (2026-09-18, D203 §3) ──────────────────────────────────
    //
    // CachePhaseMs was one number and every reading of it a derivation. These are its measured
    // parts: the two pass clocks (their sum IS CachePhaseMs), and inside the read pass the hash
    // cost, the bytes the hits weighed, and the CPU spent parsing and health-checking them.
    // DeserializeMs and ClassifyMs are summed across MaxCacheParallelism parallel probes, so they
    // are CPU-seconds and may exceed CacheReadMs on a multi-core host - on EP1 (1 vCPU) they
    // cannot, and a large share of the read clock in them means the store is not the bottleneck.
    public long CacheReadMs         { get; init; }
    public long CacheWriteMs        { get; init; }
    public long HashMs              { get; init; }
    public long CacheBytesRead      { get; init; }
    public long CacheBytesWritten   { get; init; }
    public long VectorDeserializeMs { get; init; }
    public long VectorClassifyMs    { get; init; }

    // Fresh vectors the write pass refused to cache because the embedder judged them wrong-width
    // or unusable (D203 M4). Equals VectorDimErrors + EmptyVectors by construction today; carried
    // separately so the report states the gate fired rather than leaving it to be inferred.
    public int  CacheWritesSkipped  { get; init; }

    // Per-op latency by kind from the two passes (D203 §6c): the store's round trip for a hit
    // (body, parse subtracted), a miss (no body) and a PUT. Null = no operations of that kind.
    public AgenticRagApp.Observability.Reports.LatencySummary? GetHitLatency  { get; init; }
    public AgenticRagApp.Observability.Reports.LatencySummary? GetMissLatency { get; init; }
    public AgenticRagApp.Observability.Reports.LatencySummary? PutLatency     { get; init; }
}
