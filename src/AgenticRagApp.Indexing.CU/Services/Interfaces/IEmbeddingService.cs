using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ChunkObject> documents, CancellationToken ct = default);
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

    // Fresh vectors of the right length whose values are all zero or non-finite - they pass the
    // dimension check and upload cleanly, then never match a query (EmbeddingService
    // .IsEmptyVector). Init property for the same reason as TotalInputTokens.
    public int EmptyVectors { get; init; }

    // The split of the embed step's wall-clock (2026-09-15): the batched API phase (batches run
    // MaxParallelism-wide, so this is wall time, not summed call time) and the cache read + write
    // phases around it. Together they are most of the caller's TotalEmbeddingDurationMs.
    public long ApiPhaseMs   { get; init; }
    public long CachePhaseMs { get; init; }

    // Stored token counts of the chunks whose vector came from the cache - what they would have
    // billed. CacheHits says how many; this says how much.
    public long CacheHitTokens { get; init; }

    // The 429 subset of EmbeddingRetries (2026-09-15). The total also counts 5xx, dropped
    // connections and request timeouts, and those point at different remedies: throttling means
    // the deployment's TPM is the binding constraint, a 5xx spike means the service was unwell.
    // What a re-embed actually costs is wall-clock and throttling, so this is the number to watch
    // on a full rebuild - not the dollars, which are noise at this corpus size.
    public int ThrottledRetries { get; init; }
}
