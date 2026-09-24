using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

// Maintains the rolling full-corpus snapshot for one doc-type pipeline, written via
// ReportPath (pipeline-reports/{yyyy}/{MM}/{dd}/{ts}-snapshot-{source}-{instanceId}.json) —
// snapshots for different sources (e.g. "pdf", "csv") never merge. The latest snapshot for a
// source is found via a _latest-snapshot-{source}.json pointer blob, not by listing, since the
// shared container no longer has a per-source prefix to scope a listing to.
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
    // Returns what is now live in the merged snapshot, at both grains the pipeline's two
    // corpus-scoped stores are keyed by: content hashes for the vector cache, document ids for
    // the document-identity store. Eviction itself is deliberately NOT done here — it's
    // indexing-pipeline infra, not an observability concern — the caller (which owns both
    // stores) uses this return value for its own EvictOrphanedAsync calls.
    // processedDocumentIds: every document this run re-extracted, whether or not it went on to
    // produce chunks. Its previous rows are dropped before this run's are added (2026-09-17,
    // D200 R1).
    //
    // staleDocumentIds alone was not enough, and the gap was silent: on the daily run the index
    // is recreated first, so the diff sees an empty index, every document reads as NEW rather
    // than updated, and the stale list is empty. Nothing was ever dropped, so each run appended
    // its whole corpus to the last - 65,728 rows against ~3,700 live chunks by 09-15, 93.4%
    // superseded, +8 MB per run forever. Worse than the size: EvictOrphanedAsync receives every
    // content hash the snapshot ever held, so no cache entry can ever look orphaned and eviction
    // is dead.
    //
    // Sourced from the EXTRACTED document list rather than from newChunks: a document that
    // extracted but produced zero chunks belongs in the drop set (it has no live chunks), and
    // deriving the set from the chunks would silently keep its superseded rows instead.
    Task<SnapshotLiveSet> UpdateAsync<T>(
        string                source,
        IReadOnlyList<T>      newChunks,
        IReadOnlyList<string> staleDocumentIds,
        IReadOnlyList<string> processedDocumentIds,
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

// The merged snapshot's live set, at the two grains the pipeline's corpus-scoped stores are
// keyed by. Both are derived from the same merged list, so they cannot disagree about what
// survived this run - which matters, because each one drives a delete.
public sealed record SnapshotLiveSet(
    IReadOnlySet<string> ContentHashes,
    IReadOnlySet<string> DocumentIds)
{
    // Rows the scheme guard dropped this run (D234 Step 8, 2026-09-24). Reported rather than only
    // logged because the one channel that would have carried a log line - App Insights - has
    // received nothing since 2026-07-13, so the run report is the only place a reader can see it.
    // 0 on every run after the one that cleans up, which is the point: a non-zero here says the
    // snapshot was carrying rows no drop set could reach.
    public int ForeignSchemeRowsDropped { get; init; }
}
