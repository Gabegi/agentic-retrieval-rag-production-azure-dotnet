namespace AgenticRagApp.Common.Models;

// Generic row/page batch accumulator, identical in both pipelines. Init-only
// because this type is now consumed cross-assembly by both pipelines - the
// previous internal AddRecord/AddError/AddWarning mutators can't compile once
// callers live outside this project. Callers accumulate into local lists while
// extracting, then construct one of these once at the end.
public sealed class ExtractionBatch<T>
{
    public required IReadOnlyList<T> Records { get; init; }
    public required IReadOnlyList<PipelineIssue> Errors { get; init; }
    public required IReadOnlyList<PipelineIssue> Warnings { get; init; }
    public int RowsAttempted => Records.Count + Errors.Count;
}
