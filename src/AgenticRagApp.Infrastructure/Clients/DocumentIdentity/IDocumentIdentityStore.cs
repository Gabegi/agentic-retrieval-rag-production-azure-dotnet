namespace AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

// Corpus-wide, cross-run store of per-document identity (title+domain+heading-list
// embedding, resolved FamilyId) - DocumentIdentityResolver's persistence layer. Unlike VectorCache
// (content-hash-keyed, evictable, purely a paid-call skip), every record here needs to stay
// readable as a whole set: clustering a newly-processed document requires comparing it
// against every document ever indexed, not just the current run's batch.
public interface IDocumentIdentityStore
{
    // Full corpus-wide set, one entry per SourceId. Used as the comparison set for cosine
    // clustering and the Levenshtein title-distance check - both need every prior document,
    // not just the ones touched by the current run.
    Task<IReadOnlyList<DocumentIdentityRecord>> GetAllAsync(CancellationToken ct = default);

    Task SetAsync(DocumentIdentityRecord record, CancellationToken ct = default);

    // Drops records for documents that are no longer in the corpus, against the live document
    // ids from the rolling snapshot - the same shape as IVectorCache.EvictOrphanedAsync, and
    // called from the same place.
    //
    // Without this the store is the one persistence layer that never forgets: stale chunks are
    // removed from Search and orphaned vectors are evicted from the cache, but a deleted
    // document's identity record kept clustering forever. A ghost record does real damage
    // rather than just wasting space - single-linkage means one sitting between two live
    // documents merges their families, it can be the lexicographically smallest member and so
    // *be* the family id, and it can be named in a live document's ConfusableWith pointing at
    // something no consumer can retrieve.
    //
    // Returns the number of records deleted.
    Task<int> EvictOrphanedAsync(IReadOnlySet<string> liveSourceIds, CancellationToken ct = default);
}
