namespace AgenticRagApp.Observability.Reports;

// Three tiers, adapted from P0-P3. A batch report has no pager, so the top tier is "content is
// wrong in the index", not "production is down".
//
// The governing rule is the SRE actionability test: if the reader cannot take a specific action,
// the flag should not exist. Anything that fails that test belongs in the metrics tables (§4 of
// pipeline-email-report-structure.md), not here.
public enum FlagSeverity
{
    // Trend worth knowing; no action today. Only rendered when there's a delta to show.
    Watch = 0,
    // Quality signal outside expected range. Not yet user-visible.
    Warning = 1,
    // Content is missing from or wrong in the index - retrieval is degraded right now.
    // Also emits an alert metric: this must not depend on someone reading mail.
    Critical = 2,
}

public sealed record ReportFlag(
    FlagSeverity Severity,
    // The field this came from, e.g. "Chunking.DocsWithZeroChunks". Every flag names its source
    // so a reader can check it against the metrics tables.
    string Metric,
    string Observed,
    string Expected,
    // What it means and what to do - the half that makes the flag actionable.
    string Meaning,
    string Action)
{
    // True when the threshold behind this flag has no defensible source yet and is awaiting
    // calibration against real runs. In calibration mode these are suppressed entirely rather
    // than rendered as low-confidence flags - a flag nobody trusts is worse than no flag.
    public bool AwaitingCalibration { get; init; }
}
