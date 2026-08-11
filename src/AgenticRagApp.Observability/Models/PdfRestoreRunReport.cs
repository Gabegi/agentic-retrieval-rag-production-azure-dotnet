namespace AgenticRagApp.Observability.Reports;

// Written to blob after every PDF index-restore run (RestoreOrchestrator).
// Path: pipeline-reports/restore/{yyyy}/{MM}/{dd}/{instanceId}.json
//
// Distinct from PdfIndexRunReport: this is the "the index was wiped/corrupt and got rebuilt
// from the rolling snapshot" path, not a normal extract/chunk/embed run - there is no
// extraction/chunking/embedding here at all, just a bulk restore of whatever the snapshot
// (and vector cache) already had.
public record PdfRestoreRunReport(
    string         InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool           Success,
    string?        ErrorMessage,

    // Which snapshot generation this restore actually read from - null means no snapshot
    // existed for this source at all (nothing to restore).
    string? SnapshotInstanceId,

    int ChunksRestored,
    // Chunks the Search upsert itself reported as failed - the accurate, immediate success
    // signal (unlike IndexDocumentCountSnapshot below, which lags live writes by minutes).
    // RunRestoreOrchestrator gates Success on this being 0.
    int ChunksFailed,
    // Quality signal: a chunk restored without its cached vector needs re-embedding before
    // it's actually searchable via vector/hybrid search - it still exists as a document,
    // just without content_vector, until the next incremental run touches it.
    int ChunksMissingVector,

    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,

    // Which config/index this restore ran against - "which source/config versions were
    // used" for the recovery scenario.
    string SearchIndexName,
    string EmbeddingModel,
    string EmbeddingDeployment
);
