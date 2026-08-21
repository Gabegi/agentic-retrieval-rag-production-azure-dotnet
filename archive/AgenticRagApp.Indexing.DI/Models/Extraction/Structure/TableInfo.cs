namespace AgenticRagApp.Indexing.DI.Models;

// Offset/PageNumber stay the anchor pattern (first BoundingRegion only) other Get*
// records use. Regions is deliberately different: a table is a 2D area, not a point in
// the content flow, and a table split across a page break has one BoundingRegion per
// page - anchor-only would silently discard every page after the first, and
// re-acquiring that geometry later means a paid re-analysis, not a re-read of stored
// data. So Regions follows SectionInfo's "every region" convention instead.
// Caption/Footnotes are free fields off the same DocumentTable GetTables already reads.
// A table chunk without its caption loses most of what makes the table findable by
// search - whoever builds the chunk-metadata step must carry Caption through into
// whatever text represents this table, not just the cell content.
public sealed record TableInfo(
    int RowCount,
    int ColumnCount,
    IReadOnlyList<TableCellInfo> Cells,
    int? Offset,
    int PageNumber,
    string? Caption,
    IReadOnlyList<string> Footnotes,
    IReadOnlyList<DocumentRegion> Regions);
