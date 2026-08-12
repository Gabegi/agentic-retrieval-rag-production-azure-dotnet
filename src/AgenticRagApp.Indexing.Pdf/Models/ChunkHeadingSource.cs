namespace AgenticRagApp.Indexing.Pdf.Models;

// Which signal produced a chunk's heading (action-plan.md §4.6). One field with a stated
// provenance beats three half-populated heading fields, and it makes "how much of the
// corpus is resting on which signal" a facet query rather than an investigation.
//
// String constants for the same reason as ChunkGrain: this crosses the Search schema.
public static class ChunkHeadingSource
{
    // A Document Intelligence title/sectionHeading paragraph. The primary signal - it works
    // even when the PDF has no bookmark outline at all, which is most of this corpus.
    public const string DiHeading = "di_heading";

    // The PDF's own bookmark outline, via the page breadcrumb. Hierarchical where present,
    // but only 5 of 51 documents have an outline and the four largest have none.
    public const string Bookmark = "bookmark";

    // Document Intelligence's own nested section tree. Phase A measured its boundaries as
    // identical to the DI headings (99.4-100%, both directions), so it is kept as a
    // hierarchy cross-check rather than a boundary source - but a heading whose chain came
    // from section nesting rather than depth should say so.
    public const string DiSection = "di_section";

    // No heading covers this unit - preamble before the first heading, or a document with
    // no headings anywhere. Distinct from null, which would mean "not yet computed".
    public const string None = "none";
}
