using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The shape route 2 exists for: a form, a cover sheet, a metadata table. A pair is atomic in
// the strongest sense in the cascade - a value separated from its label is not degraded, it is
// unretrievable. Nobody searches for "12-03-2024" alone.
[TestClass]
public class KeyValueCuttingTests
{
    private static string Pairs(int count) =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => "Veld " + i + ": waarde " + i));

    // ── the detector ─────────────────────────────────────────────────────────

    [TestMethod]
    public void BothWrittenFormsAreRecognised()
    {
        Assert.IsTrue(KeyValueDetector.IsPair("Vastgesteld: 12-03-2024"), "label and value on one line");
        Assert.IsTrue(KeyValueDetector.IsLabel("Vastgesteld:"), "label with its value on the next line");
        Assert.IsFalse(KeyValueDetector.IsPair("Vastgesteld:"), "a bare label is not a pair on its own");
    }

    [TestMethod]
    public void AnUnboundedLabelIsRejected_SoAProseSentenceWithAColonStaysProse()
    {
        // Without the 60-character bound, any sentence containing a colon looks like a pair,
        // and whole paragraphs would route to a cutter that refuses to cut.
        var longLabel = new string('a', 61) + ": waarde";

        Assert.IsFalse(KeyValueDetector.IsPair(longLabel));
        Assert.IsTrue(KeyValueDetector.IsPair(new string('a', 60) + ": waarde"), "exactly at the bound still counts");
    }

    [TestMethod]
    public void ALineContainingAPipe_IsNotAPair()
    {
        // Table rows reach the line classifier first, and a pipe inside the label is how a row
        // would otherwise be mistaken for one.
        Assert.IsFalse(KeyValueDetector.IsPair("| Functie | Schaal: 35 |"));
    }

    [TestMethod]
    public void FewerThanTwoPairs_IsProse()
    {
        // One "Let op: ..." line inside a paragraph is prose, and treating it as a key-value
        // block would pull it out of the paragraph it belongs to.
        Assert.IsFalse(KeyValueDetector.IsKeyValue(Block("Let op: dit geldt niet voor iedereen.", BlockKind.KeyValue)));
        Assert.IsTrue(KeyValueDetector.IsKeyValue(Block(Pairs(2), BlockKind.KeyValue)));
    }

    [TestMethod]
    public void ABlockThatIsOnlyPartKeyValue_IsProseWithPairsInIt()
    {
        var mixed = "Vastgesteld: 12-03-2024\nEen gewone zin zonder enige structuur.\nVersie: 2.1";

        Assert.IsFalse(KeyValueDetector.IsKeyValue(Block(mixed, BlockKind.KeyValue)));
    }

    [TestMethod]
    public void ABareLabelConsumesTheFollowingLineAsItsValue()
    {
        // Which is why that line is not required to look like anything in particular.
        var adjacent = "Vastgesteld:\n12-03-2024\nDocumentnummer:\n4.2.1";

        Assert.IsTrue(KeyValueDetector.IsKeyValue(Block(adjacent, BlockKind.KeyValue)));
    }

    [TestMethod]
    public void TwoBareLabelsInARow_DoNotCount_BecauseTheSecondCannotBeAValue()
    {
        Assert.IsFalse(KeyValueDetector.IsKeyValue(Block("Vastgesteld:\nDocumentnummer:", BlockKind.KeyValue)));
    }

    // ── the cutter ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ARunUnderTheCeiling_StaysOnePiece()
    {
        var text = Pairs(4);

        var pieces = KeyValueCutter.Cut(Block(text, BlockKind.KeyValue), Tokens(text));

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
    }

    [TestMethod]
    public void ALongFormIsCutBetweenPairs_RatherThanEmittedAsOneOversizedChunk()
    {
        // The draft's blocks were single pairs, so it never had to answer this. A 60-pair form
        // emitted whole would put a document behind a single vector.
        var text  = Pairs(60);
        var block = Block(text, BlockKind.KeyValue);

        var pieces = KeyValueCutter.Cut(block, 40);

        Assert.IsTrue(pieces.Count > 1);
        AssertSliceInvariant(text, pieces);
        AssertAscendingAndDisjoint(pieces);

        // No piece starts or ends inside a pair: every line of every piece is a whole pair.
        foreach (var line in pieces.SelectMany(p => p.Text.Split('\n')))
            Assert.IsTrue(KeyValueDetector.IsPair(line), "cut inside a pair: " + line);
    }

    [TestMethod]
    public void ACutNeverFallsBetweenABareLabelAndItsValue()
    {
        // The adjacent-line form is why the boundaries are computed here rather than taken from
        // LineSpans: after "Label:", the following line is its value and is not a boundary.
        var text = string.Join("\n",
            Enumerable.Range(0, 40).Select(i => "Veld " + i + ":\nwaarde " + i));

        var pieces = KeyValueCutter.Cut(Block(text, BlockKind.KeyValue), 30);

        Assert.IsTrue(pieces.Count > 1);
        foreach (var piece in pieces)
        {
            var lines = piece.Text.Split('\n');
            Assert.IsTrue(KeyValueDetector.IsLabel(lines[0]), "a piece must open on a label, not on an orphaned value");
            Assert.IsFalse(KeyValueDetector.IsLabel(lines[^1]), "a piece must not end on a label whose value it left behind");
        }
    }

    [TestMethod]
    public void APairBiggerThanTheWholeCeiling_IsKeptWholeAndFlagged()
    {
        // Splitting it is the one thing this cutter exists to refuse, so it is counted rather
        // than hidden.
        var text = "Toelichting: " + Prose(300) + "\nVersie: 2.1\nStatus: definitief";

        var pieces = KeyValueCutter.Cut(Block(text, BlockKind.KeyValue), 40);

        Assert.IsTrue(pieces.Any(p => p.Degraded && Tokens(p.Text) > 40));
    }

    [TestMethod]
    public void CoordinatesSurvive_WhenTheRunSitsDeepInTheDocument()
    {
        var lead    = "Inleidende alinea die niet meedoet.\n\n";
        var content = lead + Pairs(40);
        var block   = BlockIn(content, lead.Length, content.Length, BlockKind.KeyValue);

        var pieces = KeyValueCutter.Cut(block, 40);

        Assert.IsTrue(pieces.All(p => p.Start >= lead.Length));
        AssertSliceInvariant(content, pieces);
    }
}
