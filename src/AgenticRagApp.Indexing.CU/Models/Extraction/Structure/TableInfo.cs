namespace AgenticRagApp.Indexing.CU.Models;

// Offset/PageNumber stay the anchor pattern (first BoundingRegion only) other Get*
// records use. Regions is deliberately different: a table is a 2D area, not a point in
// the content flow, and a table split across a page break has one BoundingRegion per
// page - anchor-only would silently discard every page after the first, and
// re-acquiring that geometry later means a paid re-analysis, not a re-read of stored
// data. So Regions follows SectionInfo's "every region" convention instead.
// Caption/Footnotes are free fields off the same DocumentTable CuTableHelper.Build already
// reads. Caption is carried for the record and NOT projected onto the chunk or the index
// (decided 2026-09-22, docs/2609/260922/table-chunking-review.md §4): on run 260921/1, 163 of
// 3,619 tables had one and 153 of those were already inside the table's own markdown as
// <caption>, so the chunk text carries them without any help; of the 10 that were not, 8 were
// the placeholder "Salarisschaal functiegroep :formula:" and 2 were real. A table_captions
// index field would buy those 2 and duplicate the 153. figure_captions is not a precedent:
// figure captions are often absent from the markdown, table captions almost never are.
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
    // HTML-table routing work - but NOTHING ROUTES ON IT, deliberately: map it, report the
    // distribution, then decide, the same sequence FiguresByKind followed. Decided 2026-09-22
    // (docs/2609/260922/table-chunking-review.md §4): route on nothing. Run 260921/1 reported
    // null on 3,619 of 3,619 tables, so there is nothing to route on; the same mapping pays off
    // on figures (FigureInfo.Role set on 3,606 of 9,116, routed by FigureTextCounter), so the
    // mapping stays and TablesByRole in the extraction report is where a populated Role would
    // first show up. Trailing default so extraction blobs written before the field existed
    // still deserialize; null = the service reported no role, or the document predates the
    // mapping.
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
