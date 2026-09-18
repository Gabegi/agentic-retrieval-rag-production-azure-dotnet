namespace AgenticRagApp.Observability.Reports;

// Who/when/what-happened for one indexing run - the fields that identify the run itself
// rather than measuring any particular stage of it.
public sealed record RunIdentity(
    string         InstanceId,     // Durable orchestration ID — correlate with App Insights traces
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool           ForceReindex,   // true = all docs re-indexed regardless of last-modified date
    bool           Success,
    string?        ErrorMessage = null,
    // The failing exception's type name, on its own (2026-09-17, D199 §8b item 3). ErrorMessage
    // is a stringified exception and classifying a failure meant regexing it - which put the
    // classification in whatever read the report, outside the repo and untested. One field
    // instead, populated on every failure path.
    //
    // In the isolated worker the orchestrator does NOT see the activity's exception: it gets
    // TaskFailedException carrying FailureDetails, where the original type survives only as a
    // string and custom properties do not survive at all. So this is read from FailureDetails
    // rather than from ex.GetType(), which would say "TaskFailedException" on every run.
    // Null = the report predates the field, or the run succeeded.
    string?        ErrorType    = null,
    // true = this run dropped and rebuilt the index before extracting (2026-09-17). Without it a
    // report cannot say WHY the index-state read was empty: "every document was new" reads
    // identically whether the index was deliberately emptied or the read failed — the ambiguity
    // that made run 9/260917/2 unreadable. EmptyIndexStateException used to fail the run on the
    // second case; it was removed the same day (IndexDiffService), so this field plus the
    // "Index-state read returned no documents" warning is now the ONLY way to tell them apart.
    //
    // Last, and defaulted, so reports written before it deserialize unchanged.
    bool           RecreateIndex = false);
