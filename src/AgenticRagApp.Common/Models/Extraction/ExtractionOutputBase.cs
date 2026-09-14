
namespace AgenticRagApp.Common.Models;

// The source-agnostic half of an extraction stage's output. Docs is left out here because its
// element type differs per source - each derived record (PdfExtractionOutput today; the archived
// CSV pipeline's before it) adds its own Docs property with the right list type.
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
    // Null = source has no equivalent mechanism, not "verified zero". PDF reports null since the
    // Zenya metadata removal (2026-08-26).
    public int? TraceabilityGapCount { get; init; }
    public required IReadOnlyList<PipelineIssue> Issues { get; init; }
    public required IReadOnlyList<string> RedFlags { get; init; }
    public required IReadOnlyList<SpotCheckEntry> SpotCheckSample { get; init; }
}
