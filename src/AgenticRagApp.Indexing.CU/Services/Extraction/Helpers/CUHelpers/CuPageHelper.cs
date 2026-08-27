using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Page structure from the typed response: PageSpans and PageDimensions from
// DocumentContent.Pages, Lines from Pages[].Lines, Boilerplate from the furniture-role
// paragraphs (PageHeader/PageFooter/PageNumber) - CU decides what is furniture, not a regex.
//
// Ownership rule (cuhelper-typed-structure-plan.md): this helper owns the furniture roles;
// CuOutlineHelper owns Title|SectionHeading. Footnote, FormulaBlock and role-less paragraphs
// are claimed by neither - deliberate, revisit only with a consumer.
internal static class CuPageHelper
{
    // CU's own page ranges, VERBATIM - one PageSpan per (page, span) pair, exactly the offsets
    // and lengths the service reported. Nothing is tiled, backfilled, anchored to 0 or clamped
    // (user decision 2026-08-26: the model accepts CU's output, not the other way around). So:
    // a page whose text is non-contiguous contributes several entries, a page with no spans
    // contributes none, and the separators between pages belong to no page at all - PageAt
    // answers 0 ("unknown") there rather than guessing.
    //
    // The consumers were audited for this shape: PageResolver is a pure interval-overlap test,
    // HeadingLocator.FindInPage falls back to a whole-document search when a page window
    // misses, and StructureFilter's dimensions lookup is a plain PageNumber match.
    internal static List<PageSpan> BuildPageSpans(
        DocumentContent document, string markdown, string? unit, List<string> warnings)
    {
        var pages = (document.Pages ?? Enumerable.Empty<DocumentPage>())
            .OrderBy(p => p.PageNumber)
            .ToList();

        if (pages.Count == 0)
        {
            // Map-what's-there: no typed pages, no page attribution - not a fabricated
            // full-document span. PageResolver reports (0,0) for every chunk, which its own
            // comment calls the honest answer.
            warnings.Add("Typed Pages missing from the CU response; no page attribution for this document.");
            return [];
        }

        var result        = new List<PageSpan>();
        var spanlessPages = 0;

        foreach (var page in pages)
        {
            var spans = page.Spans;
            if (spans is not { Count: > 0 })
            {
                spanlessPages++;
                continue;
            }

            var dimensions = DimensionsOf(page, unit);
            foreach (var span in spans.OrderBy(s => s.Offset))
            {
                // Diagnostics only - the span is still recorded verbatim. Every reader of
                // these offsets already bounds-checks its own slicing.
                if (span.Offset + span.Length > markdown.Length)
                    warnings.Add(
                        $"Page {page.PageNumber} span [{span.Offset}..{span.Offset + span.Length}] exceeds the markdown length ({markdown.Length}).");

                result.Add(new PageSpan(
                    page.PageNumber, span.Offset, span.Length,
                    Dimensions: dimensions,
                    // No local parser and no owned derivation to say otherwise - same stance
                    // the markdown mapper took.
                    IsPictureOnly: false));
            }
        }

        if (spanlessPages > 0)
            warnings.Add($"{spanlessPages} page(s) reported no spans; content on them cannot be page-attributed.");

        return result;
    }

    internal static List<PageDimensions> BuildPageDimensions(DocumentContent document, string? unit) =>
        [.. (document.Pages ?? Enumerable.Empty<DocumentPage>())
            .OrderBy(p => p.PageNumber)
            .Select(p => DimensionsOf(p, unit))
            .Where(d => d is not null)
            .Cast<PageDimensions>()];

    private static PageDimensions? DimensionsOf(DocumentPage page, string? unit) =>
        page.Width is null && page.Height is null
            ? null
            : new PageDimensions(page.PageNumber, page.Width, page.Height, unit ?? "");

    // No polygons: CU encodes geometry as an opaque Source string, not typed points - the
    // empty Polygon is honest, and LineInfo's highlight-on-source consumer does not exist yet.
    internal static List<LineInfo> BuildLines(DocumentContent document) =>
        [.. (document.Pages ?? Enumerable.Empty<DocumentPage>())
            .SelectMany(p => (p.Lines ?? Enumerable.Empty<DocumentLine>())
                .Select(l => new LineInfo(l.Content ?? "", l.Span?.Offset, p.PageNumber, Polygon: [])))];

    // The furniture paragraphs, as classified by CU itself. Same Heading record and role
    // strings the pipeline has always used, so StructureFilter and the reports read on.
    internal static List<Heading> BuildBoilerplate(
        DocumentContent document, IReadOnlyList<PageSpan> pageSpans)
    {
        var result = new List<Heading>();

        foreach (var paragraph in document.Paragraphs ?? Enumerable.Empty<DocumentParagraph>())
        {
            // SemanticRole is an extensible enum (no constant patterns), hence == not switch.
            var role = paragraph.Role == SemanticRole.PageHeader ? "pageHeader"
                     : paragraph.Role == SemanticRole.PageFooter ? "pageFooter"
                     : paragraph.Role == SemanticRole.PageNumber ? "pageNumber"
                     : null;
            if (role is null) continue;

            var text = paragraph.Content?.Trim() ?? "";
            if (text.Length == 0) continue;

            var offset = paragraph.Span?.Offset;
            result.Add(new Heading(text, role, offset, PageAt(pageSpans, offset), Depth: 1));
        }

        return result;
    }

    // Which page an offset falls on: the page whose reported span CONTAINS it, and nothing
    // else. 0 means "unknown" - a null offset, an offset in the gap between pages, or a
    // document with no typed pages at all. The honest answer, never a nearest-page guess
    // (user decision 2026-08-26); HeadingLocator and StructureFilter both tolerate 0.
    internal static int PageAt(IReadOnlyList<PageSpan> spans, int? offset)
    {
        if (offset is int o)
            foreach (var s in spans)
                if (o >= s.Offset && o < s.Offset + s.Length)
                    return s.PageNumber;

        return 0;
    }

    // How well the service says it read the document, summarised across every word on every
    // page (DocumentPage.Words[].Confidence). This helper owns it because it already owns
    // DocumentContent.Pages - see the class comment's ownership rule.
    //
    // Deliberately reduced to a summary HERE, not carried up as words: this walks tens of
    // thousands of DocumentWord entries on a 134-page document and keeps five doubles. Nothing
    // is added to PdfDocumentStructure.
    //
    // Words with no reported Confidence are skipped rather than counted as 0 - the same
    // absent-is-not-zero rule the rest of this mapper follows. A document where NO word carries
    // one yields null, which the report shows as blank.
    internal static WordConfidenceSummary? SummariseWordConfidence(DocumentContent document)
    {
        var confidences = new List<double>();

        foreach (var page in document.Pages ?? Enumerable.Empty<DocumentPage>())
            foreach (var word in page.Words ?? Enumerable.Empty<DocumentWord>())
                if (word.Confidence is { } confidence)
                    confidences.Add(confidence);

        return WordConfidenceSummary.From(confidences);
    }
}
