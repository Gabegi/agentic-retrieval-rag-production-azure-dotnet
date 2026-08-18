using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The two keys a cut is identified by, both scoped to the document and the cut's position
// within it.
//
// NO PAGE NUMBER, on purpose: an id built from a page shifts for every chunk below an inserted
// page, and an id change in Search is a delete-plus-insert rather than a field update.
//
// These are ORDINAL ids, not content hashes. Draft §6.2 replaces Id with
// hash(embedString + embeddingModelId) when ChunkIndexer lands - the embedding model id is not
// in scope during chunking, and minting a hash without it would ship an id scheme that stage 3
// has to change again. Until then this is deterministic for the same cut of the same document,
// which is what the diff needs.
public static class ChunkIdBuilder
{
    // The section key uses index -1 where a child uses its ChildIndex, so a section can never
    // collide with its own first child.
    private const int SectionKeySentinel = -1;

    public static string ChunkId(string sourceId, int sectionIndex, int childIndex) =>
        ChunkingHelper.SafeKey(SectionScope(sourceId, sectionIndex), childIndex);

    // This IS parent_id - no separate field is needed. A grouping key for de-duplicating the
    // children of one section or fetching the rest of it, so nothing has to exist for it to
    // identify.
    public static string SectionId(string sourceId, int sectionIndex) =>
        ChunkingHelper.SafeKey(SectionScope(sourceId, sectionIndex), SectionKeySentinel);

    private static string SectionScope(string sourceId, int sectionIndex) =>
        $"{sourceId}::s{sectionIndex}";
}
