namespace AgenticRagApp.Observability.Reports;

// Written to blob after every PDF indexing run.
// Path: pipeline-reports/indexing/{yyyy}/{MM}/{dd}/{instanceId}.json
//
// Composed from the run's identity plus one record per pipeline stage, rather than ~40
// flat fields copied out of those same stage records by hand. The old shape had a
// 45-line FromResults that assigned every field individually and substituted `?? 0` for
// a stage that never ran - which made "the embed stage crashed" and "the embed stage
// uploaded nothing" serialise identically. Here a stage that never ran is null, and the
// difference is visible to anything reading the JSON.
//
// How to use: compare two reports side-by-side after a source change or config tweak to
// see whether quality moved in the right direction. See docs/report-schema.md for what
// each stage's fields mean.
public sealed record PdfIndexRunReport
{
    public required RunIdentity Run { get; init; }

    // Each null when that stage never ran (an earlier stage threw, or the orchestration
    // was cut short). Null is not zero: it means "no measurement", not "measured nothing".
    public ExtractionStageMetrics?  Extraction { get; init; }
    public ChunkingStageMetrics?    Chunking   { get; init; }
    public EmbedUploadStageMetrics? Embedding  { get; init; }

    // Quality signal: documents with no zenya_document_id blob metadata set. Non-zero means
    // every citation built from them will show Citation.TraceabilityGap - this is the one
    // metric that tells you, without waiting for a query, how much of the corpus is
    // currently untraceable back to Zenya. Expected to be the full corpus count until
    // whoever uploads PDFs starts setting this metadata.
    //
    // Read off the extraction stage rather than stored again, so it can't drift from it.
    public int? TraceabilityGapCount => Extraction?.TraceabilityGapCount;

    // Convenience accessors for the handful of headline numbers callers log or assert on.
    // These read through to the stage records - they are not a second copy of the data.
    public string InstanceId    => Run.InstanceId;
    public bool   Success       => Run.Success;
    public string? ErrorMessage => Run.ErrorMessage;
    public int    DocsToProcess  => Extraction?.DocsToProcess  ?? 0;
    public int    ChunksProduced => Chunking?.ChunksProduced   ?? 0;
    public int    DocsUploaded   => Embedding?.DocsUploaded    ?? 0;
}
