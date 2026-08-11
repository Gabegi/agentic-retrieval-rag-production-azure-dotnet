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

// Where a run report is written. Delegates to ReportPath, the one naming scheme shared by every
// report producer in the app - see ReportPath.cs.
//
// Historical reports stay at their old (pre-consolidation) paths. Nothing migrates them, and
// nothing reads them.
public static class RunReportPath
{
    public static string Build(RunReportKind kind, DateTimeOffset startedAt, string instanceId) =>
        ReportPath.Build(startedAt, kind == RunReportKind.Restore ? "restore-run" : "index-run", instanceId);
}
