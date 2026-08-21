using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Every table, with cell position, kind (columnHeader vs content) and merge spans.
// RowSpan/ColumnSpan are null for an ordinary cell; without them a merged header
// cell looks like a missing cell downstream.
// Caption/Footnotes/Regions are free fields off the same DocumentTable already in
// hand - see TableInfo for why Regions captures every BoundingRegion rather than
// just the first.
internal static class GetTablesHelper
{
    // internal (not private): unit tested directly against a hand-built AnalyzeResult.
    public static IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
        (result.Tables ?? [])
            .Select(t => new TableInfo(
                t.RowCount,
                t.ColumnCount,
                (t.Cells ?? []).Select(c => new TableCellInfo(
                    c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content, c.RowSpan, c.ColumnSpan)).ToList(),
                DiGeometryHelpers.FirstOffset(t.Spans),
                DiGeometryHelpers.FirstPage(t.BoundingRegions),
                t.Caption?.Content,
                (t.Footnotes ?? []).Select(f => f.Content).ToList(),
                (t.BoundingRegions ?? []).Select(br => new DocumentRegion(
                    br.PageNumber, DiGeometryHelpers.ToPolygonPoints(br.Polygon))).ToList()))
            .ToList();
}
