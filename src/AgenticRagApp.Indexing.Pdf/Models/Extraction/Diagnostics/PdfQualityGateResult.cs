using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Mirrors CSV's ValidationReport. Drops StaleDocCount — Zenya's check_date_exceeded
// attention flag has no PDF equivalent (no attention-flags data source for PDFs).
// Reuses the source-agnostic PipelineIssue type.
public class PdfQualityGateResult
{
    public DateTime RunAtUtc              { get; init; }
    public int      PagesExtracted        { get; init; }
    public int      CleanedRecords        { get; init; }
    public bool     Passed                { get; init; }

    public IReadOnlyList<PipelineIssue>      Issues                           { get; init; } = [];
    public IReadOnlyList<string>               ReconciliationProblems           { get; init; } = [];
    // Advisory only — unlike CSV's magnitude-shift check, this never gates Passed (see
    // PdfPipelineValidator's tiering comment): with extraction-skip in place, most runs
    // only touch a handful of changed documents, so a legitimate small-changeset run can
    // still look like a huge swing against the whole-corpus baseline.
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

    // How many <table> blocks fell back to plain text because ConvertTable couldn't parse
    // their shape (finding #16) - watch alongside DetectedTableCount: a non-zero value here
    // against a small DetectedTableCount is the discrepancy worth investigating.
    public int TableConversionFallbacks { get; init; }
}
