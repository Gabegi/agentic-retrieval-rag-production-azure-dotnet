using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionStageMetrics Stats)> ExtractAsync(
        bool forceReindex, bool overrideMagnitudeCheck = false, CancellationToken ct = default);
}
