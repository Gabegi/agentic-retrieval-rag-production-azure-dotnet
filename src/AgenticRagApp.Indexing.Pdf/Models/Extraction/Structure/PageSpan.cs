namespace AgenticRagApp.Indexing.Pdf.Models;

// Where one page's cleaned text sits inside its document's assembled Content
// (action-plan.md §3.1). Same shape as SectionSpan, deliberately - both answer "which
// range of this string is that thing".
//
// This is what lets extraction emit whole documents while chunking can still say which
// page a chunk started on. It is recorded during assembly by the component that does the
// concatenating, so it is exact: reconstructing it downstream would mean guessing the
// separator the assembler used, and every offset after a wrong guess is wrong.
//
// Offsets address the CLEANED document text, not DI's raw content. Structural offsets
// (Heading.Offset, SectionSpan.Offset) address the raw content and are not comparable -
// see the heading locator for how the two coordinate systems are bridged.
public sealed record PageSpan(
    int PageNumber,
    int Offset,
    int Length,

    // Physical page geometry, for a future highlight-on-source feature - carried here
    // rather than as a parallel per-page list so it cannot drift out of step with the
    // spans. Null when DI reported no dimensions for the page.
    PageDimensions? Dimensions,

    // This page has at least one figure AND no extractable words (or nothing survived
    // cleaning) - GetPictureOnlyPagesHelper's join. This is the only way a mixed document
    // (38 normal pages, 2 diagram pages) can be spotted: the document-level density gate
    // passes such a file comfortably.
    //
    // How it reaches a chunk is looser than "the chunk covering this page", because such a
    // page usually contributes no text at all and so has a zero-length span. ResolvePages
    // treats a span as covered on an interval overlap, so a zero-length span is picked up by
    // whichever chunk happens to straddle that single point - a chunk built entirely from the
    // neighbouring pages' text. That is intended: the flag is a document-level "there are
    // diagram pages in here" signal riding on a chunk, not a claim about that chunk's content.
    bool IsPictureOnly);
