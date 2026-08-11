using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Reports;

// The assembled view of one run: everything the email renders, and — serialized — the exact
// payload attached as run-summary-{instanceId}.json so the whole run can be handed to an agent
// without anyone re-deriving it from blobs.
//
// This is also precisely what the analysis agent is given. Keeping "what the reader sees" and
// "what the model saw" the same object is deliberate: it makes the agent's claims checkable
// against the same numbers printed below them.
public sealed record RunEmailSummary
{
    public required RunReportKind Kind { get; init; }
    public required string        InstanceId { get; init; }
    public required string        BlobPath { get; init; }

    // ── Index runs ──────────────────────────────────────────────────────────
    // Null on a restore run. The three stage records keep their own null-means-"did not run"
    // semantics; the renderer must print "did not run", never 0.
    public PdfIndexRunReport?   IndexReport { get; init; }
    public PdfRestoreRunReport? RestoreReport { get; init; }

    // ── Sibling stage reports (best-effort) ─────────────────────────────────
    // Each null when not found. Absence is normal for some (a failure report only exists when
    // extraction crashed) and a real signal for others, so the footer lists which were found.
    public ValidationReportFacts? Validation { get; init; }
    public FileFactsSummary?      FileFacts { get; init; }
    public ExtractionDiffFacts?   Diff { get; init; }
    public FailureReportFacts?    Failure { get; init; }

    // ── Context ─────────────────────────────────────────────────────────────
    public long?              CorpusDocumentCount { get; init; }
    public PreviousRunPointer? Previous { get; init; }
    public EvalBaseline?      EvalBaseline { get; init; }

    public required IReadOnlyList<ReportFlag> Flags { get; init; }
    public required IReadOnlyList<string>     SourcesFound { get; init; }
    public required IReadOnlyList<string>     SourcesMissing { get; init; }

    // Populated last, by the analysis agent. Null when the model call failed - the email still
    // sends everything above it.
    public RunAssessment? Assessment { get; init; }

    public bool Success => IndexReport?.Run.Success ?? RestoreReport?.Success ?? false;

    public string Verdict => Kind == RunReportKind.Restore
        ? "RESTORE"
        : !Success                                              ? "FAIL"
        : Flags.Any(f => f.Severity >= FlagSeverity.Warning)    ? "WARN"
        : "OK";
}

// The subset of PdfQualityGateResult worth carrying into an email. Deliberately a projection,
// not the whole object: SpotCheckSample and Issues are unbounded and the full record would
// dominate the attachment.
public sealed record ValidationReportFacts(
    bool Passed,
    int  ControlCharsStripped,
    int  InvisibleCharsStripped,
    int  LigaturesExpanded,
    int  HyphenationJoinsRepaired,
    int  TableConversionFallbacks,
    int  MojibakeRepairedPages,
    int  DetectedTableCount,
    IReadOnlyList<string> MagnitudeWarnings,
    IReadOnlyList<string> RedFlags,
    IReadOnlyList<string> DocumentsNeedingFallbackChunking);

// Aggregated, never per-file rows: a 900-document corpus would otherwise put 900 objects in the
// attachment. EstimatedCostUsd is the only Document Intelligence spend figure the pipeline
// produces and appears in no run report.
public sealed record FileFactsSummary(
    int    FileCount,
    double EstimatedCostUsd,
    long   TotalBytes,
    int    FilesWithoutProducer,
    IReadOnlyDictionary<string, int> SpecVersionHistogram);

// Counts are already in ExtractionStageMetrics; the *names* are what this adds. Capped -
// "3 documents deleted" is a number, "verzuimprotocol.pdf was deleted" is something to act on.
public sealed record ExtractionDiffFacts(
    int NewCount,
    int Updated,
    int Skipped,
    int RemovedCount,
    IReadOnlyList<string> RemovedSourceIds,
    IReadOnlyList<string> ProcessedSourceIds,
    bool NamesTruncated);

public sealed record FailureReportFacts(
    DateTimeOffset RunAt,
    string ExceptionType,
    string Message,
    string? StackTraceExcerpt);

// Written to runs/_last-run.json after each successful send and read at the start of the next.
// A pointer blob rather than a folder walk: indexing runs are infrequent, so the previous run is
// usually days or weeks back, and any "look in today's folder" approach loses the delta on the
// first run of every day - which is most runs.
public sealed record PreviousRunPointer(
    string         InstanceId,
    string         BlobPath,
    DateTimeOffset FinishedAt,
    bool           Success,
    int?           DocsToProcess,
    int?           ChunksProduced,
    int?           DocsUploaded,
    double?        CoherentChunkRatio,
    long?          IndexDocumentCount);

// The most recent answer-quality eval, if one exists. Explicitly a PRE-RUN baseline: an eval
// measures the index as it stood when it ran, which is necessarily before the run being
// reported. Rendering it as this run's quality would attribute the previous index state's
// scores to this run.
public sealed record EvalBaseline(
    string         ExecutionId,
    DateTimeOffset RanAt,
    int            ScenarioCount,
    int            FailedCount,
    double?        MeanGroundedness,
    double?        MeanRelevance,
    double?        MeanCoherence,
    double?        MeanEquivalence,
    double?        MeanCitationMatch,
    double?        MeanRefusalScore,
    double?        MeanContextTokens,
    double         TotalCostUsd);

public sealed record RunAssessment(
    string Narrative,
    IReadOnlyList<ImprovementSuggestion> Suggestions,
    string WhatIsFine);

public sealed record ImprovementSuggestion(
    string Suggestion,
    // Mandatory. An ungrounded suggestion is worse than none - it gets acted on once and
    // trusted thereafter, so the renderer drops any suggestion arriving without evidence.
    string Evidence,
    string ExpectedImpact,
    string Effort);
