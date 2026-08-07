namespace AgenticRagApp.Observability.Reports;

// Records whole-index size telemetry (Instrumentation histograms, every environment —
// not gated by IRunReportWriter.IsEnabled, since drift dashboards need data everywhere)
// and flags a run-over-run doc-count swing beyond a threshold versus the last saved
// baseline for this source, then saves these stats as the new baseline. Source-scoped
// (IRunReportWriter.GetLastIndexStatsAsync/SaveLastIndexStatsAsync) so PDF and CSV never
// compare against each other's baseline. One shared instance — each doc-type's own
// UploadService calls this after Infrastructure's IIndexDocumentService.GetStatisticsAsync,
// instead of owning its own copy of this comparison logic.
public interface IIndexStatsMonitor
{
    Task<IndexDriftCheck> RecordAndCheckDriftAsync(
        string source, long documentCount, long storageSizeBytes, CancellationToken ct = default);
}

// The outcome of one drift check.
//
// PreviousDocumentCount/PreviousStorageSizeBytes are returned rather than kept internal
// because this call *overwrites* the baseline blob as its last act - so the value it
// compared against is unrecoverable afterwards. Anything wanting a run-over-run index delta
// (the run report, and the run email built on it) has to receive it here or not at all.
// Null when no baseline existed, i.e. the first run for this source.
//
// RedFlags stays threshold-gated (only breaches are flagged); the Previous* fields are
// unconditional, so a delta is available at any magnitude.
public sealed record IndexDriftCheck(
    IReadOnlyList<string> RedFlags,
    long?                 PreviousDocumentCount,
    long?                 PreviousStorageSizeBytes)
{
    public static readonly IndexDriftCheck None = new([], null, null);
}
