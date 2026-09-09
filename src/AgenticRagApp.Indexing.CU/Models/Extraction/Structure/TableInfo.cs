namespace AgenticRagApp.Indexing.CU.Models;

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
    IReadOnlyList<DocumentRegion> Regions,
    // The service's own DocumentTable.Role as a string, mapped 2026-09-08 (A6). It is what
    // could distinguish a LAYOUT table from a DATA table, which is directly relevant to the
    // HTML-table routing work - but NOTHING ROUTES ON IT YET, deliberately: map it, report the
    // distribution, then decide, the same sequence FiguresByKind followed. Trailing default so
    // extraction blobs written before the field existed still deserialize; null = the service
    // reported no role, or the document predates the mapping.
    string? Role = null,
    // The span length in the markdown (DocumentTable.Span.Length), mapped 2026-09-09 so the
    // chunker can take table blocks off the typed span instead of regex-detecting <table> runs
    // (docs/2609/260909/cu-helpers-review.md, B). Trailing and nullable for the usual reason:
    // null on a blob extracted before it was mapped - such a document gets NO table blocks and
    // no has_table, which is absent, not a substitute.
    int? Length = null)
{
    // The pages this table actually occupies, from its regions - the reason Regions is a list
    // (2026-09-08). PageNumber is the ANCHOR: the page the table's first offset falls on, which
    // for a table spanning pages 12-14 is 12 and nothing else. Attaching such a table only to
    // chunks on page 12 is what made a continuation fragment on page 14 report no table at all.
    //
    // Falls back to the anchor when there is no geometry - a document extracted before regions
    // were parsed, or a response that reported none. Never empty, so a caller can always read a
    // range off it.
    public int PageStart => Regions.Count > 0 ? Regions.Min(r => r.PageNumber) : PageNumber;

    public int PageEnd => Regions.Count > 0 ? Regions.Max(r => r.PageNumber) : PageNumber;

    // Whether this table has any presence on the page range a chunk covers. An overlap test,
    // not a containment test: the chunk and the table each span pages, and either can be the
    // wider of the two.
    public bool OverlapsPages(int start, int end) => PageStart <= end && PageEnd >= start;
    // Whether the table's markup shares at least one character with [start, end) of the
    // markdown. False when the span is unknown (no Offset or no Length) - never a guess.
    public bool Overlaps(int start, int end) =>
        Offset is int o && Length is int l && o < end && o + l > start;
}
