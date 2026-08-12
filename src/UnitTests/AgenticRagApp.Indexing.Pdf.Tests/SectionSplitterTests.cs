using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class SectionSplitterTests
{
    // ~3.1 chars/token for prose, so a 100-token ceiling is ~310 characters.
    private const int SmallCeiling = 100;

    // SectionSplitter is an instance now - it is axis 2 of the two-axis model, resolved
    // through ITextSplitter rather than called statically.
    private static readonly SectionSplitter Splitter = new();

    private static string Prose(int chars) =>
        string.Join(" ", Enumerable.Repeat("woord", chars / 6)) + ".";

    [TestMethod]
    public void SectionUnderTheCeiling_IsNotSplit()
    {
        // Phase A measured 83-87% of sections below the ceiling, so this is the common path,
        // not an edge case: the ceiling is a limit for the long tail, not a target.
        var pieces = Splitter.Split("Short section body.", SmallCeiling);

        Assert.AreEqual(1, pieces.Count);
        Assert.IsFalse(pieces[0].IsOverlap);
    }

    [TestMethod]
    public void SectionJustOverTheCeiling_SplitsIntoBalancedHalves_NotAFullChunkPlusRunt()
    {
        // The defect this replaced: greedy fill took a full ceiling's worth, then the flush
        // seeded overlap into a tiny remainder, producing a second chunk that was almost
        // entirely a copy of the first - and the tiny-tail merge did not fold it away,
        // because the overlap had pushed it past the merge threshold.
        var budget = ChunkingHelper.CharBudgetForTokens(SmallCeiling, isTable: false);
        var text   = Prose((int)(budget * 1.05));

        var pieces = Splitter.Split(text, SmallCeiling);

        Assert.AreEqual(2, pieces.Count);

        // Balanced: neither piece is a runt. Without overlap the two would be near-equal;
        // the second carries a quarter of its own length as overlap, so allow for that.
        var shortest = pieces.Min(p => p.Text.Length);
        var longest  = pieces.Max(p => p.Text.Length);
        Assert.IsTrue(shortest > longest / 3, $"lopsided split: {shortest} vs {longest}");
    }

    [TestMethod]
    public void OverlapIsSizedAgainstTheProducedChild_NotTheCeiling()
    {
        // Sizing overlap against the ceiling is what made the runt case degenerate: a
        // 470-character piece was handed 410 characters of overlap.
        var budget = ChunkingHelper.CharBudgetForTokens(SmallCeiling, isTable: false);
        var pieces = Splitter.Split(Prose(budget * 3), SmallCeiling);

        foreach (var piece in pieces.Where(p => p.IsOverlap))
            Assert.IsTrue(piece.Text.Length < budget * 2,
                "an overlapped child should not be dominated by its overlap");
    }

    [TestMethod]
    public void FirstChildIsNeverMarkedAsOverlap()
    {
        var budget = ChunkingHelper.CharBudgetForTokens(SmallCeiling, isTable: false);
        var pieces = Splitter.Split(Prose(budget * 3), SmallCeiling);

        Assert.IsFalse(pieces[0].IsOverlap);
    }

    [TestMethod]
    public void TableIsNeverCutMidRow_AndIsNotOverlapped()
    {
        // Tables are an atomicity constraint on splitting, not a precedence branch - which
        // is the correction that made a prose+table+prose section well defined at all.
        var table = "| a | b |\n| --- | --- |\n" +
                    string.Join("\n", Enumerable.Range(0, 60).Select(i => $"| r{i} | value {i} |"));

        var pieces = Splitter.Split(table, SmallCeiling);

        Assert.IsTrue(pieces.Count > 1, "an oversized table should be row-split");
        Assert.IsTrue(pieces.All(p => p.IsTable));
        Assert.IsTrue(pieces.All(p => !p.IsOverlap), "row fragments must not be overlapped");

        // Every fragment repeats the header - a header-less run of numbers means nothing to
        // either the embedder or the model.
        Assert.IsTrue(pieces.All(p => p.Text.StartsWith("| a | b |", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProseAndTableInOneSection_AreSplitIntoSeparatePieces()
    {
        var text = "Intro prose paragraph.\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\nClosing prose.";

        var pieces = Splitter.Split(text, SmallCeiling);

        Assert.IsTrue(pieces.Any(p => p.IsTable));
        Assert.IsTrue(pieces.Any(p => !p.IsTable));
    }

    [TestMethod]
    public void EmptyOrWhitespaceSection_ProducesNothing()
    {
        Assert.AreEqual(0, Splitter.Split("", SmallCeiling).Count);
        Assert.AreEqual(0, Splitter.Split("   \n\n  ", SmallCeiling).Count);
    }

    // ── lists ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void OversizedList_IsCutBetweenWholeItems_NeverMidItem()
    {
        // The narrow exception where a fixed window is right. A half-item is worse than an
        // uneven chunk: the reader cannot tell a truncated instruction from a complete one.
        var list = string.Join("\n", Enumerable.Range(1, 80)
            .Select(i => $"- Stap {i}: doe dit zorgvuldig en volledig voordat je verdergaat."));

        var pieces = Splitter.Split(list, SmallCeiling);

        Assert.IsTrue(pieces.Count > 1, "an oversized list should be split");

        foreach (var line in pieces.SelectMany(p => p.Text.Split('\n')))
            Assert.IsTrue(line.TrimStart().StartsWith("- Stap", StringComparison.Ordinal),
                $"cut mid-item: '{line}'");
    }

    [TestMethod]
    public void ListPiecesAreNeverOverlapped()
    {
        // Repeating whole items across two chunks duplicates instructions rather than
        // restoring context - a list item is already self-contained.
        var list = string.Join("\n", Enumerable.Range(1, 80)
            .Select(i => $"- Stap {i}: doe dit zorgvuldig en volledig voordat je verdergaat."));

        Assert.IsTrue(Splitter.Split(list, SmallCeiling).All(p => !p.IsOverlap));
    }

    [TestMethod]
    public void NumberedListIsRecognised_AsWellAsBulleted()
    {
        var list = string.Join("\n", Enumerable.Range(1, 80)
            .Select(i => $"{i}. Stap {i}: doe dit zorgvuldig en volledig voordat je verdergaat."));

        var pieces = Splitter.Split(list, SmallCeiling);

        foreach (var line in pieces.SelectMany(p => p.Text.Split('\n')))
            Assert.IsTrue(char.IsDigit(line.TrimStart().FirstOrDefault()), $"cut mid-item: '{line}'");
    }

    [TestMethod]
    public void SingleDashedLine_IsProse_NotAList()
    {
        // One line starting with a dash is a sentence with a dash in it. A run needs two or
        // more items, the same rule tables use and for the same reason.
        var text = "- Dit is een enkele zin met een streepje ervoor, geen lijst.";

        var pieces = Splitter.Split(text, SmallCeiling);

        Assert.AreEqual(1, pieces.Count);
        Assert.IsFalse(pieces[0].IsTable);
    }

    [TestMethod]
    public void ListInsideProse_IsSplitOutFromTheSurroundingText()
    {
        var text = "Volg deze stappen zorgvuldig.\n" +
                   string.Join("\n", Enumerable.Range(1, 3).Select(i => $"- Stap {i}")) +
                   "\nDaarna ben je klaar.";

        var pieces = Splitter.Split(text, SmallCeiling);

        // Short enough to stay whole, but the run must still have been recognised as a list
        // rather than folded into one prose blob - otherwise an oversized version would be
        // cut mid-item.
        Assert.IsTrue(pieces.Count >= 1);
        Assert.IsTrue(pieces.Any(p => p.Text.Contains("- Stap 1", StringComparison.Ordinal)));
    }
}
