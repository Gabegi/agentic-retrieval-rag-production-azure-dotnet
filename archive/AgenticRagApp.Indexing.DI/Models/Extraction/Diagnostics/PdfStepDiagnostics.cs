using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Models;

// One pipeline step's non-fatal findings for one PDF file (PdfDocumentValidator,
// PdfNativeMetadataExtractor, PdfDocumentIntelligenceAnalyzer, PdfSectionBreadCrumbBuilder each
// produce one of these) - lets a report answer "which step found this" instead of
// everything landing in one undifferentiated pile.
//
// Report/diagnostic material only - NOT a second source of truth for validation
// gating. PdfPipelineValidator gates on PdfExtractionResult.Ok/Error (the file-level
// outcome) and PageErrors (per-page failures within an otherwise-successful file);
// it must never also fold these per-step Errors into CollectIssues/the error-rate
// gate, or a mirrored hard failure (see ValidationDiagnostics) would be counted
// twice. These are written by WriteReportsAsync for humans to read, nothing else.
public sealed record PdfStepDiagnostics(
    IReadOnlyList<PipelineIssue> Warnings,
    IReadOnlyList<PipelineIssue>   Errors,
    IReadOnlyList<PipelineIssue>? Info = null)
{
    // Successes worth reporting ("N bookmarks found", "XMP packet parsed") land here,
    // not in Warnings - a step finding something present and fine is not the same
    // signal as a step finding something missing/broken, and folding both into one
    // list makes Warnings useless as a quality signal (see GetIssuesFromMetadataDiagnostics
    // in PdfPipelineValidator, which reads only Warnings for exactly that reason).
    public IReadOnlyList<PipelineIssue> Info { get; init; } = Info ?? [];

    public static readonly PdfStepDiagnostics Empty = new([], []);
}
