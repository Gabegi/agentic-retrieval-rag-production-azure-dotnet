namespace AgenticRagApp.Observability.Reports;

// Builds the blob path for a per-stage diagnostic report (validation, file-facts, failure,
// extraction diff).
//
// These used to be named purely by wall-clock timestamp - {HHmmssfff}-validation-report.json -
// which made them impossible to attribute to a run. Two things went wrong with that:
//
//   1. Two overlapping runs interleave their reports in the same folder with no way to tell
//      whose is whose, so anything correlating by time window cross-attributes them.
//   2. The reports are named at activity-execution time while the run report's folder comes
//      from the run's StartedAt - so a run starting at 23:58 writes its extraction reports
//      into the *next* day's folder, outside any single-folder search.
//
// Naming by instance ID removes both. The timestamp is kept as a prefix so the folder still
// sorts chronologically and a human browsing to a date still sees runs in order.
//
// instanceId is nullable because not every caller runs inside an orchestration (tests, ad-hoc
// invocations); those keep the old timestamp-only naming rather than inventing an ID.
public static class StageReportPath
{
    public static string Build(string reportFolder, DateTimeOffset runAt, string? instanceId, string suffix) =>
        string.IsNullOrWhiteSpace(instanceId)
            ? $"{reportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-{suffix}.json"
            : $"{reportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-{instanceId}-{suffix}.json";
}
