using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The shared loop behind every ladder cutter. Each cutter's own content is only WHERE its
// boundaries are; everything else - packing, trimming, dropping empties - happens here, which
// is what keeps the offset discipline in one place instead of four.
[TestClass]
public class SpanCutterTests
{
    [TestMethod]
    public void SegmentsArePackedToTheCeiling_NotEmittedOnePerBoundary()
    {
        // The literal reading of "cut at every boundary of this level" gives one piece per line
        // at the line level and one piece per WORD at the word level, which is not a chunk, it
        // is a token. Boundaries say where a cut is ALLOWED; the packing decides where it goes.
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => Prose(4, "regel" + i)));
        var block = Block(text);

        var boundaries = LineSpans.Read(text).Skip(1).Select(s => s.Start).ToList();

        var pieces = SpanCutter.Between(block, boundaries, BoundaryLevel.Line, Tokens(text));

        Assert.AreEqual(1, pieces.Count, "everything fits, so ten boundaries still produce one piece");
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void APieceClosesAtTheLastBoundaryBeforeTheCeiling()
    {
        var lines = Enumerable.Range(0, 6).Select(i => Prose(5, "regel" + i)).ToList();
        var text  = string.Join("\n", lines);
        var block = Block(text);

        // Room for roughly two lines per piece.
        var ceiling    = Tokens(lines[0]) * 2 + 1;
        var boundaries = LineSpans.Read(text).Skip(1).Select(s => s.Start);

        var pieces = SpanCutter.Between(block, boundaries, BoundaryLevel.Line, ceiling);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => Tokens(p.Text) <= ceiling));
        AssertSliceInvariant(text, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void TheTextAfterTheLastBoundary_IsASegmentToo()
    {
        // A block that does not end on a boundary is the normal case, not an edge case - drop
        // it and the tail of every document disappears.
        const string text = "eerste regel\ntweede regel\nstaart zonder grens";
        var block = Block(text);

        var pieces = SpanCutter.Between(block, [13], BoundaryLevel.Line, 4096);

        Assert.AreEqual(1, pieces.Count);
        StringAssert.EndsWith(pieces[0].Text, "staart zonder grens");
    }

    [TestMethod]
    public void ASegmentThatAloneExceedsTheCeiling_ComesBackOversized()
    {
        // This is the cascade's stop condition, and nothing else has to detect it: the
        // oversized piece makes CeilingCheck.AllFit report false and the caller descends a rung.
        var big  = Prose(300);
        var text = big + "\nkort";

        var pieces = SpanCutter.Between(Block(text), [big.Length + 1], BoundaryLevel.Line, 50);

        Assert.IsTrue(pieces.Any(p => Tokens(p.Text) > 50));
        Assert.IsFalse(CeilingCheck.AllFit(pieces, 50));
    }

    [TestMethod]
    public void DuplicateDescendingAndOutOfRangeBoundaries_AreSkippedRatherThanThrown()
    {
        // A boundary generator that emits a duplicate is a harmless nuisance, not a corrupt
        // cut, so it is absorbed here instead of crashing a document.
        const string text = "een twee drie vier vijf zes";
        var block = Block(text);

        var pieces = SpanCutter.Between(
            block, [9, 9, 4, -3, 9, 900], BoundaryLevel.Word, 4096);

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(text, pieces[0].Text);
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void WhitespaceOnlyPiecesAreDropped_SoAnAllBlankBlockYieldsNone()
    {
        // And AllFit deliberately calls that "does not fit", so the caller falls through
        // instead of silently losing the text.
        var pieces = SpanCutter.Between(Block("   \n\n   \n "), [4, 8], BoundaryLevel.Line, 4096);

        Assert.AreEqual(0, pieces.Count);
        Assert.IsFalse(CeilingCheck.AllFit(pieces, 4096));
    }

    [TestMethod]
    public void EveryPieceCarriesTheLevelItWasCutOn()
    {
        // BoundaryLevel is the fall-through metric the run report counts, so it has to be the
        // rung that actually made the cut rather than a default.
        var text  = Sentences(8);
        var block = Block(text);

        var pieces = SpanCutter.Between(
            block, LineSpans.Read(text).Skip(1).Select(s => s.Start), BoundaryLevel.Sentence, 20);

        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Sentence));
    }

    [TestMethod]
    public void CoordinatesStayAbsolute_WhenTheBlockIsAWindowOntoALargerDocument()
    {
        // Route 1 hands the cascade one section at a time. If the block's own Start were not
        // added back, every piece from every section but the first would address the wrong text.
        var lead    = "voorwoord dat niet meedoet\n\n";
        var section = Sentences(6);
        var content = lead + section;

        var block  = BlockIn(content, lead.Length, content.Length);
        var pieces = SpanCutter.Between(
            block, LineSpans.Read(block.Text).Skip(1).Select(s => s.Start), BoundaryLevel.Line, 30);

        Assert.IsTrue(pieces.All(p => p.Start >= lead.Length));
        AssertSliceInvariant(content, pieces);
    }
}
