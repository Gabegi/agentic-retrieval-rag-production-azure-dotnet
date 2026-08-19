using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// One forward pass that turns a document into blocks. The helper it replaces split on newlines
// and rejoined the runs, which is wrong twice over: it rewrites CRLF as LF, and it produces a
// string with no position in the document. Everything downstream - page attribution, the
// section window, the slice invariant - depends on a block being a WINDOW onto the content.
[TestClass]
public class BlockParserTests
{
    // The property everything else rests on: blocks tile the document exactly, so no character
    // is dropped at parse time and no character is covered twice.
    private static void AssertTilesExactly(string content, IReadOnlyList<ContentBlock> blocks)
    {
        Assert.AreEqual(0, blocks[0].Start);
        Assert.AreEqual(content.Length, blocks[^1].End);

        for (var i = 1; i < blocks.Count; i++)
            Assert.AreEqual(blocks[i - 1].End, blocks[i].Start, "block " + i + " does not start where its predecessor ends");

        foreach (var block in blocks)
            Assert.AreEqual(content.Substring(block.Start, block.Text.Length), block.Text);

        Assert.AreEqual(content, string.Concat(blocks.Select(b => b.Text)));
    }

    [TestMethod]
    public void EmptyContent_ProducesNoBlocks()
    {
        Assert.AreEqual(0, BlockParser.Parse("").Count);
    }

    [TestMethod]
    public void BlocksTileTheDocument_AcrossEveryKind()
    {
        // The newline between two runs belongs to the earlier one, which is what makes the
        // concatenation above reproduce the source exactly.
        const string content =
            "Een gewone alinea met tekst.\n" +
            "\n" +
            "| kop | waarde |\n" +
            "| --- | --- |\n" +
            "| a | 1 |\n" +
            "\n" +
            "- eerste punt\n" +
            "- tweede punt\n" +
            "\n" +
            "Vastgesteld: 12-03-2024\n" +
            "Documentnummer: 4.2.1\n" +
            "\n" +
            "Slotalinea.";

        AssertTilesExactly(content, BlockParser.Parse(content));
    }

    [TestMethod]
    public void EachKindIsRecognised_WhenItStandsOnItsOwn()
    {
        const string content =
            "Inleidende alinea.\n" +
            "\n" +
            "| kop | waarde |\n" +
            "| --- | --- |\n" +
            "| a | 1 |\n" +
            "\n" +
            "- eerste punt\n" +
            "- tweede punt\n" +
            "\n" +
            "Vastgesteld: 12-03-2024\n" +
            "Documentnummer: 4.2.1\n";

        var kinds = BlockParser.Parse(content).Select(b => b.Kind).ToList();

        CollectionAssert.Contains(kinds, BlockKind.Table);
        CollectionAssert.Contains(kinds, BlockKind.ListRun);
        CollectionAssert.Contains(kinds, BlockKind.KeyValue);
        CollectionAssert.Contains(kinds, BlockKind.Prose);
    }

    [TestMethod]
    public void CarriageReturnsSurviveTheParse()
    {
        // The specific regression the running-cursor rewrite fixed: rejoining lines with "\n"
        // silently rewrote every CRLF, so the block no longer matched the source it claimed
        // to slice.
        const string content = "Eerste alinea.\r\n\r\nTweede alinea.\r\n";

        var blocks = BlockParser.Parse(content);

        Assert.IsTrue(blocks.Any(b => b.Text.Contains('\r')));
        AssertTilesExactly(content, blocks);
    }

    [TestMethod]
    public void OneLineContainingPipes_IsProse_NotATable()
    {
        // A table needs two consecutive rows. One line that happens to contain pipes is a
        // sentence with pipes in it, and the run that fails its own detector is demoted.
        const string content = "De kolommen | a | b | staan hier in de lopende tekst.";

        var blocks = BlockParser.Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.Prose, blocks[0].Kind);
    }

    [TestMethod]
    public void ADemotedRun_IsMergedWithItsProseNeighbours()
    {
        // Prose runs that become adjacent after a demotion are one paragraph flow, not three
        // blocks - and the merge is a re-slice, so the text still matches the source.
        const string content = "Alinea een.\n| eenzame pipe regel |\nAlinea twee.";

        var blocks = BlockParser.Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.Prose, blocks[0].Kind);
        Assert.AreEqual(content, blocks[0].Text);
    }

    [TestMethod]
    public void ABareLabel_KeepsTheFollowingLineInTheSameKeyValueRun()
    {
        // The adjacent-line form. After "Vastgesteld:", the next line IS the value and looks
        // like prose, because a value is prose. Closing the run there would put a label and its
        // value in different blocks - the one thing the key-value kind exists to prevent.
        const string content = "Vastgesteld:\n12-03-2024\nDocumentnummer:\n4.2.1";

        var blocks = BlockParser.Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.KeyValue, blocks[0].Kind);
        Assert.AreEqual(content, blocks[0].Text);
    }

    [TestMethod]
    public void ABlankLineTerminatesATableRun_ButNotAParagraphFlow()
    {
        // Blank lines classify as prose so a paragraph keeps the blank line after it, while a
        // table or list run still ends there - which is exactly how those runs end in practice.
        const string content = "| a | b |\n| 1 | 2 |\n\n| c | d |\n| 3 | 4 |";

        var tables = BlockParser.Parse(content).Where(b => b.Kind == BlockKind.Table).ToList();

        Assert.AreEqual(2, tables.Count);
        AssertTilesExactly(content, BlockParser.Parse(content));
    }

    [TestMethod]
    public void ParserAndDetectorsCannotDisagree_AboutWhatABlockIs()
    {
        // Confirm re-runs the block detectors the cascade will run. Any block still claiming an
        // atomic kind has to satisfy that kind's own test, or the cascade would dispatch a
        // block to a cutter that does not recognise it.
        const string content =
            "Alinea.\n\n| a | b |\n| 1 | 2 |\n\n- punt een\n- punt twee\n\nSleutel: waarde\nAnder: iets\n";

        foreach (var block in BlockParser.Parse(content))
        {
            switch (block.Kind)
            {
                case BlockKind.Table:    Assert.IsTrue(TableDetector.IsTable(block));       break;
                case BlockKind.ListRun:  Assert.IsTrue(ListRunDetector.IsListRun(block));   break;
                case BlockKind.KeyValue: Assert.IsTrue(KeyValueDetector.IsKeyValue(block)); break;
            }
        }
    }

    [TestMethod]
    public void WhitespaceOnlyContent_IsOneProseBlock_AndIsNotDroppedHere()
    {
        // Nothing is dropped at parse time; whitespace-only text is trimmed away later, when
        // pieces are built. Dropping it here would break the tiling property.
        const string content = "   \n\n  \n";

        var blocks = BlockParser.Parse(content);

        Assert.AreEqual(1, blocks.Count);
        AssertTilesExactly(content, blocks);
    }
}
