using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The single cutter BOTH routes go through: route 1 hands it one oversized section, route 2
// hands it the whole document. That shared-ness is the point of the class (chunking-done.md
// §2) - an oversized section and an unstructured document cannot drift apart about what a
// table is - and the WINDOW is the hazard it introduced.
//
// The coordinate contract is what most of this file is about. BlockCascade parses a slice and
// shifts every block back into source coordinates immediately, so pieces address content, not
// the window. chunking-done.md §5.1 lists asserting that as the first thing left to do,
// "currently reasoned, not executed" - these run it.
[TestClass]
public class BlockCascadeTests
{
    private const string Table =
        "| Functie | Schaal |\n| --- | --- |\n| Verpleegkundige | FWG 35 |\n| Begeleider | FWG 40 |";

    private static IReadOnlyList<ContentPiece> CutAll(string content, int ceiling) =>
        BlockCascade.Cut(content, 0, content.Length, ceiling);

    // ── dispatch ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ATableIsCutOnRows_AListOnItems_AKeyValueRunOnPairs()
    {
        // Classification decides how a block may be cut, and the level it comes back with is
        // how the run report can tell which decision was taken.
        var table = CutAll(Table + "\n" + string.Join("\n",
            Enumerable.Range(0, 40).Select(i => "| Rol " + i + " | FWG " + i + " |")), 60);

        var list = CutAll(string.Join("\n",
            Enumerable.Range(0, 40).Select(i => "- " + Prose(6, "stap" + i))), 60);

        var pairs = CutAll(string.Join("\n",
            Enumerable.Range(0, 60).Select(i => "Veld " + i + ": waarde " + i)), 40);

        Assert.IsTrue(table.All(p => p.BoundaryLevel == BoundaryLevel.TableRow));
        Assert.IsTrue(list.All(p => p.BoundaryLevel == BoundaryLevel.ListItem));
        Assert.IsTrue(pairs.All(p => p.BoundaryLevel == BoundaryLevel.ListItem),
            "the key-value cutter shares the pair/item boundary level");
    }

    [TestMethod]
    public void ProseFallsThroughToTheLengthLadder()
    {
        // Prose is the LAST answer, not the first: a mid-paragraph cut merely interrupts what a
        // mid-row cut destroys.
        var pieces = CutAll(Sentences(60), 60);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel is
            BoundaryLevel.None or BoundaryLevel.Line or BoundaryLevel.Sentence
            or BoundaryLevel.Word or BoundaryLevel.HardCut));
    }

    [TestMethod]
    public void EverythingUnderTheCeiling_ComesBackWhole()
    {
        // The common case the packer works to produce - not a special shape, just no cut.
        const string content = "Een korte alinea.\n\nEn nog een korte alinea.";

        var pieces = CutAll(content, 4096);

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
        AssertSliceInvariant(content, pieces);
    }

    // ── prose packing and the atomic separator ───────────────────────────────

    [TestMethod]
    public void AnAtomicBlockClosesTheProseRunAroundIt()
    {
        // A table between two paragraphs is a real separation - the text either side of it is
        // not adjacent. Merging across it would put the before and after of a table in one
        // chunk with the table itself in another.
        var content = "Alinea voor de tabel.\n\n" + Table + "\n\nAlinea na de tabel.";

        var pieces = CutAll(content, 4096);

        Assert.AreEqual(3, pieces.Count);
        StringAssert.Contains(pieces[0].Text, "voor de tabel");
        StringAssert.Contains(pieces[1].Text, "Functie");
        StringAssert.Contains(pieces[2].Text, "na de tabel");
        AssertSliceInvariant(content, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void ATrailingProseRunIsStillFlushed_WithNoAtomicBlockAfterItToCloseIt()
    {
        // The flush after the loop. Without it, the tail of every document that does not end
        // in a table would be silently dropped.
        var content = Table + "\n\nSlotalinea die nergens door wordt afgesloten.";

        var pieces = CutAll(content, 4096);

        Assert.AreEqual(2, pieces.Count);
        StringAssert.Contains(pieces[^1].Text, "Slotalinea");
    }

    [TestMethod]
    public void ShortConsecutiveParagraphs_AreMergedRatherThanEmittedOneByOne()
    {
        // A paragraph is not a chunk on its own, it is one unit the packer may merge. Twelve
        // 40-token paragraphs emitted separately embed badly and fill top-k with fragments of
        // a single document.
        var paragraph = Prose(10);
        var content   = string.Join("\n\n", Enumerable.Repeat(paragraph, 12));

        var pieces = CutAll(content, 4096);

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(content, pieces[0].Text);
    }

    // ── the coordinate contract ──────────────────────────────────────────────

    [TestMethod]
    public void AWindowedCut_ReturnsPiecesInSourceCoordinates()
    {
        // chunking-done.md §5.1, executed. Route 1 narrows the window per section, and this is
        // the exact thing that breaks if BlockParser's output is not shifted by start: the
        // pieces would address the window and every offset below would be wrong by the length
        // of everything before the section.
        var lead    = "Voorwoord dat buiten het venster valt.\n\n";
        var section = Sentences(40);
        var content = lead + section;

        var pieces = BlockCascade.Cut(content, lead.Length, content.Length, 60);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.Start >= lead.Length), "a piece escaped the window");
        AssertSliceInvariant(content, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void AWindowedCut_SeesNothingOutsideItsOwnRange()
    {
        var lead    = "Voorwoord met het woord kenmerk erin.\n\n";
        var section = "Sectietekst zonder dat woord.";
        var content = lead + section;

        var pieces = BlockCascade.Cut(content, lead.Length, content.Length, 4096);

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(section, pieces[0].Text);
        Assert.IsFalse(pieces[0].Text.Contains("kenmerk"));
    }

    [TestMethod]
    public void TheFullWindowIsTheSameCutAsNoWindowAtAll()
    {
        // chunking-done.md §2, "route 2 is unchanged, deliberately". The extraction was a move,
        // not a rewrite, so route 2's measured numbers must stay comparable across it: a
        // full-length slice returns the same string instance and the coordinate shift adds 0.
        var content = "Inleiding.\n\n" + Table + "\n\n" + Sentences(40);

        var whole  = BlockCascade.Cut(content, 0, content.Length, 60);
        var padded = BlockCascade.Cut(content, -20, content.Length + 20, 60);

        CollectionAssert.AreEqual(
            whole.Select(p => (p.Text, p.Start, p.Length, p.BoundaryLevel, p.Degraded)).ToArray(),
            padded.Select(p => (p.Text, p.Start, p.Length, p.BoundaryLevel, p.Degraded)).ToArray());
    }

    [TestMethod]
    public void SectionsCutOneByOne_CoverTheSameTextAsTheWholeDocumentCutAtOnce()
    {
        // The property route 1 depends on when it windows per section: narrowing changes WHERE
        // the cuts fall, but never which characters are covered.
        var first   = Sentences(20);
        var second  = Sentences(20);
        var content = first + "\n\n" + second;

        var perSection = BlockCascade.Cut(content, 0, first.Length, 60)
            .Concat(BlockCascade.Cut(content, first.Length, content.Length, 60))
            .ToList();

        AssertSliceInvariant(content, perSection);
        AssertAscendingAndDisjoint(perSection);
        Assert.IsTrue(perSection.Count > 2);
    }

    // ── degenerate ranges ────────────────────────────────────────────────────

    [TestMethod]
    public void AnEmptyRange_ProducesNothing()
    {
        Assert.AreEqual(0, BlockCascade.Cut("wat tekst hier", 5, 5, 512).Count);
    }

    [TestMethod]
    public void EmptyContent_ProducesNothing()
    {
        Assert.AreEqual(0, BlockCascade.Cut("", 0, 0, 512).Count);
    }

    [TestMethod]
    public void OutOfRangeAndReversedBounds_ClampRatherThanThrow()
    {
        const string content = "Een enkele alinea.";

        Assert.AreEqual(1, BlockCascade.Cut(content, -50, 5_000, 512).Count);
        Assert.AreEqual(0, BlockCascade.Cut(content, 10, 2, 512).Count, "end clamps up to start, so the range is empty");
    }

    [TestMethod]
    public void WhitespaceOnlyContent_ProducesNoPieces()
    {
        // Trimmed away when pieces are built, not at parse time - and no piece is better than
        // an empty one, which would occupy an index row saying nothing.
        Assert.AreEqual(0, CutAll("   \n\n  \n", 512).Count);
    }

    // ── the whole cascade on a mixed document ────────────────────────────────

    [TestMethod]
    public void AMixedDocument_KeepsEveryKindIntactAndInOrder()
    {
        var content =
            "Inleidende alinea over de regeling.\n\n" +
            Table + "\n\n" +
            "- eerste stap\n- tweede stap\n- derde stap\n\n" +
            "Vastgesteld: 12-03-2024\nDocumentnummer: 4.2.1\n\n" +
            "Slotalinea met de toelichting.";

        var pieces = CutAll(content, 4096);

        AssertSliceInvariant(content, pieces);
        AssertAscendingAndDisjoint(pieces);
        Assert.AreEqual(5, pieces.Count, "each kind is its own piece; nothing merges across an atomic block");
    }

    [TestMethod]
    public void EveryPieceFits_OrSaysWhyItDoesNot()
    {
        // The cascade's contract in one line: a piece over the ceiling exists only where the
        // alternative was worse, and it is flagged rather than silently emitted.
        var content =
            Table + "\n\n" +
            "- " + Prose(300) + "\n- korte stap\n\n" +
            Sentences(60) + "\n\n" +
            new string('x', 3000);

        var pieces = CutAll(content, 60);

        foreach (var piece in pieces)
            Assert.IsTrue(Tokens(piece.Text) <= 60 || piece.Degraded,
                "an oversized piece must be flagged: " + piece.BoundaryLevel);

        AssertSliceInvariant(content, pieces);
    }
}
