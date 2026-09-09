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
// CuPageHelper.PageAt, HeadingLocator.PageAt and PageResolver, all containment/overlap tests
// with the same 0 answer (unified 2026-09-09).
//
// Offsets address the document's markdown - the same string every structural offset in this
// folder addresses (utf16 spans, one coordinate system, nothing rewritten).
//
// An IsPictureOnly flag sat here until 2026-09-09. It was a DI-era join ("a figure and no
// words on this page") that CU has no typed source for, so the mapper hardcoded it false on
// every span - and PageResolver's trailing-edge rule was justified by the zero-length spans it
// implied, which no longer existed either. Removed rather than carried as a constant.
public sealed record PageSpan(
    int PageNumber,
    int Offset,
    int Length,

    // Physical page geometry, for a future highlight-on-source feature - carried here
    // rather than as a parallel per-page list so it cannot drift out of step with the
    // spans. Null when the service reported no dimensions for the page.
    PageDimensions? Dimensions);
