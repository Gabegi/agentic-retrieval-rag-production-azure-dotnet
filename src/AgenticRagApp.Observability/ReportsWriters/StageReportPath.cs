namespace AgenticRagApp.Observability.Reports;

// Builds the blob path for a per-stage diagnostic report (validation, file-facts, failure,
// extraction diff). Delegates to ReportPath, the one naming scheme shared by every report
// producer in the app - see ReportPath.cs.
//
// instanceId is nullable because not every caller runs inside an orchestration (tests, ad-hoc
// invocations); those get the id-less form rather than inventing one.
public static class StageReportPath
{
    public static string Build(string reportName, DateTimeOffset runAt, string? instanceId) =>
        ReportPath.Build(runAt, reportName, instanceId);
}
