using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// One forward pass that turns a document into blocks. The helper it replaces split on newlines
// and rejoined the runs, which is wrong twice over: it rewrites CRLF as LF, and it produces a
// string with no position in the document. Everything downstream - page attribution, the
// section window, the slice invariant - depends on a block being a WINDOW onto the content.
//
// Tables are TYPED (2026-09-09): the parser is handed the ranges Content Understanding reported
// as tables and detects nothing table-shaped itself. The fixtures scan their own markdown for
// `<table>...</table>` to stand in for the analyzer (TableRangesIn) - production never does.
[TestClass]
public class BlockParserTests
{
    private const string HtmlTable =
        "<table><tr><th>kop</th><th>waarde</th></tr><tr><td>a</td><td>1</td></tr></table>";

    private static IReadOnlyList<ContentBlock> Parse(string content) =>
        BlockParser.Parse(content, TableRangesIn(content));

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
        Assert.AreEqual(0, Parse("").Count);
    }

    [TestMethod]
    public void BlocksTileTheDocument_AcrossEveryKind()
    {
        // The newline between two runs belongs to the earlier one, which is what makes the
        // concatenation above reproduce the source exactly.
        var content =
            "Een gewone alinea met tekst.\n" +
            "\n" +
            HtmlTable + "\n" +
            "\n" +
            "- eerste punt\n" +
            "- tweede punt\n" +
            "\n" +
            "Vastgesteld: 12-03-2024\n" +
            "Documentnummer: 4.2.1\n" +
            "\n" +
            "Slotalinea.";

        AssertTilesExactly(content, Parse(content));
    }

    [TestMethod]
    public void EachKindIsRecognised_WhenItStandsOnItsOwn()
    {
        var content =
            "Inleidende alinea.\n" +
            "\n" +
            HtmlTable + "\n" +
            "\n" +
            "- eerste punt\n" +
            "- tweede punt\n" +
            "\n" +
            "Vastgesteld: 12-03-2024\n" +
            "Documentnummer: 4.2.1\n";

        var kinds = Parse(content).Select(b => b.Kind).ToList();

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

        var blocks = Parse(content);

        Assert.IsTrue(blocks.Any(b => b.Text.Contains('\r')));
        AssertTilesExactly(content, blocks);
    }

    // ── tables are typed, not detected ───────────────────────────────────────

    [TestMethod]
    public void ATableIsWhereTheServiceSaysItIs_NotWhereTheMarkupLooksLikeOne()
    {
        // The same text twice: once with the typed span, once without. Only the span makes a
        // table block - the markup on its own is not evidence, the service is.
        const string content = "Inleiding.\n\n" + HtmlTable + "\n\nSlot.";

        var typed   = BlockParser.Parse(content, TableRangesIn(content));
        var untyped = BlockParser.Parse(content, []);

        Assert.AreEqual(1, typed.Count(b => b.Kind == BlockKind.Table));
        Assert.AreEqual(0, untyped.Count(b => b.Kind == BlockKind.Table), "no span, no table - never a regex fallback");
        Assert.AreEqual(1, untyped.Count, "without the span the whole thing is one prose flow");
        AssertTilesExactly(content, typed);
        AssertTilesExactly(content, untyped);
    }

    [TestMethod]
    public void GfmPipeRows_AreProse()
    {
        // Content Understanding never emits GFM (tableFormat is fixed at html), and nothing here
        // recognises it any more - two pipe rows are two lines of prose with pipes in them.
        const string content = "| kop | waarde |\n| --- | --- |\n| a | 1 |";

        var blocks = Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.Prose, blocks[0].Kind);
    }

    [TestMethod]
    public void TwoTypedTablesBackToBack_AreTwoBlocks()
    {
        // TableCutter closes and repeats the markup of ONE table; two spans that touch are still
        // two tables, whatever the lines between them look like.
        const string content = HtmlTable + "\n" + HtmlTable;

        var blocks = Parse(content);

        Assert.AreEqual(2, blocks.Count(b => b.Kind == BlockKind.Table));
        AssertTilesExactly(content, blocks);
    }

    [TestMethod]
    public void ABlankLineTerminatesATableRun_ButNotAParagraphFlow()
    {
        // Blank lines classify as prose so a paragraph keeps the blank line after it, while a
        // table run still ends where its span does.
        const string content = HtmlTable + "\n\n" + HtmlTable;

        var blocks = Parse(content);

        Assert.AreEqual(2, blocks.Count(b => b.Kind == BlockKind.Table));
        AssertTilesExactly(content, blocks);
    }

    [TestMethod]
    public void AnHtmlTableOnOneLine_IsOneTableBlock()
    {
        // How Content Understanding actually writes a table: markup with no newlines of its own.
        const string content =
            "Inleidende alinea.\n\n" +
            "<table><tr><th>Functie</th></tr><tr><td>Verpleegkundige</td></tr></table>\n\n" +
            "Slotalinea.\n";

        var blocks = Parse(content);

        var table = blocks.Single(b => b.Kind == BlockKind.Table);
        StringAssert.Contains(table.Text, "<table>");
        StringAssert.Contains(table.Text, "</table>");
        AssertTilesExactly(content, blocks);
    }

    [TestMethod]
    public void AMultiLineTypedTable_StaysOneBlock_WhateverItsLinesLookLike()
    {
        // The span covers lines with no markup on them and a blank line inside the table; all of
        // them are the table's, because the span says so - nothing looks at the line text.
        const string content =
            "<table>\n" +
            "<tr><th>Functie</th><th>Toelichting</th></tr>\n" +
            "<tr><td>Verpleegkundige</td><td>Een lange toelichting die\n" +
            "\n" +
            "doorloopt op de volgende regel</td></tr>\n" +
            "</table>\n" +
            "Gewone tekst na de tabel.\n";

        var blocks = Parse(content);

        var table = blocks.Single(b => b.Kind == BlockKind.Table);
        StringAssert.Contains(table.Text, "doorloopt op de volgende regel");
        // The prose after the closing tag is its own block - the run ends where the span does.
        StringAssert.Contains(blocks[^1].Text, "Gewone tekst na de tabel.");
        Assert.AreEqual(BlockKind.Prose, blocks[^1].Kind);
        AssertTilesExactly(content, blocks);
    }

    [TestMethod]
    public void AStrayTagMentionInProse_StaysProse()
    {
        // A sentence mentioning a tag has no typed span, so it opens no table - and no detector
        // is left to be fooled by it.
        const string content = "De kolom <td> hoort bij de opmaak, niet bij de inhoud.\n";

        var blocks = Parse(content);

        Assert.AreEqual(BlockKind.Prose, blocks.Single().Kind);
        AssertTilesExactly(content, blocks);
    }

    // ── the detected kinds ───────────────────────────────────────────────────

    [TestMethod]
    public void ABareLabel_KeepsTheFollowingLineInTheSameKeyValueRun()
    {
        // The adjacent-line form. After "Vastgesteld:", the next line IS the value and looks
        // like prose, because a value is prose. Closing the run there would put a label and its
        // value in different blocks - the one thing the key-value kind exists to prevent.
        const string content = "Vastgesteld:\n12-03-2024\nDocumentnummer:\n4.2.1";

        var blocks = Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.KeyValue, blocks[0].Kind);
        Assert.AreEqual(content, blocks[0].Text);
    }

    [TestMethod]
    public void ADemotedRun_IsMergedWithItsProseNeighbours()
    {
        // A single list-looking line fails the list-run detector (two items minimum) and is
        // demoted; prose runs that become adjacent after a demotion are one paragraph flow, and
        // the merge is a re-slice, so the text still matches the source.
        const string content = "Alinea een.\n- eenzaam punt\nAlinea twee.";

        var blocks = Parse(content);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(BlockKind.Prose, blocks[0].Kind);
        Assert.AreEqual(content, blocks[0].Text);
    }

    [TestMethod]
    public void ParserAndDetectorsCannotDisagree_AboutWhatABlockIs()
    {
        // Confirm re-runs the block detectors the cascade will run. Any block still claiming a
        // detected kind has to satisfy that kind's own test, or the cascade would dispatch a
        // block to a cutter that does not recognise it. Tables are not confirmed: the service
        // typed them.
        var content =
            "Alinea.\n\n" + HtmlTable + "\n\n- punt een\n- punt twee\n\nSleutel: waarde\nAnder: iets\n";

        foreach (var block in Parse(content))
        {
            switch (block.Kind)
            {
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

        var blocks = Parse(content);

        Assert.AreEqual(1, blocks.Count);
        AssertTilesExactly(content, blocks);
    }
}
