namespace AgenticRagApp.Indexing.Pdf.Models;

// One unit produced by a document chunking strategy, before ChunkingService turns it into a
// ChunkObject (adds ids, document metadata, page attribution, the embedded prefix).
//
// Replaces the flat TextChunk the old IChunkingStrategy returned. A flat list could not
// express two grains at all: it had an Index and a Content and nothing to say which section a
// piece belonged to, where it sat inside that section, or what the section's own text was.
public sealed record ChunkUnit(
    // "document" | "parent" | "child" - see ChunkGrain.
    string Grain,

    // Position of the section within the document, and of this child within that section.
    // Together with the document id they compose the chunk's identity, which is why neither
    // is optional even on a single-child section.
    int SectionIndex,
    int ChildIndex,

    // This unit's own text, without the title/sector/heading prefix - ChunkingService adds
    // that, because the prefix is a property of how the chunk is embedded rather than of how
    // the document was cut.
    string Content,

    // The whole section's text, materialized so retrieval never needs a second round trip
    // ("materialize, don't assemble").
    //
    // NULL when the section was not split, because then this unit IS the section and
    // ParentText would be a byte-for-byte copy of Content. Phase A measured 83-87% of
    // sections as never split, so this is the common case, and storing the copy would roughly
    // double the corpus's stored text to say nothing.
    string? ParentText,

    // The section's own heading (leaf) and its full chain.
    string? HeadingText,
    string? HeadingPath,
    int     HeadingDepth,
    string  HeadingSource,

    // False when the heading could not be located in the cleaned text and a fallback was
    // used - the per-chunk form of the locator's failure counter.
    bool HeadingLocated,

    // This child carries overlap from its predecessor.
    bool IsOverlap,

    // Where this unit's text starts in the document's cleaned Content, and how long it is.
    // Used to resolve which pages the unit covers via the PageSpan map; carried here rather
    // than re-found by string search, which would pick the wrong occurrence whenever overlap
    // makes two units share text.
    int Start,
    int Length);
