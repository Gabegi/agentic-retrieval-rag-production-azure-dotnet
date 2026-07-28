using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Models;

public class ValidationReport
{
    public DateTime RunAtUtc              { get; init; }
    public int      PagesExtracted        { get; init; }
    public int      IndexRecordsExtracted { get; init; }
    public int      JoinedRecords         { get; init; }
    public int      CleanedRecords        { get; init; }
    public bool     Passed                { get; init; }
    // True when every check except the magnitude-shift check passed. Lets a caller
    // decide whether an operator override should be allowed to proceed - error-rate
    // and reconciliation failures indicate genuinely malformed data and must never be
    // bypassable, but a magnitude shift can be a legitimate large import.
    public bool     PassedExcludingMagnitude { get; init; }

    public IReadOnlyList<ValidationIssue>   Issues                        { get; init; } = [];
    public IReadOnlyList<string>            ReconciliationProblems        { get; init; } = [];
    public IReadOnlyList<string>            MagnitudeWarnings             { get; init; } = [];
    public IReadOnlyList<string>            RedFlags                      { get; init; } = [];
    public IReadOnlyList<CleanedPageRecord> SpotCheckSample               { get; init; } = [];
    public IReadOnlyList<string>            DocumentsNeedingFallbackChunking { get; init; } = [];
    public IReadOnlyList<string>            SkippedIndexDocuments             { get; init; } = [];  // "DocumentTypeName (DocumentId)" of index docs with no pages
    public int                              StaleDocCount                     { get; init; }
    public int                              MojibakeRepairedPages             { get; init; }
    public int                              DetectedTableCount                { get; init; }
}
