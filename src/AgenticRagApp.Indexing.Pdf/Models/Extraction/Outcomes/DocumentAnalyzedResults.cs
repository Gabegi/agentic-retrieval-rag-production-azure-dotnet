using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Result of PdfDocumentIntelligenceAnalyzer.AnalyzeDocumentAsync (the DI-scoped step only -
// preflight/native-metadata are separate steps, combined by DocumentIntelligenceExtractor
// into the final PdfExtractionResult):
// - Ok = true  -> RawContent/Pages/Structure/EstimatedCostUsd are populated.
// - Ok = false -> Error explains what went wrong, whether the failure happened during
//   preflight checks or during the paid Document Intelligence call itself.
public sealed record DocumentAnalyzedResults(
    bool Ok,
    string? RawContent,                            // analysis.Content, unsplit, before per-page assembly
    IReadOnlyList<PdfPageRecord>? Pages,
    PdfDocumentStructure? Structure,
    decimal? EstimatedCostUsd,
    PipelineIssue? Error)
{
    // Empty (not null) when Ok is false - there's no analysis to have warned about.
    public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = [];

    // Informational, non-defect findings (e.g. cosmetic normalization counts, the
    // estimated-cost echo) - kept separate from Warnings so callers can tell "worth a
    // human look" apart from "worth knowing." Empty (not null) when Ok is false.
    public IReadOnlyList<AnalysisWarning> Infos { get; init; } = [];

    // "nl"/"en" (LanguageDetectionHelper), read off DI's own AnalyzeResult.Languages -
    // null when Ok is false, since there's no analysis to have detected a language from.
    public string? Language { get; init; }
}
