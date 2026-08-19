using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The only place a ContentPiece is constructed, so it is the only place the slice invariant
// can be got wrong. The helpers this replaced returned trimmed strings, which is precisely
// where position was lost - a trimmed substring no longer starts where its Start says it does,
// and nothing downstream can tell.
[TestClass]
public class PieceFactoryTests
{
    [TestMethod]
    public void TrimmingMovesTheBounds_RatherThanTrimmingTheString()
    {
        // The distinction the whole class exists for. Text and Start must stay consistent: if
        // the leading spaces were dropped by Trim() the text would be right and the offset
        // three characters early, and page attribution would follow the offset.
        const string content = "   kop van de sectie   ";
        var block = Block(content);

        var piece = PieceFactory.Piece(block, 0, content.Length, BoundaryLevel.Line);

        Assert.AreEqual("kop van de sectie", piece.Text);
        Assert.AreEqual(3, piece.Start);
        Assert.AreEqual(17, piece.Length);
        AssertSliceInvariant(content, [piece]);
    }

    [TestMethod]
    public void CoordinatesAreAbsolute_SoABlockDeepInTheDocumentStillAddressesTheSource()
    {
        // block.Start is what makes a cutter's local index pair meaningful. Route 1 windows
        // per section, so a block whose Start is not 0 is the normal case, not an edge one.
        const string content = "voorwoord\n\nArtikel 1\n\nDe werkgever betaalt.";
        var block = BlockIn(content, 22, content.Length);

        var piece = PieceFactory.Whole(block, BoundaryLevel.None);

        Assert.AreEqual("De werkgever betaalt.", piece.Text);
        Assert.AreEqual(22, piece.Start);
        AssertSliceInvariant(content, [piece]);
    }

    [TestMethod]
    public void AnAllWhitespaceBlock_TrimsAwayToNothing()
    {
        // SpanCutter drops zero-length pieces on the strength of this, which is how a run of
        // blank lines disappears without a special case anywhere else.
        var piece = PieceFactory.Whole(Block("   \n  \n "), BoundaryLevel.Line);

        Assert.AreEqual(0, piece.Length);
        Assert.AreEqual("", piece.Text);
    }

    [TestMethod]
    public void OutOfRangeBoundsClamp_RatherThanThrow()
    {
        // Boundary generators are allowed to be sloppy - a duplicate or an overshoot is a
        // nuisance, not a corrupt cut - so the factory absorbs it here rather than every
        // cutter having to guard.
        const string content = "korte regel";
        var block = Block(content);

        var piece = PieceFactory.Piece(block, -5, content.Length + 40, BoundaryLevel.Word);

        Assert.AreEqual(content, piece.Text);
        AssertSliceInvariant(content, [piece]);
    }

    [TestMethod]
    public void ReversedBounds_AreRefusedByPiece_AndClampedByComposed()
    {
        // The asymmetry, now deliberate rather than incidental. It used to be that Piece clamped
        // each bound independently and then sliced, so start > end reached the range operator and
        // threw ArgumentOutOfRangeException - the right outcome for the wrong reason, and one
        // sentence of "cleanup" away from becoming a silent empty piece.
        //
        // Piece now refuses explicitly, because a descending segment is a cutter bug: absorbing
        // it would drop a cut and leave nothing saying which cutter walked backwards. Composed
        // keeps its clamp, because its bounds address the source slice behind a composed string,
        // where a degenerate range means "carries no source characters".
        //
        // Still not reachable from any caller - SpanCutter only emits ascending segments and
        // TableCutter's first-fragment path starts at the header - so this pins the contract, not
        // a live path.
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => PieceFactory.Piece(Block("abcdef"), 5, 2, BoundaryLevel.Word));
        StringAssert.Contains(ex.Message, "reversed");

        var composed = PieceFactory.Composed(Block("abcdef"), "abc", 5, 2, BoundaryLevel.TableRow);
        Assert.AreEqual(0, composed.Length);
    }

    [TestMethod]
    public void ComposedIsTheOneException_TextAndLengthDisagreeByDesign()
    {
        // A table continuation fragment repeats the header so a run of numbers still means
        // something to the embedder. Start and Length keep addressing the ROWS the fragment
        // carries - the true position of the data, not of the repeated header - which is what
        // keeps page attribution landing on the right pages.
        const string content = "| kop | waarde |\n| --- | --- |\n| a | 1 |\n| b | 2 |";
        var block = Block(content, BlockKind.Table);

        var rows = content.IndexOf("| b | 2 |", StringComparison.Ordinal);

        var piece = PieceFactory.Composed(
            block, "| kop | waarde |\n| --- | --- |\n| b | 2 |", rows, content.Length, BoundaryLevel.TableRow);

        Assert.AreNotEqual(piece.Text.Length, piece.Length);
        Assert.AreEqual(rows, piece.Start);
        Assert.AreEqual("| b | 2 |", content.Substring(piece.Start, piece.Length));
        AssertSliceInvariant(content, [piece]);
    }

    [TestMethod]
    public void FlagsAreCarriedOntoThePiece_NotDefaulted()
    {
        // Degraded is the one flag that says an over-ceiling piece was deliberate, so it can
        // never be re-derived and has to survive construction.
        var piece = PieceFactory.Whole(Block("| a | 1 |"), BoundaryLevel.TableRow, degraded: true);

        Assert.IsTrue(piece.Degraded);
        Assert.AreEqual(BoundaryLevel.TableRow, piece.BoundaryLevel);
        Assert.IsFalse(piece.IsOverlap);
    }
}
