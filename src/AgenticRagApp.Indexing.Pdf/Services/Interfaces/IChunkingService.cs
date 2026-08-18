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

    // Converts ExtractionDocuments into indexed ChunkObjects,
    // computes ChunkingStageMetrics, and emits all chunk telemetry. Async - resolves
    // family/domain identity (DocumentIdentityResolver) via an embedding call before splitting.
    //
    // Also writes the stage's own run report (one blob covering identity resolution, strategy
    // routing, heading location and the chunks), including when the stage throws - which is
    // why it needs instanceId/startedAt to name the blob. Both are optional: a caller outside
    // an orchestration gets the id-less path, same as StageReportPath's other callers.
    Task<(IReadOnlyList<ChunkObject> Docs, ChunkingStageMetrics Stats)> ChunkDocumentsAsync(
        IReadOnlyList<PdfExtractionDocument> docs,
        string?                              instanceId = null,
        DateTimeOffset?                      startedAt  = null,
        CancellationToken                    ct         = default);
}
