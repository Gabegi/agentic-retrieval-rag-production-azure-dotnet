namespace AgenticRagApp.Indexing.CU.Services;

// Rebuilds the Search index's content from the rolling full-corpus snapshot
// (Observability's ISnapshotService) instead of re-extracting/re-chunking/re-embedding
// every source document - the fast recovery path for "the index is corrupt or incomplete".
// Callers are expected to have already wiped/recreated the index (IIndexService.RecreateIndexAsync)
// before calling this - RestoreService only repopulates, it doesn't own index lifecycle.
public interface IRestoreService
{
    Task<RestoreResult> RestoreFromLatestSnapshotAsync(CancellationToken ct = default);
}

public record RestoreResult(
    string? SnapshotInstanceId,
    int     ChunksRestored,
    int     ChunksFailed,
    int     ChunksMissingVector,
    // Restored chunks withheld from the index because the vector the cache handed back failed
    // VectorHealth.Classify (2026-09-17, D199 A1). Reported on its own rather than folded into
    // ChunksFailed the way the indexing path folds it, because on this path nothing else can say
    // why: the run report's VectorDimErrors and EmptyVectors count what the EMBEDDER produced,
    // and a restore embeds nothing. Structurally 0 - a non-zero here means a cached vector no
    // longer matches the configured width, i.e. the cache outlived a dimension change.
    int     ChunksWithheld,
    long?   IndexDocumentCountSnapshot,
    long?   IndexStorageSizeBytesSnapshot,
    string  SearchIndexName,
    string  EmbeddingModel,
    string  EmbeddingDeployment)
{
    // Snapshot rows skipped because their document is no longer in the live source listing
    // (2026-09-24, D234 Step 8). Init property rather than positional so every existing
    // construction site keeps compiling and says nothing false by omission.
    //
    // Structurally 0 once the scheme guard in SnapshotService has cleaned a snapshot, and that is
    // the point of reporting it: a non-zero here means the blob still holds rows for documents
    // that no longer exist, which before this guard would have been restored into the index as
    // live content - 3,723 of them on the 2026-09-24 snapshot.
    public int ChunksSkippedNotInSource { get; init; }
}
