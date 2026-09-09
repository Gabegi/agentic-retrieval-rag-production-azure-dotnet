using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Which pages a cut covers.
//
// A chunk that starts inside page 4 and runs into page 5 reports (4, 5) - the reason
// page_start/page_end replaced a single page number.
//
// A strict interval overlap: a span counts when the chunk's [start, end) and the span's
// [offset, offset+length) share at least one character. This used to be loose on the trailing
// edge (>=), justified by picture-only pages carrying zero-length spans that a strict test
// would never match - but CU reports no such spans (spanless pages contribute no PageSpan at
// all, see CuPageHelper.BuildPageSpans) and the picture-only flag itself is gone. What the
// loose edge actually did was attach a chunk beginning exactly where page N ends to page N.
//
// 0 means "unknown", the same answer CuPageHelper.PageAt and HeadingLocator.PageAt give
// (unified 2026-09-09): no spans at all, or a cut whose coordinates fall in a gap between
// pages. Not page 1 - a guessed citation is worse than an absent one, and a fallback to the
// first span made an unattributed chunk indistinguishable from one genuinely on page 1.
public static class PageResolver
{
    public static (int Start, int End) Resolve(
        IReadOnlyList<PageSpan> spans, int chunkStart, int chunkLength)
    {
        if (spans.Count == 0) return (0, 0);

        var chunkEnd = chunkStart + chunkLength;

        var covered = spans
            .Where(s => s.Offset < chunkEnd && s.Offset + s.Length > chunkStart)
            .ToList();

        if (covered.Count == 0) return (0, 0);

        return (covered[0].PageNumber, covered[^1].PageNumber);
    }
}
