using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

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
);
