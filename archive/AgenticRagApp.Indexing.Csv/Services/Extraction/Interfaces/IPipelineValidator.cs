using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IPipelineValidator
{
    ValidationReport Validate(
        ExtractionBatch<PageRecord>  pagesExtraction,
        ExtractionBatch<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        int?                          previousRunCleanedCount = null);
}
