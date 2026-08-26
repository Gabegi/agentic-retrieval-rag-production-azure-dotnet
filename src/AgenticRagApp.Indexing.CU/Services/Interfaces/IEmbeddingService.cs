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
}
