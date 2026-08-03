using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<PdfExtractionDocument> Docs, ExtractionStageMetrics Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default);
}
