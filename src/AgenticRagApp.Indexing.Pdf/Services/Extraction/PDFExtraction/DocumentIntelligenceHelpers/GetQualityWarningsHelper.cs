using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Average OCR word-confidence scoring (PageQuality/LowPageConfidence) was removed
// here: it measures DI's uncertainty about pixel-derived text, which doesn't apply
// to this born-digital, no-scan corpus - DI decodes an embedded text stream with no
// pixel ambiguity to be uncertain about, so the score never moved (sampled at
// 0.94-0.995 across a real batch, nowhere near the 0.85 threshold it was compared
// against). It also can't catch this corpus's actual failure mode - a broken
// ToUnicode CMap decodes with full (wrong) confidence, since DI isn't uncertain
// about what it read, just wrong about what it means. See
// docs/260728/extraction-optimisation.md for the sampling this was based on, and
// the deferred replacement signal (F) once one is needed.
// ZeroWordsOnPage survives: unlike confidence, "DI found no words at all" is still
// meaningful on a born-digital page - either genuinely blank or entirely vector
// figure content.
internal static class GetQualityWarningsHelper
{
    // internal (not private): testable without a live DI call, as with GetPages.
    public static IReadOnlyList<AnalysisWarning> GetZeroWordWarnings(AnalyzeResult result, string blobName) =>
        result.Pages
            // Null Words means DI returned no words collection at all, which for this
            // warning's purpose is the same signal as an empty one: nothing OCR-able
            // was reported for the page.
            .Where(p => (p.Words?.Count ?? 0) == 0)
            .Select(p => new AnalysisWarning(
                "ZeroWordsOnPage",
                $"Page {p.PageNumber} has zero detected words - either genuinely blank or entirely vector figure content (no OCR-able text).",
                blobName))
            .ToList();

    // File-level structural defects: things DI extracted but returned incomplete or
    // malformed. Computed from what GetTables/GetFigures already produced, so they're
    // flagged at the source rather than recomputed by PdfPipelineValidator later.
    // Cost is not here: it's data (DocumentAnalyzedResults.EstimatedCostUsd) and an
    // info entry, not a defect.
    // internal (not private): testable without a live DI call, as with GetPages.
    public static IReadOnlyList<AnalysisWarning> StructureWarnings(
        IReadOnlyList<TableInfo> tables, IReadOnlyList<FigureInfo> figures, string blobName)
    {
        var warnings = new List<AnalysisWarning>(2);

        var uncaptioned = figures.Count(f => f.Caption is null);
        if (uncaptioned > 0)
            warnings.Add(new AnalysisWarning(
                "FiguresWithoutCaption",
                $"{uncaptioned} of {figures.Count} figure(s) have no caption.",
                blobName));

        var malformed = tables.Count(t => t.Cells.Count == 0 || t.RowCount == 0 || t.ColumnCount == 0);
        if (malformed > 0)
            warnings.Add(new AnalysisWarning(
                "MalformedTable",
                $"{malformed} of {tables.Count} table(s) have no cells or a zero row/column count.",
                blobName));

        return warnings;
    }

    // Cost echo: informational, not a defect, so it goes to Infos rather than Warnings.
    // Takes the already-computed estimatedCost rather than recomputing pageCount *
    // CostPerPage itself - DocumentAnalyzedResults.EstimatedCostUsd and this message
    // must agree, so there's exactly one place that does the multiplication.
    // internal (not private): keeps the message format unit-testable without a live
    // DI call, as with StructureWarnings.
    public static AnalysisWarning CostInfo(decimal estimatedCost, int pageCount, string blobName) =>
        new("EstimatedCost",
            $"Estimated cost: ${estimatedCost:F2} ({pageCount} page(s) at ${PdfDocumentIntelligenceAnalyzer.CostPerPage}/page).",
            blobName);

    // DI's own non-fatal warnings (e.g. a page that partially failed OCR), distinct from
    // the zero-pages case which is an outright failure. Wraps the SDK type so callers
    // don't need it.
    public static IReadOnlyList<AnalysisWarning> GetDiWarnings(AnalyzeResult result) =>
        (result.Warnings ?? [])
            .Select(w => new AnalysisWarning(w.Code, w.Message, w.Target))
            .ToList();
}
