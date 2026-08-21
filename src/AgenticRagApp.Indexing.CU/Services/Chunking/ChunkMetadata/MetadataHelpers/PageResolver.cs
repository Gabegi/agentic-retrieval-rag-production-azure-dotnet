using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Which pages a cut covers, and whether any of them was picture-only.
//
// A chunk that starts inside page 4 and runs into page 5 reports (4, 5) - the reason
// page_start/page_end replaced a single page number.
//
// Moved out of ChunkingService.ResolvePages unchanged. The interval test is deliberately
// LOOSE on the trailing edge (>= rather than >): a picture-only page usually contributes no
// text at all and so has a ZERO-LENGTH span, which a strict overlap test would never match.
// Widening it lets that span be picked up by whichever chunk straddles the point, which is
// intended - the flag is a document-level "there are diagram pages in here" signal riding on
// a chunk, not a claim about that chunk's own content (PageSpan.cs). It is not an off-by-one.
public static class PageResolver
{
    public static (int Start, int End, bool PictureOnly) Resolve(
        IReadOnlyList<PageSpan> spans, int chunkStart, int chunkLength)
    {
        // Extraction recorded no spans at all. Zero is the honest answer - page 1 would be a
        // guess, and a guessed citation is worse than an absent one.
        if (spans.Count == 0) return (0, 0, false);

        var chunkEnd = chunkStart + chunkLength;

        var covered = spans
            .Where(s => s.Offset < chunkEnd && s.Offset + s.Length >= chunkStart)
            .ToList();

        // Offsets that fall outside every span - a cut whose coordinates do not address this
        // document's assembled content. Fall back to the first page rather than reporting 0,
        // which would be indistinguishable from "no spans" above.
        if (covered.Count == 0)
            return (spans[0].PageNumber, spans[0].PageNumber, spans[0].IsPictureOnly);

        return (covered[0].PageNumber, covered[^1].PageNumber, covered.Any(s => s.IsPictureOnly));
    }
}
