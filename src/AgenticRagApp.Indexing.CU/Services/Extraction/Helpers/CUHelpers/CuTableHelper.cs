using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Tables from the typed response (DocumentContent.Tables) - the wishlist #1 element, typed
// instead of parsed: 288 HTML tables in the corpus that no pipe-row regex ever matched.
// Merged cells survive as RowSpan/ColumnSpan (57 rowspan / 151 colspan live), which the HTML
// representation carries and a GFM conversion would lose.
//
// Regions come off the Source string via CuGeometryHelper (2026-09-08). They used to be
// hardcoded empty on the grounds that CU "encodes geometry as an opaque Source string, not
// typed polygons" - true about the wire format, wrong about the SDK: DocumentSource.Parse
// decodes it. A table spanning a page break carries one region per page, which is why this is
// a list and not an anchor (see TableInfo).
internal static class CuTableHelper
{
    internal static List<TableInfo> Build(
        DocumentContent document, IReadOnlyList<PageSpan> pageSpans, List<string> warnings)
    {
        var result      = new List<TableInfo>();
        var unparseable = 0;

        foreach (var t in document.Tables ?? Enumerable.Empty<DocumentTable>())
        {
            var offset  = t.Span?.Offset;
            var regions = CuGeometryHelper.Parse(t.Source);

            if (CuGeometryHelper.FailedToParse(t.Source, regions))
                unparseable++;

            result.Add(new TableInfo(
                RowCount:    t.RowCount,
                ColumnCount: t.ColumnCount,
                Cells:       [.. (t.Cells ?? Enumerable.Empty<DocumentTableCell>())
                                .Select(c => new TableCellInfo(
                                    c.RowIndex, c.ColumnIndex,
                                    // "content" is the service's own default cell kind.
                                    c.Kind?.ToString() ?? "content",
                                    c.Content ?? "",
                                    c.RowSpan, c.ColumnSpan))],
                Offset:      offset,
                // The anchor page - first offset only. A table spanning pages 12-14 reports 12
                // here and all three in Regions; the page RANGE is A10's half of the work.
                PageNumber:  CuPageHelper.PageAt(pageSpans, offset),
                Caption:     t.Caption?.Content,
                Footnotes:   [.. (t.Footnotes ?? Enumerable.Empty<DocumentFootnote>())
                                .Select(f => f.Content)
                                .Where(c => !string.IsNullOrWhiteSpace(c))
                                .Cast<string>()],
                Regions:     regions));
        }

        // One aggregate line per document rather than one per table: 288 tables could otherwise
        // fill PdfExtractionOutput.Issues by themselves (capped at 100).
        if (unparseable > 0)
            warnings.Add(
                $"{unparseable} of {result.Count} table(s) reported a geometry Source this build could not parse; their Regions are empty.");

        return result;
    }
}
