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

    // Wall-clock per stage (keys: extract | chunk | embed_upload), measured replay-safe in the
    // orchestrator via CurrentUtcDateTime deltas (observability plan 2.3, 2026-08-26). A stage
    // that never ran has no key. Before this, the only stage timing anywhere was embedding's
    // own TotalEmbeddingDurationMs - extraction's had to be reconstructed from host-log dumps
    // twice on 2026-08-26 alone.
    public IReadOnlyDictionary<string, long>? StageDurationsMs { get; init; }

    // A second index-statistics sample, taken at report time by SaveIndexReportActivity -
    // seconds after Embedding's own post-upload snapshot, which Azure Search's stats lag
    // regularly zeroes (plan 4.1). Verification only: the drift baseline stays owned by
    // IndexStatsMonitor, this is get-and-verify. Null when the stats read failed or reporting
    // is disabled; both samples zero is what the run-analysis snapshot_unverified flag keys on.
    public IndexStatsReadback? StatsReadback { get; init; }

    // The vector side of the live index definition - metric, width, HNSW parameters,
    // compression, vectorizer - read back at report time next to StatsReadback (2026-09-15).
    // Get-and-verify: BuildVectorSearch never sets the HNSW parameters, so this is the only place
    // the metric the index actually compares with is written down, and the Configured* fields on
    // it are what the run analysis flags drift against. Null when the read failed or the report
    // predates the field.
    public AgenticRagApp.Infrastructure.Clients.Search.IndexVectorConfig? VectorConfig { get; init; }

    // ── What the run cost (2026-09-15) ───────────────────────────────────────────────────────
    // Derived at report time from the stage records above plus the live vector config, rather
    // than measured again: every input already exists, split across stages because each stage
    // owns its own number. These three are the rollups that answer the questions a stage record
    // cannot - what did the whole run send to the API, what would a full rebuild cost, what are
    // the vectors occupying, and how many runs has this index definition survived.
    //
    // Null when their inputs are absent: no chunking stage, no vector config read, or no
    // configured rate. Null is "not derivable", never zero cost.
    public EmbeddingCostMetrics?   EmbeddingCost { get; init; }
    public VectorStorageMetrics?   VectorStorage { get; init; }
    public IndexDefinitionCounter? IndexDefinition { get; init; }

    // Null since the Zenya metadata removal (2026-08-26): the mechanism this counted never
    // existed on any blob, so PDF now reports "no equivalent concept".
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

// See PdfIndexRunReport.StatsReadback.
public sealed record IndexStatsReadback(long DocumentCount, long StorageSizeBytes, DateTimeOffset ReadAtUtc)
{
    // The vector field plus its HNSW graph (2026-09-15) - what counts against the tier's vector
    // quota, which is the limit that binds before StorageSize does. Init property so the existing
    // positional construction sites stay untouched. Null = the service did not report it.
    public long? VectorIndexSizeBytes { get; init; }
}
