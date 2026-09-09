using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The cutter and the markup reader together. Which block IS a table is not tested here any
// more: since 2026-09-09 that comes off the typed span (BlockParserTests), and this file only
// covers what happens to a block the service already called a table.
//
// The shape Content Understanding actually emits - all 288 tables in the corpus - is HTML on one
// line. The GFM pipe path this file used to test alongside it went 2026-09-09: CU never emits
// GFM (tableFormat is fixed at html), and the regex detection it belonged to is gone.
[TestClass]
public class TableCuttingTests
{
    private const string HtmlHeader = "<tr><th>Functie</th><th>Schaal</th><th>Bedrag</th></tr>";

    // One line, as CU writes it: the markup carries no newlines of its own.
    private static string HtmlTable(int rows, bool caption = true, bool header = true) =>
        "<table>" +
        (caption ? "<caption>Tabel 5 Salarisschalen</caption>" : "") +
        (header ? HtmlHeader : "") +
        string.Concat(Enumerable.Range(0, rows).Select(i =>
            $"<tr><td>Verpleegkundige niveau {i}</td><td>FWG {35 + i}</td><td>{2000 + i},00</td></tr>")) +
        "</table>";

    // ── the markup reader ────────────────────────────────────────────────────

    [TestMethod]
    public void RowsArePairedOpenToClose_InOrder()
    {
        var text = HtmlTable(3);
        var rows = TableMarkup.Rows(text);

        Assert.AreEqual(4, rows.Count, "one header row plus three data rows");
        foreach (var (start, end) in rows)
        {
            StringAssert.StartsWith(text[start..end], "<tr>");
            StringAssert.EndsWith(text[start..end], "</tr>");
        }
        Assert.IsTrue(rows.Zip(rows.Skip(1)).All(p => p.First.End <= p.Second.Start), "rows do not overlap");
    }

    [TestMethod]
    public void TheHeaderEndsAfterTheThRows_OrBeforeTheFirstRowWhenThereAreNone()
    {
        var withHeader = HtmlTable(2);
        var rows       = TableMarkup.Rows(withHeader);
        Assert.AreEqual(rows[0].End, TableMarkup.HeaderEnd(withHeader, rows));

        var noHeader = HtmlTable(2, caption: false, header: false);
        var dataRows = TableMarkup.Rows(noHeader);
        Assert.AreEqual(dataRows[0].Start, TableMarkup.HeaderEnd(noHeader, dataRows),
            "a table whose first row is data repeats no row - repeating one would duplicate a record");
    }

    [TestMethod]
    public void TheTableEndsAtItsClosingTag_OrAtTheTextEndWhenCutOff()
    {
        var text = HtmlTable(1);
        Assert.AreEqual(text.Length, TableMarkup.TableEnd(text));

        var withTail = text + " Bron: CAO.";
        Assert.AreEqual(text.Length, TableMarkup.TableEnd(withTail));

        const string cutOff = "<table><tr><td>a</td></tr>";
        Assert.AreEqual(cutOff.Length, TableMarkup.TableEnd(cutOff));
    }

    [TestMethod]
    public void ARowWithoutAClosingTag_IsNotAnRow()
    {
        // The cutter can only cut on a boundary the markup declares.
        const string text = "<table><tr><td>open</td><tr><td>closed</td></tr></table>";

        var rows = TableMarkup.Rows(text);

        Assert.AreEqual(1, rows.Count);
        StringAssert.Contains(text[rows[0].Start..rows[0].End], "closed");
    }

    // ── the cutter ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ASmallHtmlTableStaysAtomic()
    {
        var text = HtmlTable(2);

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), Tokens(text));

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
        Assert.IsFalse(pieces[0].Degraded);
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void EveryHtmlFragmentIsWellFormedAndRepeatsTheHeaderAndCaption()
    {
        // A header-less run of numbers means nothing to the embedder or to the model reading
        // the retrieved chunk - "1.847" is not an answer to anything.
        var text = HtmlTable(30);

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 120);

        Assert.IsTrue(pieces.Count > 1, "a 30-row table over the ceiling must be cut");
        foreach (var piece in pieces)
        {
            StringAssert.StartsWith(piece.Text, "<table>");
            StringAssert.EndsWith(piece.Text, "</table>");
            StringAssert.Contains(piece.Text, HtmlHeader);
            // The caption is what makes a table fragment findable at all - see TableInfo.
            StringAssert.Contains(piece.Text, "<caption>Tabel 5 Salarisschalen</caption>");
            // No dangling cell: every opened tag in the fragment is closed in it.
            Assert.AreEqual(
                CountOf(piece.Text, "<tr"), CountOf(piece.Text, "</tr>"),
                "unbalanced rows in: " + piece.Text);
            Assert.AreEqual(
                CountOf(piece.Text, "<td"), CountOf(piece.Text, "</td>"),
                "unbalanced cells in: " + piece.Text);
        }
    }

    [TestMethod]
    public void HtmlDataRowsSurviveExactlyOnce_AcrossEveryFragment()
    {
        // Never overlapped: repeating DATA rows duplicates records, which is not the same thing
        // as restoring context.
        var pieces = TableCutter.Cut(Block(HtmlTable(30), BlockKind.Table), 120);

        var dataRows = pieces
            .SelectMany(p => RowsOf(p.Text))
            .Where(row => !row.Contains("<th", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(30, dataRows.Count, "every row survives the cut exactly once");
        Assert.AreEqual(dataRows.Count, dataRows.Distinct().Count());
    }

    [TestMethod]
    public void AFragmentsCoordinatesAddressItsOwnRows_NotTheRepeatedHeader()
    {
        // What keeps page attribution landing on the right pages for a table spanning a page
        // break: the fragment's Start is where its DATA is, not where the header it borrowed sits.
        var text   = HtmlTable(30);
        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 120);

        AssertSliceInvariant(text, pieces);

        foreach (var piece in pieces)
        {
            var addressed = text.Substring(piece.Start, piece.Length);
            Assert.IsFalse(addressed.Contains("<caption>", StringComparison.Ordinal));
            Assert.IsFalse(addressed.Contains("<th", StringComparison.Ordinal));
            StringAssert.Contains(piece.Text, addressed);
        }
    }

    [TestMethod]
    public void EveryPieceCarriesTheTableRowBoundary_WhenACutWasMade()
    {
        var pieces = TableCutter.Cut(Block(HtmlTable(30), BlockKind.Table), 120);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.TableRow));
    }

    [TestMethod]
    public void MergedCellsSurviveTheCut_WhichIsWhyGfmConversionWasRejected()
    {
        // 57 rowspan and 151 colspan cells are live in the corpus; converting to GFM to reuse
        // the pipe path would silently drop them.
        var text =
            "<table><tr><th colspan=\"2\">Salaris</th></tr>" +
            string.Concat(Enumerable.Range(0, 20).Select(i =>
                $"<tr><td rowspan=\"2\">Regel {i}</td><td>{2000 + i},00</td></tr>")) +
            "</table>";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 100);

        Assert.IsTrue(pieces.Count > 1);
        Assert.AreEqual(20, pieces.Sum(p => CountOf(p.Text, "rowspan=")));
        Assert.IsTrue(pieces.All(p => p.Text.Contains("colspan=\"2\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnHtmlTableWithNoHeaderRepeatsTheOpeningMarkupOnly_NeverADataRow()
    {
        // Repeating a data row as if it were a header would duplicate a record. A table whose
        // first row is data simply gets no repeated row.
        var pieces = TableCutter.Cut(Block(HtmlTable(30, caption: false, header: false), BlockKind.Table), 120);

        Assert.IsTrue(pieces.Count > 1);
        var dataRows = pieces.SelectMany(p => RowsOf(p.Text)).ToList();
        Assert.AreEqual(30, dataRows.Count);
    }

    [TestMethod]
    public void AGiantHtmlRowIsEmittedWholeAndFlagged()
    {
        // Cutting inside a row would corrupt the column alignment, and a corrupt row is worse
        // than an oversized chunk - the reader cannot tell which column a value belongs to.
        var giant = $"<tr><td>{Prose(400)}</td></tr>";
        var text  = "<table>" + HtmlHeader + giant + "<tr><td>kort</td></tr></table>";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 60);

        Assert.IsTrue(pieces.Any(p => p.Degraded));
        Assert.IsTrue(pieces.All(p => p.Text.EndsWith("</table>", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TextAfterTheClosingTagRidesTheLastFragmentOnly()
    {
        // CU writes table footnotes as text after the table; on a single-line table that text
        // sits inside this block, and it must be neither dropped nor duplicated.
        var text = HtmlTable(30) + " Bron: CAO GGZ bijlage 2.";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 120);

        Assert.AreEqual(1, pieces.Count(p => p.Text.Contains("Bron: CAO GGZ", StringComparison.Ordinal)));
        StringAssert.Contains(pieces[^1].Text, "Bron: CAO GGZ bijlage 2.");
    }

    [TestMethod]
    public void ATypedTableWithNoRowMarkup_IsKeptWholeAndFlagged()
    {
        // The service said table; the markup offers no row boundary to cut on. Whole and
        // degraded, not silently dropped and not cut mid-cell.
        var text = "<table>" + Prose(300) + "</table>";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 20);

        Assert.AreEqual(1, pieces.Count);
        Assert.IsTrue(pieces[0].Degraded);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static IEnumerable<string> RowsOf(string text) =>
        TableMarkup.Rows(text).Select(r => text[r.Start..r.End]);
}
