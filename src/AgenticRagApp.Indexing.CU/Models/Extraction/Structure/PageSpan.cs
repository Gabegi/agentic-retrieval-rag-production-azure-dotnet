namespace AgenticRagApp.Indexing.CU.Models;

// Where one of a page's text ranges sits inside the document's markdown. Same shape as
// SectionSpan, deliberately - both answer "which range of this string is that thing".
//
// One entry per (page, span) pair, VERBATIM from Content Understanding's DocumentPage.Spans
// (user decision 2026-08-26: the model accepts CU's output, not the other way around). That
// means: a page whose text is non-contiguous appears several times, a page the service
// reported no spans for appears not at all, and the ranges need not tile the string - the
// separators between pages belong to no page. Anything asking "which page is offset X on"
// gets an honest 0 ("unknown") in those holes, never a nearest-page guess - see
// CuPageHelper.PageAt and PageResolver, both containment/overlap tests.
//
// Offsets address the document's markdown - the same string every structural offset in this
// folder addresses (utf16 spans, one coordinate system, nothing rewritten).
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
