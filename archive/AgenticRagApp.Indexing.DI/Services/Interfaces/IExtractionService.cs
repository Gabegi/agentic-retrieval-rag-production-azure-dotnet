using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.DI.Services;

public interface IExtractionService
{
    // instanceId names this run's diagnostic report blobs (the extraction diff, and the
    // pipeline's validation/file-facts/failure reports further down). Those used to be named
    // by wall-clock timestamp, which made them impossible to attribute to a run: two
    // overlapping runs cross-attribute each other's reports, and a run starting at 23:58 writes
    // its extraction reports into the next day's folder. Null falls back to the old timestamp
    // naming for callers outside an orchestration (tests, ad-hoc use).
    Task<(IReadOnlyList<PdfExtractionDocument> Docs, ExtractionStageMetrics Stats)> ExtractAsync(
        bool forceReindex, string? instanceId = null, CancellationToken ct = default);
}
