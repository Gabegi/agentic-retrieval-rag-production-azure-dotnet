using AgenticRagApp.Indexing.Pdf.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// Merges consecutive PROSE blocks up to the ceiling. Without it, a document of twelve
// 40-token paragraphs becomes twelve 40-token chunks: each embeds badly, and top-k fills with
// fragments of one document.
[TestClass]
public class BlockPackerTests
{
    [TestMethod]
    public void ConsecutiveProse_MergesUpToTheCeiling()
    {
        var paragraph = Prose(10);
        var content   = string.Join("\n\n", Enumerable.Repeat(paragraph, 4));
        var blocks    = ProseSplitter.SplitParagraphs(Block(content));

        var packed = BlockPacker.Pack(content, blocks, Tokens(content) + 10);

        Assert.AreEqual(1, packed.Count);
        Assert.AreEqual(content, packed[0].Text);
    }

    [TestMethod]
    public void MergingIsAReSlice_SoTheBlankLinesBetweenParagraphsComeBack()
    {
        // Paragraph blocks cover no blank line, so a JOIN of their texts would have to guess
        // the whitespace between them. Slicing from the first Start to the last End restores it
        // for free and keeps the coordinates true.
        const string content = "Eerste alinea.\n\nTweede alinea.";
        var blocks = ProseSplitter.SplitParagraphs(Block(content));

        var packed = BlockPacker.Pack(content, blocks, 4096);

        Assert.AreEqual(1, packed.Count);
        Assert.AreEqual(content, packed[0].Text);
        StringAssert.Contains(packed[0].Text, "\n\n");
        Assert.AreEqual(content.Substring(packed[0].Start, packed[0].Text.Length), packed[0].Text);
    }

    [TestMethod]
    public void ParagraphsStopMerging_AtTheCeiling()
    {
        var paragraph = Prose(10);
        var content   = string.Join("\n\n", Enumerable.Repeat(paragraph, 6));
        var blocks    = ProseSplitter.SplitParagraphs(Block(content));

        // Room for about two paragraphs at a time.
        var packed = BlockPacker.Pack(content, blocks, Tokens(paragraph) * 2 + 1);

        Assert.IsTrue(packed.Count > 1);
        Assert.IsTrue(packed.Count < blocks.Count);
    }

    [TestMethod]
    public void AnAtomicBlockIsNeverMergedIntoTheProseAroundIt()
    {
        // Absorbing a table into the paragraph before it would put two different things behind
        // one vector and make the table unfindable as a table.
        const string content =
            "Alinea voor de tabel.\n\n| kop | waarde |\n| --- | --- |\n| a | 1 |\n\nAlinea na de tabel.";

        var blocks = BlockParser.Parse(content);
        var packed = BlockPacker.Pack(content, blocks, 4096);

        var table = packed.Single(b => b.Kind == BlockKind.Table);
        Assert.IsFalse(table.Text.Contains("Alinea"));
        Assert.AreEqual(blocks.Count, packed.Count, "an atomic block separates the runs either side of it");
    }

    [TestMethod]
    public void ProseEitherSideOfATable_IsNotMergedAcrossIt()
    {
        // The separation is real: the text before a table and the text after it are not
        // adjacent, and merging them would put the before and after in one chunk with the table
        // itself in another.
        const string content =
            "Alinea voor de tabel.\n\n| a | b |\n| 1 | 2 |\n\nAlinea na de tabel.";

        var packed = BlockPacker.Pack(content, BlockParser.Parse(content), 4096);

        Assert.AreEqual(3, packed.Count);
        Assert.IsFalse(packed.Any(b => b.Text.Contains("voor") && b.Text.Contains("na de tabel")));
    }

    [TestMethod]
    public void TheTokenAccumulatorResets_AfterABlockThatCouldNotMerge()
    {
        // A stale accumulator would carry the previous run's cost into the next one and stop
        // packing far below the ceiling - chunks that are small for no measurable reason.
        var paragraph = Prose(10);
        var content   = string.Join("\n\n", Enumerable.Repeat(paragraph, 3)) +
                        "\n\n| a | b |\n| 1 | 2 |\n\n" +
                        string.Join("\n\n", Enumerable.Repeat(paragraph, 3));

        var packed = BlockPacker.Pack(content, BlockParser.Parse(content), 4096);

        Assert.AreEqual(3, packed.Count);
        Assert.AreEqual(BlockKind.Table, packed[1].Kind);
        StringAssert.Contains(packed[2].Text, paragraph);
    }

    [TestMethod]
    public void NoBlocks_PacksToNothing()
    {
        Assert.AreEqual(0, BlockPacker.Pack("", [], 512).Count);
    }

    [TestMethod]
    public void AParagraphAlreadyOverTheCeiling_IsPassedThroughForTheLadderToCut()
    {
        // The packer only ever makes things bigger; making them smaller is the ladder's job,
        // and a block it cannot merge has to arrive there intact.
        var big     = Prose(400);
        var content = big;

        var packed = BlockPacker.Pack(content, ProseSplitter.SplitParagraphs(Block(content)), 50);

        Assert.AreEqual(1, packed.Count);
        Assert.IsTrue(Tokens(packed[0].Text) > 50);
    }
}
