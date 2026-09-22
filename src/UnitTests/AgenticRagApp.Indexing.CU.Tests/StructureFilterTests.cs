using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The document's structure narrowed to the pages one chunk covers. Tables are the one element
// filtered on a page RANGE rather than an anchor page, which is the second half of the
// cross-page table work (A10): the rows can be cut correctly and still be attributed to the
// wrong page if the table itself is only ever attached where its first offset happens to fall.
[TestClass]
public class StructureFilterTests
{
    [TestMethod]
    public void ATableSpanningAPageBreak_ReachesChunksOnEveryPageItOccupies()
    {
        // Anchor page 12, regions on 12/13/14 - the shape a CAO salary table has.
        var table = TableWithRegions(anchorPage: 12, pages: [12, 13, 14]);
        var doc   = DocumentWith(table);

        Assert.AreEqual(1, StructureFilter.Build(doc, 12, 12).Tables.Count);
        Assert.AreEqual(1, StructureFilter.Build(doc, 13, 13).Tables.Count, "the continuation page");
        Assert.AreEqual(1, StructureFilter.Build(doc, 14, 14).Tables.Count, "the last page");
        Assert.AreEqual(0, StructureFilter.Build(doc, 15, 16).Tables.Count, "past the table");
        Assert.AreEqual(0, StructureFilter.Build(doc, 10, 11).Tables.Count, "before the table");
    }

    [TestMethod]
    public void WithNoGeometry_TheAnchorPageIsStillTheAnswer()
    {
        // A document extracted before regions were parsed, or a response that reported none:
        // the old behaviour exactly, rather than a table that reaches nothing.
        var doc = DocumentWith(TableWithRegions(anchorPage: 12, pages: []));

        Assert.AreEqual(1, StructureFilter.Build(doc, 12, 12).Tables.Count);
        Assert.AreEqual(0, StructureFilter.Build(doc, 13, 13).Tables.Count);
    }

    [TestMethod]
    public void AChunkSpanningPages_PicksUpATableThatOnlyOverlapsIt()
    {
        // Overlap, not containment: either side can be the wider one.
        var doc = DocumentWith(TableWithRegions(anchorPage: 12, pages: [12, 13]));

        Assert.AreEqual(1, StructureFilter.Build(doc, 13, 20).Tables.Count);
        Assert.AreEqual(1, StructureFilter.Build(doc, 1, 12).Tables.Count);
    }

    [TestMethod]
    public void AnAttachedTable_CarriesItsShapeButNotItsCells()
    {
        // The page-range test attaches one table to every chunk on every page it touches, and
        // the cells were 43.5% of the 282 MB chunking artifact on 260921/1. Nothing on the chunk
        // side reads them; the extraction artifact keeps them in full.
        var doc      = DocumentWith(TableWithRegions(anchorPage: 12, pages: [12, 13]));
        var attached = StructureFilter.Build(doc, 12, 12).Tables.Single();

        Assert.AreEqual(0, attached.Cells.Count);
        Assert.AreEqual(2, attached.RowCount);
        Assert.AreEqual(2, attached.ColumnCount);
        Assert.AreEqual("Tabel 5", attached.Caption);
        Assert.AreEqual(2, attached.Regions.Count);
    }

    private static TableInfo TableWithRegions(int anchorPage, int[] pages) =>
        new(RowCount:    2,
            ColumnCount: 2,
            Cells:       [new TableCellInfo(0, 0, "columnHeader", "Naam", null, null)],
            Offset:      0,
            PageNumber:  anchorPage,
            Caption:     "Tabel 5",
            Footnotes:   [],
            Regions:     [.. pages.Select(p => new DocumentRegion(p, []))]);

    private static PdfExtractionDocument DocumentWith(TableInfo table) =>
        Doc("inhoud van het document") with { Tables = [table] };
}
