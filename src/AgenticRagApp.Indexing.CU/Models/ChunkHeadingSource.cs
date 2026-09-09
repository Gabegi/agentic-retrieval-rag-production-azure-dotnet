namespace AgenticRagApp.Indexing.CU.Models;

// Which signal produced a chunk's heading (action-plan.md §4.6). One field with a stated
// provenance beats three half-populated heading fields, and it makes "how much of the
// corpus is resting on which signal" a facet query rather than an investigation.
//
// String constants for the same reason as ChunkGrain: this crosses the Search schema.
public static class ChunkHeadingSource
{
    // A title/sectionHeading paragraph the analyzer classified - Content Understanding since
    // 2026-08; the constant keeps its DI-era value because it crosses the Search schema. The
    // primary signal - it works
    // even when the PDF has no outline at all, which is most of this corpus.
    public const string DiHeading = "di_heading";


    // The analyzer section tree. Phase A (DI-era) measured its boundaries as
    // identical to the headings (99.4-100%, both directions), so it is kept as a
    // hierarchy cross-check rather than a boundary source - but a heading whose chain came
    // from section nesting rather than depth should say so.
    public const string DiSection = "di_section";

    // "table_caption" was a fourth value until 2026-09-09: a caption line above a GFM table,
    // promoted to a boundary by TableCaptionSplitter. Dead under CU (HTML tables, no caption
    // lines - the CAO GHZ salary tables carry neither) and a layout heuristic besides; removed.
    // Rows indexed before then may still carry the value.

    // No heading covers this unit - preamble before the first heading, or a document with
    // no headings anywhere. Distinct from null, which would mean "not yet computed".
    public const string None = "none";
}
