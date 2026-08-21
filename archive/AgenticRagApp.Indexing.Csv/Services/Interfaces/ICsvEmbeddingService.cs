using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvEmbeddingService
{
    Task<CsvEmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ChunkStatsAdapter> documents, CancellationToken ct = default);
}

public record CsvEmbeddingRunResult(
    IEnumerable<ChunkStatsAdapter> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors
);
