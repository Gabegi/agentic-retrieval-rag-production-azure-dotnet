using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IPdfPipelineValidator
{
    // Takes raw per-file results directly - the validator owns flattening them into a
    // page-level batch (and into a per-blob Structure lookup) itself, rather than
    // receiving a pre-aggregated shape a separate class built. See PdfPipelineValidator.
    PdfQualityGateResult Validate(
        IReadOnlyList<PdfExtractionResult> fileResults,
        PdfCleanResult                     cleanResult,
        int?                               spotCheckSeed           = null,
        int?                               previousRunCleanedCount = null);
}
