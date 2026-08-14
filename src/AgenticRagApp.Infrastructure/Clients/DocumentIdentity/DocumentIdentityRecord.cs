namespace AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

// Persisted record behind DocumentIdentityStore - one blob per SourceId, corpus-wide (not
// scoped to a single indexing run), so every run can cluster a newly-processed document
// against every document ever indexed, not just the other documents in its own batch.
// IdentityTextHash lets DocumentIdentityResolver skip re-embedding a document whose title/domain/
// headings haven't changed since the last run, same dedup shape as VectorCache's content hash;
// the embedding model id is folded into that hash, so a model change also forces a re-embed.
//
// EmbeddingModelId records which embedding space Vector actually lives in, so a record
// written under an older model can be recognised and held out of the comparison set instead
// of being cosine-compared against vectors from a different model. Nullable because records
// written before this field existed deserialize without it - those are treated as unknown,
// i.e. excluded from clustering until their document is next reindexed.
//
// Lives in Infrastructure with the store rather than beside DocumentFamily in Indexing.Pdf:
// Infrastructure cannot reference Indexing.Pdf (the dependency points the other way), so the
// record has to sit on the storage side of that line. DocumentFamily stays where it is - it is
// the per-chunk carry-along value, not the stored one.
public sealed record DocumentIdentityRecord(
    string   SourceId,
    string   Title,
    string?  DomainTag,
    float[]  Vector,
    string   FamilyId,
    string   IdentityTextHash,
    string?  EmbeddingModelId = null);
