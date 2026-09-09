using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Page structure from the typed response: PageSpans (with their dimensions) from
// DocumentContent.Pages, Boilerplate from the furniture-role paragraphs
// (PageHeader/PageFooter/PageNumber) - CU decides what is furniture, not a regex - plus the
// page-scoped counts and rarities (lines, barcodes, formulas, word confidence).
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
    // HeadingLocator cuts at the heading's own offset and asks PageAt only for the preamble,
    // and StructureFilter's dimensions lookup is a plain PageNumber match.
    //
    // Dimensions ride on the span, not in a parallel per-page list: the list that used to sit
    // beside this on PdfDocumentStructure had no reader (removed 2026-09-09).
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

                result.Add(new PageSpan(page.PageNumber, span.Offset, span.Length, Dimensions: dimensions));
            }
        }

        if (spanlessPages > 0)
            warnings.Add($"{spanlessPages} page(s) reported no spans; content on them cannot be page-attributed.");

        return result;
    }

    private static PageDimensions? DimensionsOf(DocumentPage page, string? unit) =>
        page.Width is null && page.Height is null
            ? null
            : new PageDimensions(page.PageNumber, page.Width, page.Height, unit ?? "");

    // Lines are COUNTED, not carried (2026-09-09). The LineInfo list this replaces held every
    // line's text and offset with an always-empty polygon: the geometry was dropped BY COST
    // (a polygon per text line was 178 KB per document and 57% of the whole extraction payload,
    // see ChunkStructure), and without it the list served no consumer - the highlight-on-source
    // feature it was kept for cannot be built from text and offsets alone. Its only reader was
    // the file-facts report's count column, which this still feeds. Line geometry is readable
    // (l.Source decodes through CuGeometryHelper exactly as the tables' does) the day a consumer
    // exists.
    internal static int CountLines(DocumentContent document) =>
        (document.Pages ?? Enumerable.Empty<DocumentPage>()).Sum(p => p.Lines?.Count ?? 0);

    // Barcodes and formulas, both page-scoped (Pages[].Barcodes / Pages[].Formulas) and both
    // mapped to be counted rather than used - see BarcodeInfo and FormulaInfo.
    //
    // The page number comes from the OWNING page, not from PageAt: unlike a heading or a table,
    // these elements are already page-scoped in the response, so resolving their offset against
    // the page spans would be a slower way of asking a question already answered - and would
    // report 0 for an element whose offset falls in a gap between spans.
    internal static List<BarcodeInfo> BuildBarcodes(DocumentContent document) =>
        [.. (document.Pages ?? Enumerable.Empty<DocumentPage>())
            .SelectMany(p => (p.Barcodes ?? Enumerable.Empty<DocumentBarcode>())
                .Select(b => new BarcodeInfo(
                    b.Kind.ToString(), b.Value, b.Span?.Offset, p.PageNumber, b.Confidence)))];

    internal static List<FormulaInfo> BuildFormulas(DocumentContent document) =>
        [.. (document.Pages ?? Enumerable.Empty<DocumentPage>())
            .SelectMany(p => (p.Formulas ?? Enumerable.Empty<DocumentFormula>())
                .Select(f => new FormulaInfo(
                    f.Kind.ToString(), f.Value, f.Span?.Offset, p.PageNumber, f.Confidence)))];

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
    // (user decision 2026-08-26); HeadingLocator.PageAt and PageResolver give the same 0.
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
