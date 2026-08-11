using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Corpus-wide, cross-run store of per-document identity (title+domain+heading-list
// embedding, resolved FamilyId) - FamilyIdEmbedder's persistence layer. Unlike VectorCache
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
}
