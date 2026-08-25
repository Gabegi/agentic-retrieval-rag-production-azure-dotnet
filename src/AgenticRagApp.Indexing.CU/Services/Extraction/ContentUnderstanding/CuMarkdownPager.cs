using System.Text;
using System.Text.RegularExpressions;
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Turns Content Understanding's one whole-document markdown string into the document Content
// this pipeline indexes, plus the exact PageSpans that say which range of it came from which
// page.
//
// WHY THIS EXISTS AT ALL, given CU already returns assembled markdown:
//
// CU encodes page metadata as HTML comments inline in that markdown - <!-- PageNumber="3" -->,
// <!-- PageHeader="..." -->, <!-- PageFooter="..." -->, <!-- PageBreak --> (see
// docs/2608/260824/cu-markdown-representation-notes.md). Left in, every one of those reaches
// the embedding, the chunk body, and exact-term search. The Document Intelligence pipeline had
// the identical problem and PdfCleaner stripped them; PdfCleaner is gone, so the strip lives
// here.
//
// STRIPPING INVALIDATES CU'S OWN OFFSETS, AND THAT IS FINE - it is the coordinate system this
// codebase already had. PageSpan's own comment states it: "Offsets address the CLEANED document
// text, not DI's raw content. Structural offsets (Heading.Offset, SectionSpan.Offset) address
// the raw content and are not comparable - see the heading locator for how the two coordinate
// systems are bridged." HeadingLocator never slices by a structural offset; it uses them only
// to order candidates and finds real positions by matching text. So:
//   - PageSpans are rebuilt here, against the stripped text, and are exact by construction.
//   - Structural offsets (CuStructureMapper) stay in CU's RAW coordinates, untouched.
// Stripping only ever removes characters, so raw-offset ORDERING - the only property anything
// consumes - is preserved.
internal static partial class CuMarkdownPager
{
    // The four page-metadata comment kinds, plus the line they sit on when they are alone on
    // it. Values are quoted and may contain escaped quotes ("" per the article's examples), so
    // the value body is "anything but a quote, or a doubled quote" rather than a lazy .*.
    [GeneratedRegex(@"[ \t]*<!--\s*Page(?:Number|Header|Footer|Break)(?:\s*=\s*""(?:[^""]|"""")*"")?\s*-->[ \t]*\r?\n?")]
    private static partial Regex PageMetadataComment();

    // The page-break comment on its own, used to segment the markdown when CU's per-page spans
    // are unusable. Kept separate from the strip regex because this one has to run BEFORE the
    // strip (it is the anchor) while the strip removes it.
    [GeneratedRegex(@"<!--\s*PageBreak\s*-->")]
    private static partial Regex PageBreakComment();

    // ![alt](figures/1.1 "description")  ->  alt + description as plain text.
    //
    // The path is dropped: it addresses CU's own figures output endpoint, which nothing in this
    // pipeline fetches, and a bare "figures/1.1" in the body is noise that embeds. The
    // description is the payload - it is the entire reason for moving to CU (PdfCleaner used to
    // DELETE caption-less figures outright, so an uncaptioned diagram contributed nothing).
    //
    // CU writes a single space as the alt text when it detected no text in the figure, so an
    // alt of " " must not become a stray space in the body.
    [GeneratedRegex(@"!\[(?<alt>(?:[^\]\\]|\\.)*)\]\((?<path>[^)\s]*)(?:\s+""(?<desc>(?:[^""]|"""")*)"")?\)")]
    private static partial Regex FigureImage();

    // Three or more newlines collapse to one blank line. Stripping a comment that occupied its
    // own line leaves exactly this behind, and it is the invariant PdfCleaner maintained
    // per page.
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    // One page after stripping/repair, with the page number it came from.
    internal sealed record PagedContent(int PageNumber, string Text, bool IsPictureOnly);

    // Thrown when the markdown cannot be split into the pages CU says the document has. The
    // caller records it as a file-level failure rather than indexing a document whose page map
    // is a guess - a wrong page map silently mis-cites every chunk in the document.
    internal sealed class PageSegmentationException(string message) : Exception(message);

    // Segments the raw markdown into per-page text, strips/repairs each page, then joins them
    // back into one Content string while recording each page's exact span in it.
    public static (string Content, IReadOnlyList<PageSpan> PageSpans) BuildContent(
        DocumentContent document,
        IReadOnlyList<PageDimensions> dimensions,
        IReadOnlyDictionary<int, bool> pictureOnlyByPage)
    {
        var raw      = document.Markdown ?? "";
        var pages    = document.Pages ?? [];
        var segments = Segment(raw, pages);

        var cleaned = new List<PagedContent>(segments.Count);
        foreach (var (pageNumber, rawSegment) in segments)
            cleaned.Add(new PagedContent(
                pageNumber,
                CleanSegment(rawSegment),
                pictureOnlyByPage.GetValueOrDefault(pageNumber)));

        return Join(cleaned, dimensions);
    }

    // --- Segmentation ---------------------------------------------------------

    // CU reports each page's extent in the markdown as DocumentPage.Spans (plural - a page can
    // own more than one range). Those are the authoritative segmentation when they are sane.
    //
    // Falls back to splitting on <!-- PageBreak --> when they are not, because the spans are the
    // one part of this mapping never verified against a real CU response (there is no captured
    // artifact in the repo yet - see the smoke endpoint). If both fail, the caller gets a loud
    // per-file failure rather than a document with an invented page map.
    internal static List<(int PageNumber, string Text)> Segment(
        string raw, IList<DocumentPage> pages)
    {
        if (pages.Count == 0)
            throw new PageSegmentationException("Content Understanding returned no pages for this document.");

        if (TrySegmentBySpans(raw, pages, out var bySpans)) return bySpans;

        var parts = PageBreakComment().Split(raw);
        if (parts.Length == pages.Count)
            return [.. parts.Select((text, i) => (pages[i].PageNumber, text))];

        throw new PageSegmentationException(
            $"Cannot map Content Understanding markdown onto pages: {pages.Count} page(s) reported, " +
            $"page spans unusable, and splitting on <!-- PageBreak --> produced {parts.Length} segment(s). " +
            "Indexing this document would give every chunk in it a guessed page number.");
    }

    private static bool TrySegmentBySpans(
        string raw, IList<DocumentPage> pages, out List<(int PageNumber, string Text)> segments)
    {
        segments = [];
        var cursor = 0;

        foreach (var page in pages)
        {
            var spans = page.Spans;
            if (spans is not { Count: > 0 }) return false;

            var start = spans.Min(s => s.Offset);
            var end   = spans.Max(s => s.Offset + s.Length);

            // Monotonic, in range, non-overlapping with what we already consumed. Any violation
            // means these offsets do not address this string the way we assume - fall back
            // rather than slice by them.
            if (start < cursor || end < start || end > raw.Length) return false;

            segments.Add((page.PageNumber, raw[start..end]));
            cursor = end;
        }

        return true;
    }

    // --- Per-page cleaning ----------------------------------------------------

    // Order matters, and every step runs INSIDE one page so that nothing it establishes can be
    // undone by the join afterwards.
    internal static string CleanSegment(string segment)
    {
        // 1. Page metadata comments - the whole reason this class exists.
        segment = PageMetadataComment().Replace(segment, "");

        // 2. Figure images -> their alt text and generated description as prose.
        segment = FigureImage().Replace(segment, m =>
        {
            var alt  = m.Groups["alt"].Value.Trim();
            var desc = m.Groups["desc"].Success ? m.Groups["desc"].Value.Replace("\"\"", "\"").Trim() : "";

            if (alt.Length == 0) return desc;
            if (desc.Length == 0) return alt;

            // Both present: the detected in-figure text, then the model's description of it.
            return $"{alt}\n\n{desc}";
        });

        // 3. Re-establish the blank-line invariant the strips above can break.
        segment = ExcessBlankLines().Replace(segment, "\n\n");

        // No character normalization. Page bodies reach the index in whatever form Content
        // Understanding produced - decomposed diacritics, ligature glyphs, NBSP and single-glyph
        // unit symbols included. Removed with ExtractedTextRepair; if it comes back it belongs
        // here AND at every other entry path (titles, headings, the HeadingLocator needle), or
        // the needle and the text it searches drift into different forms.
        return segment.Trim();
    }

    // --- Assembly -------------------------------------------------------------

    // The separator between two pages' text in the assembled document. A blank line is what
    // every downstream splitter already treats as a paragraph boundary, so joining with
    // anything else would invent a boundary shape nothing else recognises. Its length is
    // accounted for in the spans, so the offsets stay exact.
    private const string PageSeparator = "\n\n";

    // Ported from the Document Intelligence pipeline's BuildDocuments, whose rules are all
    // still load-bearing:
    // - No separator around a page that cleaned to nothing: appending one on both sides of an
    //   empty page produces a four-newline run, which is precisely what step 3 above exists to
    //   prevent, and assembly runs after cleaning so nothing re-collapses it.
    // - An empty page still gets a zero-length span, because dropping it would drop its
    //   IsPictureOnly flag - the only signal that a mostly-normal document has diagram pages in
    //   it (see PageSpan).
    private static (string Content, IReadOnlyList<PageSpan> PageSpans) Join(
        IReadOnlyList<PagedContent> pages, IReadOnlyList<PageDimensions> dimensions)
    {
        var dimensionsByPage = dimensions
            .DistinctBy(d => d.PageNumber)
            .ToDictionary(d => d.PageNumber, d => d);

        // Presized: without it the builder regrows and copies its way up to what can be a whole
        // large document's text. Separator count is an upper bound (blank pages don't get one),
        // which is the right side to err on for a capacity hint.
        var content = new StringBuilder(
            pages.Sum(p => p.Text.Length) + Math.Max(0, pages.Count - 1) * PageSeparator.Length);
        var spans = new List<PageSpan>(pages.Count);

        foreach (var page in pages)
        {
            if (content.Length > 0 && page.Text.Length > 0) content.Append(PageSeparator);

            spans.Add(new PageSpan(
                PageNumber:    page.PageNumber,
                Offset:        content.Length,
                Length:        page.Text.Length,
                Dimensions:    dimensionsByPage.GetValueOrDefault(page.PageNumber),
                IsPictureOnly: page.IsPictureOnly));

            content.Append(page.Text);
        }

        return (content.ToString(), spans);
    }
}
