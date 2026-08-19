using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The paragraph rung, consumed at parse time rather than during cutting. Splitting here is
// what lets BlockPacker work in paragraph units - the difference between chunks that sit near
// the ceiling and one 40-token chunk per paragraph.
[TestClass]
public class ProseSplitterTests
{
    [TestMethod]
    public void BlankLinesSeparateParagraphs_AndBelongToNoBlock()
    {
        // Nothing is lost by that: the packer re-slices from the first paragraph's Start to the
        // last one's End, which restores the blank lines in between for free.
        const string text = "Eerste alinea.\n\nTweede alinea.\n\nDerde alinea.";

        var paragraphs = ProseSplitter.SplitParagraphs(Block(text));

        Assert.AreEqual(3, paragraphs.Count);
        CollectionAssert.AreEqual(
            new[] { "Eerste alinea.", "Tweede alinea.", "Derde alinea." },
            paragraphs.Select(p => p.Text).ToArray());
        Assert.IsTrue(paragraphs.All(p => !p.Text.Contains("\n\n")));
    }

    [TestMethod]
    public void EveryParagraphIsATrueSliceOfTheSource()
    {
        const string text = "Regel een.\nRegel twee.\n\nNieuwe alinea.";

        foreach (var paragraph in ProseSplitter.SplitParagraphs(Block(text)))
            Assert.AreEqual(text.Substring(paragraph.Start, paragraph.Text.Length), paragraph.Text);
    }

    [TestMethod]
    public void ConsecutiveLinesWithoutABlankLine_AreOneParagraph()
    {
        // A single line break is a wrapped clause, not a paragraph boundary - that distinction
        // is the whole reason the ladder has a separate line rung below this one.
        const string text = "Regel een.\nRegel twee.\nRegel drie.";

        var paragraphs = ProseSplitter.SplitParagraphs(Block(text));

        Assert.AreEqual(1, paragraphs.Count);
        Assert.AreEqual(text, paragraphs[0].Text);
    }

    [TestMethod]
    public void AFinalParagraphWithoutATrailingNewline_IsStillEmitted()
    {
        var paragraphs = ProseSplitter.SplitParagraphs(Block("Eerste.\n\nLaatste zonder newline"));

        Assert.AreEqual(2, paragraphs.Count);
        Assert.AreEqual("Laatste zonder newline", paragraphs[1].Text);
    }

    [TestMethod]
    public void CoordinatesAreAbsolute_WhenTheBlockSitsDeepInTheDocument()
    {
        const string content = "kop\n\nEerste alinea.\n\nTweede alinea.";
        var block = BlockIn(content, 5, content.Length);

        var paragraphs = ProseSplitter.SplitParagraphs(block);

        Assert.AreEqual(5, paragraphs[0].Start);
        Assert.AreEqual(content.IndexOf("Tweede", StringComparison.Ordinal), paragraphs[1].Start);
    }

    [TestMethod]
    public void RunsOfBlankLinesAndAWhitespaceOnlyBlock_ProduceNoParagraphs()
    {
        Assert.AreEqual(0, ProseSplitter.SplitParagraphs(Block("   \n\n\n  \n")).Count);
        Assert.AreEqual(0, ProseSplitter.SplitParagraphs(Block("")).Count);
    }

    [TestMethod]
    public void LeadingAndTrailingBlankLines_DoNotBecomeEmptyParagraphs()
    {
        var paragraphs = ProseSplitter.SplitParagraphs(Block("\n\nAlinea.\n\n\n"));

        Assert.AreEqual(1, paragraphs.Count);
        Assert.AreEqual("Alinea.", paragraphs[0].Text);
    }
}
