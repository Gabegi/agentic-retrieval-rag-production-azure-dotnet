using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// A list run is a sequence of short peer items, which is what makes a mid-item cut so much
// worse than a mid-paragraph one: a reader cannot tell a truncated instruction from a
// complete one.
[TestClass]
public class ListRunCuttingTests
{
    private static string Items(int count, string bullet = "-") =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => bullet + " " + Prose(6, "stap" + i)));

    // ── the detector ─────────────────────────────────────────────────────────

    [TestMethod]
    public void BulletAndNumberedShapesBothCount()
    {
        // Deliberately not distinguished (parked gap E5) - what matters for cutting is that the
        // run is peer items rather than continuous prose.
        Assert.IsTrue(ListRunDetector.IsItem("- was je handen"));
        Assert.IsTrue(ListRunDetector.IsItem("* was je handen"));
        Assert.IsTrue(ListRunDetector.IsItem("- was je handen"));
        Assert.IsTrue(ListRunDetector.IsItem("1. was je handen"));
        Assert.IsTrue(ListRunDetector.IsItem("(2) was je handen"));
        Assert.IsTrue(ListRunDetector.IsItem("  3) was je handen"));
    }

    [TestMethod]
    public void ABulletWithNothingAfterIt_IsNotAnItem()
    {
        // A bare dash is a stray dash far more often than an empty list item, so bullets keep
        // the strict "marker then content" form.
        Assert.IsFalse(ListRunDetector.IsItem("-"));
        Assert.IsFalse(ListRunDetector.IsItem("gewone regel"));
    }

    [TestMethod]
    public void ANumberedMarkerAloneOnItsLine_IsStillAnItem()
    {
        // Changed deliberately (260818 plan step 15). PDF extraction strands numbered markers
        // on their own line - 111 bare "N." lines in the 260818 corpus - and the old strict
        // form made one such line fail lines.All(IsItem), dropping the WHOLE block to the prose
        // ladder and losing whole-item cutting for every other item in the run.
        //
        // PdfCleaner.OrphanedListMarker now rejoins these upstream, so this is the second line
        // of defence rather than the fix.
        Assert.IsTrue(ListRunDetector.IsItem("1."));
        Assert.IsTrue(ListRunDetector.IsItem("  3)"));
        Assert.IsTrue(ListRunDetector.IsListRun(Block("1.\n2. tweede regel", BlockKind.ListRun)));
    }

    [TestMethod]
    public void ASingleDashedLine_IsASentenceWithADashInIt()
    {
        Assert.IsFalse(ListRunDetector.IsListRun(Block("- alleen deze regel", BlockKind.ListRun)));
        Assert.IsTrue(ListRunDetector.IsListRun(Block("- een\n- twee", BlockKind.ListRun)));
    }

    [TestMethod]
    public void OneNonItemLine_DisqualifiesTheWholeRun()
    {
        Assert.IsFalse(ListRunDetector.IsListRun(Block("- een\ngewone tekst\n- twee", BlockKind.ListRun)));
    }

    // ── the cutter ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ARunUnderTheCeiling_StaysOnePiece()
    {
        var text = Items(4);

        var pieces = ListRunCutter.Cut(Block(text, BlockKind.ListRun), Tokens(text));

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
    }

    [TestMethod]
    public void CutsLandOnlyBetweenWholeItems()
    {
        // A balanced character split would land mid-item, and half an item is worse than an
        // uneven chunk.
        var text  = Items(30);
        var block = Block(text, BlockKind.ListRun);

        var pieces = ListRunCutter.Cut(block, 60);

        Assert.IsTrue(pieces.Count > 1);
        foreach (var line in pieces.SelectMany(p => p.Text.Split('\n')))
            Assert.IsTrue(ListRunDetector.IsItem(line), "cut inside an item: " + line);

        AssertSliceInvariant(text, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void EveryItemSurvivesExactlyOnce_NoOverlapIsAdded()
    {
        // Repeating whole items across two chunks duplicates instructions rather than restoring
        // context, and a list item is self-contained by construction.
        var pieces = ListRunCutter.Cut(Block(Items(30), BlockKind.ListRun), 60);

        var items = pieces.SelectMany(p => p.Text.Split('\n')).ToList();

        Assert.AreEqual(30, items.Count);
        Assert.AreEqual(items.Count, items.Distinct().Count());
        Assert.IsTrue(pieces.All(p => !p.IsOverlap));
    }

    [TestMethod]
    public void AnItemLongerThanTheCeiling_IsFlaggedRatherThanSplit()
    {
        // The draft would send it down the prose ladder, but that ladder lives in the strategy,
        // and an item split mid-sentence still reads as a whole instruction. Degraded is how it
        // is counted instead of hidden.
        var text = "- " + Prose(300) + "\n- korte stap\n- nog een stap";

        var pieces = ListRunCutter.Cut(Block(text, BlockKind.ListRun), 40);

        Assert.IsTrue(pieces.Any(p => p.Degraded && Tokens(p.Text) > 40));
    }

    [TestMethod]
    public void EveryCutPieceCarriesTheListItemBoundary()
    {
        var pieces = ListRunCutter.Cut(Block(Items(30), BlockKind.ListRun), 60);

        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.ListItem));
    }
}
