namespace AgenticRagApp.Observability.Reports;

// Single naming scheme for every report this app writes: run reports, per-stage diagnostics,
// per-run content archives, corpus snapshots, and eval results all go through this, so "where do
// I find report X" has one answer - a date folder plus a self-describing filename - instead of a
// different folder-and-suffix convention per producer.
public static class ReportPath
{
    public static string Build(DateTimeOffset at, string reportName, string? id, string extension = "json") =>
        string.IsNullOrWhiteSpace(id)
            ? $"{at:yyyy/MM/dd}/{at:yyyyMMddTHHmmssfff}Z-{reportName}.{extension}"
            : $"{at:yyyy/MM/dd}/{at:yyyyMMddTHHmmssfff}Z-{reportName}-{id}.{extension}";
}
