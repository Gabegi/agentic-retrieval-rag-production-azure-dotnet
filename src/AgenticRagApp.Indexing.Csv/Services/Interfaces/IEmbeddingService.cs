using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ChunkStatsSource> documents, CancellationToken ct = default);
}

public record EmbeddingRunResult(
    IEnumerable<ChunkStatsSource> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors
);
