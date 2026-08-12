using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IChunkingService
{
    string Name { get; }

    // The low-level Chunk(string) passthrough is gone with the flat strategy interface it
    // wrapped: a strategy now needs the whole document (headings, section tree, page map,
    // routing measurements), so there is nothing meaningful a bare string can be chunked
    // against. It had no callers outside its own tests.

    // Converts ExtractionDocuments into indexed DocumentChunks,
    // computes ChunkingStageMetrics, and emits all chunk telemetry. Async - resolves
    // family/domain identity (FamilyIdEmbedder) via an embedding call before splitting.
    Task<(IReadOnlyList<DocumentChunk> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default);
}
