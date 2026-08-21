namespace AgenticRagApp.Indexing.DI.Services;

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
    long?   IndexDocumentCountSnapshot,
    long?   IndexStorageSizeBytesSnapshot,
    string  SearchIndexName,
    string  EmbeddingModel,
    string  EmbeddingDeployment);
