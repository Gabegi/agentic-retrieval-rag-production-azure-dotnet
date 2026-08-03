using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

// Maintains the rolling full-corpus snapshot for one doc-type pipeline
// (pipeline-artifacts/snapshots/{source}/{yyyy}/{MM}/{dd}/{instanceId}/full-index.json) —
// snapshots for different sources (e.g. "pdf", "csv") are stored under separate prefixes
// and never merged. Dated like the other artifact/report paths so today's snapshot can be
// found by browsing without already knowing its instance ID.
//
// IMPORTANT operational note: the snapshot only ever gains chunks that actually pass through
// UpdateAsync (i.e. new/updated docs from a normal incremental run). A document that was
// already indexed before this feature existed, and is never updated again, will never appear
// in any snapshot. Run a `force=true` reindex once after deploying this so the first snapshot
// captures a complete baseline - after that, incremental runs keep it complete via the merge.
public interface ISnapshotService
{
    // newChunks: this run's freshly processed chunks (the same ones just sent to Search).
    // staleDocumentIds: document ids (updated + removed) whose old snapshot entries must be
    // dropped before the new ones are merged in.
    //
    // Returns the set of content hashes now live in the merged snapshot. Vector-cache eviction
    // is deliberately NOT done here — it's indexing-pipeline infra, not an observability
    // concern — the caller (which already owns the vector cache) uses this return value to
    // do its own IVectorCache.EvictOrphanedAsync call.
    Task<IReadOnlySet<string>> UpdateAsync<T>(
        string                source,
        IReadOnlyList<T>      newChunks,
        IReadOnlyList<string> staleDocumentIds,
        string                instanceId,
        DateTimeOffset        startedAt,
        CancellationToken     ct = default) where T : ISnapshotSource;

    // Reads the single most recent snapshot for a source, for index recovery - the same
    // rolling full-corpus picture UpdateAsync maintains, but read back instead of merged
    // into. InstanceId identifies which run's snapshot generation was used (empty chunks +
    // null InstanceId means no snapshot exists yet for this source).
    Task<(IReadOnlyList<SnapshotChunk> Chunks, string? InstanceId)> ReadLatestAsync(
        string source, CancellationToken ct = default);
}
