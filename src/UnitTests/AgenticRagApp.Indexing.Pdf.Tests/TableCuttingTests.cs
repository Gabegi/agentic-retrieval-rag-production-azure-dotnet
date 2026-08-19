using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The detector and the cutter together, because they are one decision: what counts as a table
// has to mean the same thing at both ends of the pipeline, which is why both levels of the test
// live in one class in production too.
[TestClass]
public class TableCuttingTests
{
    private const string Header    = "| Functie | Schaal | Bedrag |";
    private const string Separator = "| --- | --- | --- |";

    private static string Table(int rows, bool separator = true)
    {
        var lines = new List<string> { Header };
        if (separator) lines.Add(Separator);

        lines.AddRange(Enumerable.Range(0, rows)
            .Select(i => "| Verpleegkundige niveau " + i + " | FWG " + (35 + i) + " | " + (2000 + i) + ",00 |"));

        return string.Join("\n", lines);
    }

    // ── the detector ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ARowIsPipeAnythingPipe_WhichIsHowDocumentIntelligenceEmitsTables()
    {
        Assert.IsTrue(TableDetector.IsRow("| a | b |"));
        Assert.IsTrue(TableDetector.IsRow("   | a | b |   "));
        Assert.IsFalse(TableDetector.IsRow("gewone tekst"));
        Assert.IsFalse(TableDetector.IsRow("| alleen links"));
    }

    [TestMethod]
    public void ASeparatorRowIsOnlyDashesColonsAndSpace()
    {
        Assert.IsTrue(TableDetector.IsSeparator(Separator));
        Assert.IsTrue(TableDetector.IsSeparator("|---|:---:|---:|"));
        Assert.IsFalse(TableDetector.IsSeparator(Header));
        Assert.IsFalse(TableDetector.IsSeparator("| - | tekst |"));
    }

    [TestMethod]
    public void TwoRowsAreATable_OneIsASentenceWithPipesInIt()
    {
        // The same rule, and the same reason, as the list-run test.
        Assert.IsTrue(TableDetector.IsTable(Block("| a | b |\n| 1 | 2 |", BlockKind.Table)));
        Assert.IsFalse(TableDetector.IsTable(Block("| a | b |", BlockKind.Table)));
        Assert.IsFalse(TableDetector.IsTable(Block("| a | b |\ngewone tekst", BlockKind.Table)));
    }

    [TestMethod]
    public void ASeparatorIsNotRequired_BecauseRealTablesArriveAsHeaderPlusDataOnly()
    {
        Assert.IsTrue(TableDetector.IsTable(Block(Table(3, separator: false), BlockKind.Table)));
    }

    [TestMethod]
    public void ATrailingBlankLine_DoesNotDisqualifyATable()
    {
        // NonBlankLines drops it rather than counting it, which is why an all-rows table that
        // happens to end on a blank line still passes its own "every line is a row" test.
        Assert.IsTrue(TableDetector.IsTable(Block("| a | b |\n| 1 | 2 |\n", BlockKind.Table)));
    }

    [TestMethod]
    public void HeaderIsTwoLinesWithASeparator_AndOneWithout()
    {
        Assert.AreEqual(2, TableDetector.HeaderLineCount(Block(Table(2), BlockKind.Table)));
        Assert.AreEqual(1, TableDetector.HeaderLineCount(Block(Table(2, separator: false), BlockKind.Table)));
    }

    // ── the cutter ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ATableUnderTheCeiling_StaysOnePiece()
    {
        var text  = Table(3);
        var block = Block(text, BlockKind.Table);

        var pieces = TableCutter.Cut(block, Tokens(text));

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
        Assert.IsFalse(pieces[0].Degraded);
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void EveryContinuationFragmentRepeatsTheHeaderAndTheSeparator()
    {
        // A header-less run of numbers means nothing to the embedder or to the model reading
        // the retrieved chunk - "1.847" is not an answer to anything.
        var text  = Table(20);
        var block = Block(text, BlockKind.Table);

        var pieces = TableCutter.Cut(block, 60);

        Assert.IsTrue(pieces.Count > 1);
        foreach (var piece in pieces)
        {
            StringAssert.Contains(piece.Text, Header);
            StringAssert.Contains(piece.Text, Separator);
        }
    }

    [TestMethod]
    public void TheFirstFragmentIsAPureSlice_AndTheRestAreComposed()
    {
        // The first already begins at the header, so it needs nothing prepended. Only the later
        // ones are composed, and they are the documented exception to the slice invariant.
        var text  = Table(20);
        var block = Block(text, BlockKind.Table);

        var pieces = TableCutter.Cut(block, 60);

        Assert.AreEqual(pieces[0].Length, pieces[0].Text.Length, "the first fragment is a pure slice");
        Assert.IsTrue(pieces.Skip(1).All(p => p.Text.Length != p.Length), "continuation fragments are composed");
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void AComposedFragmentsCoordinatesAddressItsOwnRows_NotTheRepeatedHeader()
    {
        // What keeps page attribution landing on the right pages: the fragment's Start is where
        // its DATA is, not where the header it borrowed sits.
        var text  = Table(20);
        var block = Block(text, BlockKind.Table);

        var pieces = TableCutter.Cut(block, 60);
        var second = pieces[1];

        var addressed = text.Substring(second.Start, second.Length);

        Assert.IsFalse(addressed.Contains(Header), "the header is repeated in Text, not addressed by Start/Length");
        StringAssert.Contains(second.Text, addressed);
    }

    [TestMethod]
    public void DataRowsAreNeverRepeatedAcrossFragments()
    {
        // Never overlapped: repeating DATA rows duplicates records, which is not the same thing
        // as restoring context.
        var text  = Table(20);
        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 60);

        var dataRows = pieces
            .SelectMany(p => p.Text.Split('\n'))
            .Where(line => line != Header && line != Separator)
            .ToList();

        Assert.AreEqual(dataRows.Count, dataRows.Distinct().Count());
        Assert.AreEqual(20, dataRows.Count, "every row survives the cut exactly once");
    }

    [TestMethod]
    public void ARowThatAloneBreachesTheCeiling_IsEmittedWholeAndFlagged()
    {
        // Cutting inside a row would corrupt the column alignment, and a corrupt row is worse
        // than an oversized chunk - the reader cannot tell which column a value belongs to.
        var giantRow = "| " + Prose(300) + " | x | y |";
        var text     = Header + "\n" + Separator + "\n" + giantRow + "\n| a | b | c |";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 60);

        Assert.IsTrue(pieces.Any(p => p.Degraded));
        Assert.IsTrue(pieces.Any(p => Tokens(p.Text) > 60));
    }

    [TestMethod]
    public void EveryPieceCarriesTheTableRowBoundary_WhenACutWasMade()
    {
        var pieces = TableCutter.Cut(Block(Table(20), BlockKind.Table), 60);

        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.TableRow));
    }

    [TestMethod]
    public void AHeaderWithNoDataRows_IsKeptWholeRatherThanLost()
    {
        // The guard at the end of the cutter: an oversized header on its own must still come
        // back as something.
        var text = "| " + Prose(300) + " |\n| --- |";

        var pieces = TableCutter.Cut(Block(text, BlockKind.Table), 20);

        Assert.AreEqual(1, pieces.Count);
        Assert.IsTrue(pieces[0].Degraded);
    }
}
