namespace AgenticRagApp.Observability.Reports;

public interface IRunReportWriter
{
    // Always true today (see RunReportWriter) - kept on the interface so callers have one
    // place to check rather than each hardcoding "always write", in case a future report
    // type genuinely needs gating.
    bool IsEnabled { get; }

    // Callers build the blob path themselves (e.g. "indexing/{yyyy}/{MM}/{dd}/{id}.json") and
    // supply whatever serializable report/data they need written - one method instead of a
    // dedicated wrapper per report type. Gated by IsEnabled: callers should check it first.
    Task WriteReportAsync<T>(string path, T report, CancellationToken ct = default);

    // Baseline for drift checks. Unlike the methods above, these are NOT gated by IsEnabled —
    // they must work in every environment, since drift detection is pointless if it only runs in dev.
    // Returns null if no baseline exists yet (first run) or the read fails.
    //
    // source scopes the baseline per doc-type pipeline (e.g. "pdf", "csv") — document types are
    // never mixed in reporting, so each source gets its own baseline file, never a shared one.
    Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(string source, CancellationToken ct = default);
    Task SaveLastIndexStatsAsync(string source, long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
