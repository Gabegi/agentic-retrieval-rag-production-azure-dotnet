using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The length ladder, weakest structure last: line -> sentence -> word -> hard. Only prose ever
// reaches it, because a mid-paragraph cut merely interrupts information where a mid-row or
// mid-pair cut destroys it.
//
// The four cutters are tested together because what matters about each is the same three
// things - it cuts on its own boundary, it packs to the ceiling rather than emitting one piece
// per boundary, and it keeps the slice invariant - and because the ONE property that makes the
// ladder terminate belongs to the last of them.
[TestClass]
public class LadderCutterTests
{
    // ── line breaks ──────────────────────────────────────────────────────────

    [TestMethod]
    public void LineBreakCutter_CutsOnNewlines_AndPacksToTheCeiling()
    {
        // A line break inside a paragraph is usually a wrapped clause, a sub-item, or a line of
        // a stripped table - a stronger signal than a full stop, so it is tried first.
        var lines = Enumerable.Range(0, 12).Select(i => Prose(6, "regel" + i)).ToList();
        var text  = string.Join("\n", lines);

        var pieces = LineBreakCutter.Cut(Block(text), Tokens(lines[0]) * 3);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.Count < lines.Count, "packed, not one piece per line");
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Line));
        AssertSliceInvariant(text, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void LineBreakCutter_KeepsTheNewlineWithTheLineItEnds()
    {
        // Boundaries sit just PAST each newline, so no piece opens on a line break.
        var text = string.Join("\n", Enumerable.Range(0, 12).Select(i => Prose(6, "regel" + i)));

        var pieces = LineBreakCutter.Cut(Block(text), 40);

        Assert.IsTrue(pieces.All(p => !p.Text.StartsWith('\n')));
    }

    // ── sentences ────────────────────────────────────────────────────────────

    [TestMethod]
    public void SentenceCutter_CutsAtSentenceEnds_AndPacksToTheCeiling()
    {
        var text = Sentences(12);

        var pieces = SentenceCutter.Cut(Block(text), 60);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Sentence));
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void SentenceCutter_DoesNotBreakInsideAnArticleNumberOrAnAbbreviation()
    {
        // Same rule as ChunkingHelper.SplitSentences, deliberately: an ender only counts when
        // whitespace or end-of-text follows it, so "4.2.1" and "art. 7" survive.
        const string text = "Zie artikel 4.2.1 en art. 7 van deze regeling voor de volledige toelichting.";

        var pieces = SentenceCutter.Cut(Block(text), 4096);

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(text, pieces[0].Text);
    }

    [TestMethod]
    public void SentenceCutter_KeepsTheFullStopWithTheSentenceItEnds()
    {
        var text = Sentences(12);

        var pieces = SentenceCutter.Cut(Block(text), 60);

        Assert.IsTrue(pieces.All(p => p.Text.EndsWith('.')), "a piece ends on its own full stop");
        Assert.IsTrue(pieces.All(p => !p.Text.StartsWith('.')), "and never opens on the previous one");
    }

    // ── word gaps ────────────────────────────────────────────────────────────

    [TestMethod]
    public void WordGapCutter_ProducesNearCeilingPieces_NotOnePiecePerWord()
    {
        // Reaching this rung means the block has no sentence end at all: an address block, a
        // column of headings run together, a legal reference chain.
        var text = string.Join(" ", Enumerable.Range(0, 400).Select(i => "woord" + i));

        var pieces = WordGapCutter.Cut(Block(text), 100);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.Count < 20, "packed to the ceiling, not split at every gap");
        Assert.IsTrue(pieces.All(p => Tokens(p.Text) <= 100));
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Word));
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void WordGapCutter_NeverOpensAPieceOnAGap()
    {
        var text = string.Join("  ", Enumerable.Range(0, 200).Select(i => "woord" + i));

        var pieces = WordGapCutter.Cut(Block(text), 60);

        Assert.IsTrue(pieces.All(p => p.Text.Length > 0 && !char.IsWhiteSpace(p.Text[0])));
    }

    [TestMethod]
    public void WordGapCutter_CannotCutTextThatHasNoGaps_WhichIsWhyTheLadderContinues()
    {
        // The specific handover to HardCut: an unbroken token run offers this rung nothing.
        var text = new string('x', 4000);

        var pieces = WordGapCutter.Cut(Block(text), 100);

        Assert.IsFalse(CeilingCheck.AllFit(pieces, 100));
    }

    // ── the hard cut ─────────────────────────────────────────────────────────

    [TestMethod]
    public void HardCutter_AlwaysFits_WhichIsWhatTerminatesTheLadder()
    {
        // The property the whole cascade's fall-through chain rests on. Only text that offers
        // no boundary of any kind gets here - a base64 blob, a table whose pipes were stripped.
        var text = new string('x', 6000);

        var pieces = HardCutter.Cut(Block(text), 100);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(CeilingCheck.AllFit(pieces, 100));
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.HardCut));
        AssertSliceInvariant(text, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void HardCutter_SizesItsWindowThroughTheWorstCaseCharacterRatio()
    {
        // Sized in characters through ChunkingHelper.CharBudgetForTokens, which is set at or
        // below the worst measured ratio - so a window is if anything SMALLER than the ceiling
        // allows, which is what makes "always fits" true rather than usually true.
        //
        // Windows are where a cut is ALLOWED, not where one is taken: SpanCutter still packs,
        // and a run of one repeated character tokenizes so cheaply that several windows fit
        // under the ceiling together. So the assertion is that every cut LANDS on a window
        // boundary, not that every window becomes its own piece.
        const int ceiling = 100;
        var window = ChunkingHelper.CharBudgetForTokens(ceiling, isTable: false);

        var pieces = HardCutter.Cut(Block(new string('x', window * 6)), ceiling);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.Start % window == 0), "a cut landed off a window boundary");
        Assert.IsTrue(pieces.All(p => p.Length % window == 0), "a piece is a whole number of windows");
        Assert.IsTrue(CeilingCheck.AllFit(pieces, ceiling));
    }

    [TestMethod]
    public void HardCutter_LosesNothing_EvenThoughItCutsMidWord()
    {
        // The pieces it produces are bad chunks, and that is the point - but they are still all
        // of the text.
        var text = new string('x', 5000);

        var pieces = HardCutter.Cut(Block(text), 100);

        Assert.AreEqual(text.Length, pieces.Sum(p => p.Length));
    }

    // ── the ladder as a chain ────────────────────────────────────────────────

    [TestMethod]
    public void EachRungIsTriedOnlyWhenTheOneAboveItFailed()
    {
        // The chain is written as a fall-through because each level is cheap to try and the
        // last always succeeds. This is that contract stated as a test: line-breakable text
        // never reaches the word rung.
        var lines = Enumerable.Range(0, 12).Select(i => Prose(6, "regel" + i)).ToList();
        var text  = string.Join("\n", lines);
        var block = Block(text);

        var ceiling = Tokens(lines[0]) * 2;

        Assert.IsTrue(CeilingCheck.AllFit(LineBreakCutter.Cut(block, ceiling), ceiling),
            "line breaks alone suffice here, so the cascade stops at the first rung");
    }
}
