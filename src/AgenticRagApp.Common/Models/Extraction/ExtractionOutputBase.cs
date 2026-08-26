
namespace AgenticRagApp.Common.Models;

// Every field except Docs is identical between Csv and Pdf. Docs is left out
// here because its element type differs per source - each derived record adds
// its own Docs property with the right list type.
public abstract record ExtractionOutputBase
{
    public int ValidationErrors { get; init; }
    public int ValidationWarnings { get; init; }
    public int ReconciliationProblems { get; init; }
    public int? StaleDocCount { get; init; }          // null = source has no equivalent concept, not "verified zero"
    public int MojibakeRepairedPages { get; init; }
    public int DetectedTableCount { get; init; }
    public int DocsWithoutHeadings { get; init; }
    public int MissingTitleCount { get; init; }
    public int? MissingVersionCount { get; init; }    // null = source has no equivalent concept, not "verified zero"
    public int? MissingDepartmentCount { get; init; } // null = source has no equivalent concept, not "verified zero"
    // Null = source has no equivalent mechanism, not "verified zero". Both PDF (since the
    // Zenya metadata removal, 2026-08-26) and CSV report null.
    public int? TraceabilityGapCount { get; init; }
    public required IReadOnlyList<PipelineIssue> Issues { get; init; }
    public required IReadOnlyList<string> RedFlags { get; init; }
    public required IReadOnlyList<SpotCheckEntry> SpotCheckSample { get; init; }
}
