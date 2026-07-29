using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Mirrors CSV's ValidationReport. Drops StaleDocCount — Zenya's check_date_exceeded
// attention flag has no PDF equivalent (no attention-flags data source for PDFs).
// Reuses the existing (source-agnostic) ValidationIssue type.
public class PdfValidationReport
{
    public DateTime RunAtUtc              { get; init; }
    public int      PagesExtracted        { get; init; }
    public int      CleanedRecords        { get; init; }
    public bool     Passed                { get; init; }

    public IReadOnlyList<ValidationIssue>      Issues                           { get; init; } = [];
    public IReadOnlyList<string>               ReconciliationProblems           { get; init; } = [];
    public IReadOnlyList<string>               MagnitudeWarnings                { get; init; } = [];
    public IReadOnlyList<string>               RedFlags                         { get; init; } = [];
    public IReadOnlyList<CleanedPdfPageRecord> SpotCheckSample                  { get; init; } = [];
    public IReadOnlyList<string>               DocumentsNeedingFallbackChunking { get; init; } = [];
    public int                                 MojibakeRepairedPages            { get; init; }
    public int                                 DetectedTableCount               { get; init; }

    // Per-transform counts and raw/cleaned pairs — see PdfCleanResult and
    // CleaningSpotCheckEntry's own comments for why these exist alongside
    // MojibakeRepairedPages/SpotCheckSample above rather than replacing them.
    public int                                    ControlCharsStripped     { get; init; }
    public int                                    InvisibleCharsStripped   { get; init; }
    public int                                    LigaturesExpanded        { get; init; }
    public int                                    HyphenationJoinsRepaired { get; init; }
    public IReadOnlyList<CleaningSpotCheckEntry>  CleaningSpotCheckSample  { get; init; } = [];
}
