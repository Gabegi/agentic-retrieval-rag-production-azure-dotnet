namespace AgenticRagApp.Indexing.Pdf.Models;

// Per-document identity result FamilyIdEmbedder resolves before chunking - the same value
// on every chunk of one document, same carry-along pattern as DocumentRouting. FamilyId
// groups near-duplicate documents (embedding-clustered on title + heading list); DomainTag
// is the GGZ/GHZ/VVT/V&V/VGZ filename-pattern read off the title; ConfusableWith is the
// separate title-distance check (docs/2608/260811/pre-chunking-action-items.md C3) - titles
// close enough to be mistaken for each other without being embedding-similar (Medido/Medimo),
// so deliberately not folded into FamilyId itself.
public sealed record DocumentFamily(
    string                   FamilyId,
    string?                  DomainTag,
    IReadOnlyList<string>    ConfusableWith);

// Persisted record behind DocumentIdentityStore - one blob per SourceId, corpus-wide (not
// scoped to a single indexing run), so every run can cluster a newly-processed document
// against every document ever indexed, not just the other documents in its own batch.
// IdentityTextHash lets FamilyIdEmbedder skip re-embedding a document whose title/domain/
// headings haven't changed since the last run, same dedup shape as VectorCache's content hash;
// the embedding model id is folded into that hash, so a model change also forces a re-embed.
//
// EmbeddingModelId records which embedding space Vector actually lives in, so a record
// written under an older model can be recognised and held out of the comparison set instead
// of being cosine-compared against vectors from a different model. Nullable because records
// written before this field existed deserialize without it - those are treated as unknown,
// i.e. excluded from clustering until their document is next reindexed.
public sealed record DocumentIdentityRecord(
    string   SourceId,
    string   Title,
    string?  DomainTag,
    float[]  Vector,
    string   FamilyId,
    string   IdentityTextHash,
    string?  EmbeddingModelId = null);
