namespace AgenticRagApp.Observability.Reports;

public enum RunReportKind { Index, Restore }

// What identifies a run report, without the blob path itself. The activity that emails a run's
// report already knows all three fields directly from the orchestrator - kind, instance ID, and
// StartedAt - so this replaces parsing a path back out of a blob name (needed only when a blob
// trigger is the entry point, which this feature no longer uses).
public sealed record RunReportRef(RunReportKind Kind, DateOnly Date, string InstanceId)
{
    public static RunReportRef Index(string instanceId, DateTimeOffset startedAt) =>
        new(RunReportKind.Index, DateOnly.FromDateTime(startedAt.UtcDateTime), instanceId);

    public static RunReportRef Restore(string instanceId, DateTimeOffset startedAt) =>
        new(RunReportKind.Restore, DateOnly.FromDateTime(startedAt.UtcDateTime), instanceId);
}

// Where a run report is written.
//
// ── Why runs/ and not indexing/ ──────────────────────────────────────────────
// The run report used to sit at indexing/{date}/{instanceId}.json, alongside the per-stage
// diagnostic reports under indexing/pdf-extraction/ and indexing/extraction-diff/. Moving it to
// its own prefix means "the report for run X" is one unambiguous path with nothing else
// underneath it, which is what lets anything downstream (the run email, a reconciliation query,
// a human browsing) find run reports without pattern-matching around the stage reports.
//
// Historical reports stay at the old prefix. Nothing migrates them, and nothing reads them.
public static class RunReportPath
{
    public const string RunsPrefix    = "runs/";
    public const string RestorePrefix = "runs/restore/";

    public static string Build(RunReportKind kind, DateTimeOffset startedAt, string instanceId) =>
        kind == RunReportKind.Restore
            ? $"{RestorePrefix}{startedAt:yyyy/MM/dd}/{instanceId}.json"
            : $"{RunsPrefix}{startedAt:yyyy/MM/dd}/{instanceId}.json";
}
