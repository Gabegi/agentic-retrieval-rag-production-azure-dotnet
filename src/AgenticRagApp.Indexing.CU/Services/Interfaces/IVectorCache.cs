namespace AgenticRagApp.Indexing.CU.Services;

// Content-hash-keyed store of already-computed embedding vectors. Exists because the Search
// index's content_vector field is IsStored=false - not retrievable from Search once written -
// so this is the only place a previously-computed vector can be read back from, letting
// EmbeddingService skip the paid API call for a chunk whose content hasn't changed.
public interface IVectorCache
{
    // Null on a miss - never embedded before, or the cached entry is missing/corrupt.
    // Callers should still sanity-check the returned vector's length against the current
    // embedding config before trusting it (a model/dimension change can leave stale entries).
    //
    // Returns the vector with what the read cost (2026-09-18, D203 M2c/M3): the bytes the blob
    // weighed and the time the JSON took to parse. See VectorCacheTelemetry.cs.
    Task<CachedVector?> TryGetAsync(string contentHash, CancellationToken ct = default);

    // SetAsync does not create the container. Call this once per run before the first SetAsync;
    // it throws ContainerNotDeclaredException if the Terraform-declared container is missing
    // (2026-09-16, D197 action 1c - replaces the CreateIfNotExistsAsync SetAsync used to make
    // on every write).
    Task AssertContainerExistsAsync(CancellationToken ct = default);

    // Returns the serialized bytes written (2026-09-18, D203 M2c).
    Task<long> SetAsync(string contentHash, float[] vector, CancellationToken ct = default);

    // Deletes any cached vector whose hash isn't in liveHashes - cleans up entries for
    // chunks that no longer exist in any currently-indexed document. Called by
    // IndexingFunction right after SnapshotService writes a fresh full-corpus snapshot, using
    // that snapshot's hash set as liveHashes. Returns the listing and delete counts and clocks
    // separately (2026-09-18, D203 M5a) - see VectorCacheEviction.
    Task<VectorCacheEviction> EvictOrphanedAsync(IReadOnlySet<string> liveHashes, CancellationToken ct = default);
}
