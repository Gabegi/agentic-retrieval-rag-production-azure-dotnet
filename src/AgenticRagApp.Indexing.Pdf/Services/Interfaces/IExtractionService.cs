using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<PdfExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default);
}
