
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Content Understanding's typed document model -> this pipeline's structure records.
//
// COORDINATE SYSTEM: every Offset produced here is in CU's RAW markdown coordinates, the same
// string CU reported them against, NOT the cleaned Content that CuMarkdownPager builds. That is
// the existing contract (see PdfDocumentStructure and PageSpan): structural offsets order and
// anchor, HeadingLocator bridges to cleaned coordinates by matching text. Do not compare an
// offset from this file against a PageSpan offset.
//
// WHAT CU DOES NOT PROVIDE, and is therefore empty rather than wrong:
// - Selection marks: no typed model at all. The ☒/☐ characters are still in the markdown, so
//   the CONTENT survives; only the typed list is gone.
// - Lines: CU has DocumentLine per page, but the LineInfo record exists to carry polygons for a
//   future highlight-on-source feature, and CU encodes geometry as an opaque Source string
//   rather than a polygon. Empty until something needs it enough to parse that string.
// - Region geometry on tables, for the same reason.
internal static class CuStructureMapper
{
    public static PdfDocumentStructure Map(DocumentContent document, IReadOnlyList<PageDimensions> dimensions)
    {
        var raw        = document.Markdown ?? "";
        var pageRanges = BuildPageRanges(document);

        var headings    = new List<Heading>();
        var boilerplate = new List<Heading>();

        foreach (var paragraph in document.Paragraphs ?? [])
        {
            var role   = paragraph.Role;
            var offset = paragraph.Span?.Offset;
            var page   = PageOf(offset, pageRanges);

            if (role == SemanticRole.Title || role == SemanticRole.SectionHeading)
                headings.Add(new Heading(
                    paragraph.Content ?? "", RoleName(role), offset, page,
                    ComputeDepth(role, offset, raw)));

            // Template furniture. Reuses the same record, and Depth stays at its default 1 -
            // never a real nesting claim, just an unread field (see Heading).
            else if (role == SemanticRole.PageHeader || role == SemanticRole.PageFooter ||
                     role == SemanticRole.PageNumber || role == SemanticRole.Footnote)
                boilerplate.Add(new Heading(paragraph.Content ?? "", RoleName(role), offset, page));
        }

        return new PdfDocumentStructure(
            Headings:       headings,
            Boilerplate:    boilerplate,
            Tables:         MapTables(document, pageRanges),
            PageDimensions: dimensions,
            SelectionMarks: [],
            Figures:        MapFigures(document, pageRanges),
            Lines:          [],
            Sections:       MapSections(document));
    }

    // --- Headings -------------------------------------------------------------

    // CU renders a SectionHeading paragraph as ATX markdown ("## Text") inline in the markdown,
    // with the "#" run immediately before the paragraph's own span offset - the same rendering
    // Document Intelligence used, and the same reason this is read off the string rather than a
    // structured level field: there isn't one, in either service's response.
    //
    // A Title is rendered setext ("Text\n===") rather than ATX, so it is forced to depth 1
    // instead of scanning for a marker that was never written. Not every SectionHeading gets a
    // "#" run either (a bare numbered TOC entry can carry the role without ATX rendering), so
    // this falls back to 1 whenever no run is found - never throws over a shape a depth guess
    // does not strictly need to understand.
    internal static int ComputeDepth(SemanticRole? role, int? offset, string markdown)
    {
        if (role == SemanticRole.Title) return 1;
        if (offset is not { } pos || pos <= 0 || pos > markdown.Length) return 1;

        // Skip the single space between the "#" run and the heading text ("## Text", never
        // "##Text") before counting the run itself.
        if (markdown[pos - 1] == ' ') pos--;

        var start = pos;
        while (start > 0 && markdown[start - 1] == '#') start--;

        var depth = pos - start;
        return depth is >= 1 and <= 6 ? depth : 1;
    }

    // The role name as this codebase has always spelled it - DI's camelCase strings, which are
    // what every downstream consumer, stored chunk and report already contains. Deriving it from
    // the CU enum's own ToString() would silently rename "sectionHeading" to "SectionHeading"
    // in the index.
    private static string RoleName(SemanticRole? role) =>
        role == SemanticRole.Title          ? "title"
      : role == SemanticRole.SectionHeading ? "sectionHeading"
      : role == SemanticRole.PageHeader     ? "pageHeader"
      : role == SemanticRole.PageFooter     ? "pageFooter"
      : role == SemanticRole.PageNumber     ? "pageNumber"
      : role == SemanticRole.Footnote       ? "footnote"
      : role?.ToString() ?? "";

    // --- Tables / figures / sections ------------------------------------------

    private static List<TableInfo> MapTables(DocumentContent document, List<(int Page, int Start, int End)> pageRanges) =>
        [.. (document.Tables ?? []).Select(t => new TableInfo(
            RowCount:    t.RowCount,
            ColumnCount: t.ColumnCount,
            Cells:       [.. (t.Cells ?? []).Select(c => new TableCellInfo(
                             c.RowIndex, c.ColumnIndex,
                             c.Kind?.ToString() ?? "content",
                             c.Content ?? "",
                             c.RowSpan, c.ColumnSpan))],
            Offset:      t.Span?.Offset,
            PageNumber:  PageOf(t.Span?.Offset, pageRanges),
            Caption:     t.Caption?.Content,
            Footnotes:   [.. (t.Footnotes ?? []).Select(f => f.Content ?? "")],
            // A table's 2D geometry, per page it spans. CU reports geometry as an encoded
            // Source string, not polygons - left empty rather than half-parsed.
            Regions:     []))];

    private static List<FigureInfo> MapFigures(DocumentContent document, List<(int Page, int Start, int End)> pageRanges) =>
        [.. (document.Figures ?? []).Select(f => new FigureInfo(
            Caption:     f.Caption?.Content,
            Offset:      f.Span?.Offset,
            PageNumber:  PageOf(f.Span?.Offset, pageRanges),
            Id:          f.Id,
            Elements:    [.. f.Elements ?? []],
            // The reason for the whole migration. DI had no equivalent, so PdfCleaner deleted
            // any figure without a caption; this is the generated description of what the
            // figure actually shows. It is also already inline in the markdown body (see
            // CuMarkdownPager) - carried here as well so chunk metadata can surface it
            // separately from the prose.
            Description: f.Description))];

    private static List<SectionInfo> MapSections(DocumentContent document) =>
        [.. (document.Sections ?? []).Select(s => new SectionInfo(
            Spans:    s.Span is { } span ? [new SectionSpan(span.Offset, span.Length)] : [],
            Elements: [.. s.Elements ?? []],
            // The dereferenced form of Elements. Left empty: its only consumer builds heading
            // chains from Spans, and resolving pointers is only worth doing when something
            // reads the result. Elements above keeps the raw pointers for traceability.
            ResolvedElements: []))];

    // --- Page resolution ------------------------------------------------------

    // Which page a raw markdown offset falls on. Built from CU's own per-page spans, so it
    // answers in the same coordinate system the offsets are in.
    //
    // PageNumber on these records is display/debug only (see Heading) - it never orders
    // anything - so an offset outside every known range falls back to the first page rather
    // than failing the document.
    private static List<(int Page, int Start, int End)> BuildPageRanges(DocumentContent document) =>
        [.. (document.Pages ?? [])
            .Where(p => p.Spans is { Count: > 0 })
            .Select(p => (
                Page:  p.PageNumber,
                Start: p.Spans.Min(s => s.Offset),
                End:   p.Spans.Max(s => s.Offset + s.Length)))];

    private static int PageOf(int? offset, List<(int Page, int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return 1;
        if (offset is not { } value) return ranges[0].Page;

        foreach (var (page, start, end) in ranges)
            if (value >= start && value < end) return page;

        return ranges[0].Page;
    }
}
