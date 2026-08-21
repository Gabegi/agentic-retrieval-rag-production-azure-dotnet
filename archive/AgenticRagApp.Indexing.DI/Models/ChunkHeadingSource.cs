namespace AgenticRagApp.Indexing.DI.Models;

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

    // A caption line standing immediately above a table, promoted to a section boundary
    // because Document Intelligence did not mark it as a heading.
    //
    // The CAO GHZ salary appendix is the measured case: DI detects ONE heading
    // ("Salarisschaal functiegroep 45") for a page carrying NINE salary tables, so the
    // section spans all nine and every chunk cut from it inherits that one heading. A chunk
    // holding functiegroep 50's pay scale was being labelled, embedded and cited as
    // functiegroep 45 - wrong attribution on pay data, which is worse than retrieving
    // nothing. Measured at 35 mislabelled chunks in the 260818 run.
    //
    // Kept distinct from DiHeading rather than folded into it: this boundary rests on a
    // layout heuristic, not on DI's own judgement, and "how much of the corpus is resting on
    // which signal" is the question this whole enum exists to answer.
    public const string TableCaption = "table_caption";

    // No heading covers this unit - preamble before the first heading, or a document with
    // no headings anywhere. Distinct from null, which would mean "not yet computed".
    public const string None = "none";
}
