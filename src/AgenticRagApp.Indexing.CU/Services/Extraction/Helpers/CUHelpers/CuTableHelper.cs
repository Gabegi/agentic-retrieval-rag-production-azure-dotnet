using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Tables from the typed response (DocumentContent.Tables) - the wishlist #1 element, typed
// instead of parsed: 288 HTML tables in the corpus that no pipe-row regex ever matched.
// Merged cells survive as RowSpan/ColumnSpan (57 rowspan / 151 colspan live), which the HTML
// representation carries and a GFM conversion would lose.
//
// Regions stays empty: CU encodes geometry as an opaque Source string, not typed polygons.
internal static class CuTableHelper
{
    internal static List<TableInfo> Build(DocumentContent document, IReadOnlyList<PageSpan> pageSpans) =>
        [.. (document.Tables ?? Enumerable.Empty<DocumentTable>())
            .Select(t =>
            {
                var offset = t.Span?.Offset;
                return new TableInfo(
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
                    PageNumber:  CuPageHelper.PageAt(pageSpans, offset),
                    Caption:     t.Caption?.Content,
                    Footnotes:   [.. (t.Footnotes ?? Enumerable.Empty<DocumentFootnote>())
                                    .Select(f => f.Content)
                                    .Where(c => !string.IsNullOrWhiteSpace(c))
                                    .Cast<string>()],
                    Regions:     []);
            })];
}
