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
    string  EmbeddingDeployment);
