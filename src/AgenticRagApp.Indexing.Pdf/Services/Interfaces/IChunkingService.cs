using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IChunkingService
{
    string Name { get; }

    // Low-level: splits raw text into TextChunks using the configured strategy.
    IReadOnlyList<TextChunk> Chunk(string content);

    // High-level: converts ExtractionDocuments into indexed DocumentChunks,
    // computes ChunkingStageMetrics, and emits all chunk telemetry. Async - resolves
    // family/domain identity (FamilyIdEmbedder) via an embedding call before splitting.
    Task<(IReadOnlyList<DocumentChunk> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default);
}
