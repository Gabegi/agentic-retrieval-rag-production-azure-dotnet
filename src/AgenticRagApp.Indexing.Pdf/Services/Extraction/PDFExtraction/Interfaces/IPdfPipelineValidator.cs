using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IPdfPipelineValidator
{
    // Takes raw per-file results directly - the validator owns flattening them into a
    // page-level batch (and into a per-blob Structure lookup) itself, rather than
    // receiving a pre-aggregated shape a separate class built. See PdfPipelineValidator.
    PdfValidationReport Validate(
        IReadOnlyList<PDFExtractionResult> fileResults,
        PdfCleanResult                     cleanResult,
        int?                               previousRunCleanedCount = null,
        int?                               spotCheckSeed           = null);
}
