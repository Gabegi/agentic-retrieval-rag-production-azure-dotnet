namespace AgenticRagApp.Observability.Reports;

// Who/when/what-happened for one indexing run - the fields that identify the run itself
// rather than measuring any particular stage of it.
public sealed record RunIdentity(
    string         InstanceId,     // Durable orchestration ID — correlate with App Insights traces
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool           ForceReindex,   // true = all docs re-indexed regardless of last-modified date
    bool           Success,
    string?        ErrorMessage = null);
