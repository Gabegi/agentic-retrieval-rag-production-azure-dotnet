using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The markup reader and the cutter together (D214). Which block IS a diagram comes off the
// fence in BlockParser (BlockParserTests); this file covers the fence geometry and what happens
// to a block the fence already declared.
//
// The shape Content Understanding actually emits, measured on run 260921/1 (D214 §1): a fenced
// ` ```mermaid ` or ` ```chart ` block whose body is either one line of JSON (376 blocks) or
// Mermaid DSL over several lines (342). The JSON is what matters - a flowchart's edges array
// carries no whitespace for over a thousand characters, and that is the block that reached
// HardCutter and failed the run.
[TestClass]
public class DiagramCuttingTests
{
    // Flowchart JSON as CU writes it: ONE line, node labels that are Dutch sentences, edge
    // labels that are "Ja" / "Nee" - so the nodes half cuts at word gaps and the edges half
    // offers the prose ladder nothing at all.
    private static string FlowchartJson(int nodes, string label = "Signaal, verandering of probleem")
    {
        var nodeList = string.Join(",", Enumerable.Range(0, nodes)
            .Select(i => "{\"id\":\"n" + i + "\",\"label\":\"" + label + " " + i + "\"}"));
        var edgeList = string.Join(",", Enumerable.Range(0, Math.Max(nodes - 1, 0))
            .Select(i => "{\"from\":\"n" + i + "\",\"to\":\"n" + (i + 1) + "\",\"label\":\"Nee\"}"));

        return "{\"type\":\"flowchart\",\"orientation\":\"TB\",\"nodes\":[" + nodeList + "],\"edges\":[" + edgeList + "]}";
    }

    // Mermaid DSL: one statement per line, labels in quotes.
    private static string Dsl(int lines) =>
        "flowchart TD\n" + string.Join("\n", Enumerable.Range(0, lines)
            .Select(i => "A" + i + "[\"" + Prose(4, "stap" + i) + "\"] --> A" + (i + 1)));

    private static string Fenced(string body, string tag = "mermaid") => "```" + tag + "\n" + body + "\n```";

    private static IReadOnlyList<ContentPiece> Cut(string block, int ceiling) =>
        DiagramCutter.Cut(Block(block, BlockKind.Diagram), ceiling);

    private static void AssertPureSlices(string content, IReadOnlyList<ContentPiece> pieces)
    {
        // Every piece here is a slice, so the text comparison AssertSliceInvariant skips for
        // composed pieces is made unconditional: a diagram fragment that is not an exact
        // substring of its block is a cutter bug, not a documented exception.
        foreach (var piece in pieces)
            Assert.AreEqual(piece.Text.Length, piece.Length, "a diagram fragment is never composed");

        AssertSliceInvariant(content, pieces);
    }

    // ── the markup reader ────────────────────────────────────────────────────

    [TestMethod]
    public void AFenceRunsFromItsOpenerLineToItsCloserLine()
    {
        var text   = "Alinea.\n" + Fenced("a\nb") + "\nNa het diagram.\n";
        var fences = DiagramMarkup.Fences(text);

        Assert.AreEqual(1, fences.Count);
        var (start, end) = fences[0];
        StringAssert.StartsWith(text[start..end], "```mermaid\n");
        StringAssert.EndsWith(text[start..end], "\n```");
    }

    [TestMethod]
    public void AnUnclosedFence_IsNotAFence()
    {
        // The cutter can only cut on a boundary the markup declares - the TableMarkup.Rows rule.
        Assert.AreEqual(0, DiagramMarkup.Fences("```mermaid\n{\"type\":\"flowchart\"}\n").Count);
    }

    [TestMethod]
    public void TheInfoStringIsIgnored_AndTheCloserNeedsAtLeastAsManyBackticks()
    {
        Assert.AreEqual(1, DiagramMarkup.Fences("```\nx\n```").Count,     "a bare fence counts");
        Assert.AreEqual(1, DiagramMarkup.Fences("```chart\nx\n```").Count, "a chart fence counts the same");
        Assert.AreEqual(1, DiagramMarkup.Fences("```\nx\n````").Count,    "a longer closer closes");
        Assert.AreEqual(0, DiagramMarkup.Fences("````\nx\n```").Count,    "a shorter closer does not");
    }

    [TestMethod]
    public void TheBodyExcludesBothFenceLines()
    {
        var text = Fenced("a\nb");
        var (start, end) = DiagramMarkup.Body(text, DiagramMarkup.Fences(text)[0]);

        Assert.AreEqual("a\nb\n", text[start..end]);
    }

    [TestMethod]
    public void JsonBoundaries_LandJustAfterAContainerComma()
    {
        var body = FlowchartJson(5);
        var boundaries = DiagramMarkup.Boundaries(body, 0, body.Length).ToList();

        Assert.IsTrue(boundaries.Count >= 8, "five nodes and four edges give at least eight seams");
        foreach (var b in boundaries)
        {
            Assert.AreEqual(',', body[b - 1]);
            Assert.IsTrue(body[b - 2] is '}' or ']', "the comma follows a closing container");
        }
    }

    [TestMethod]
    public void NonJsonBoundaries_LandAtLineStarts_SkippingTheFirstLine()
    {
        var body = Dsl(6);
        var boundaries = DiagramMarkup.Boundaries(body, 0, body.Length).ToList();

        Assert.AreEqual(6, boundaries.Count);
        foreach (var b in boundaries) Assert.AreEqual('\n', body[b - 1]);
    }

    // ── the cutter ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ADiagramUnderTheCeiling_StaysOnePiece()
    {
        var block  = Fenced(FlowchartJson(3));
        var pieces = Cut(block, Tokens(block));

        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(BoundaryLevel.None, pieces[0].BoundaryLevel);
        Assert.AreEqual(block, pieces[0].Text);
        Assert.IsFalse(pieces[0].Degraded);
    }

    [TestMethod]
    public void JsonCutsLandOnlyAfterContainerCommas()
    {
        var block   = Fenced(FlowchartJson(40));
        var ceiling = Tokens(block) / 4;
        var pieces  = Cut(block, ceiling);

        Assert.IsTrue(pieces.Count > 1);
        StringAssert.StartsWith(pieces[0].Text, "```mermaid\n", "the opener rides on the first fragment");
        StringAssert.EndsWith(pieces[^1].Text, "\n```",          "the closer rides on the last");

        foreach (var piece in pieces.Skip(1))
        {
            Assert.AreEqual(',', block[piece.Start - 1], "a fragment starts right after a comma");
            Assert.IsTrue(block[piece.Start - 2] is '}' or ']', "…that closes a container");
        }

        AssertPureSlices(block, pieces);
    }

    [TestMethod]
    public void DslCutsLandOnlyAtLineStarts()
    {
        var block   = Fenced(Dsl(80));
        var ceiling = Tokens(block) / 4;
        var pieces  = Cut(block, ceiling);

        Assert.IsTrue(pieces.Count > 1);
        foreach (var piece in pieces.Skip(1))
            Assert.AreEqual('\n', block[piece.Start - 1], "a fragment starts at a line start");

        AssertPureSlices(block, pieces);
    }

    [TestMethod]
    public void EveryCutPieceCarriesTheDiagramElementBoundary_AndFits()
    {
        var block   = Fenced(FlowchartJson(40));
        var ceiling = Tokens(block) / 3;

        foreach (var piece in Cut(block, ceiling))
        {
            Assert.AreEqual(BoundaryLevel.DiagramElement, piece.BoundaryLevel);
            Assert.IsTrue(Tokens(piece.Text) <= ceiling, "no piece over the ceiling unless flagged");
            Assert.IsFalse(piece.Degraded);
        }
    }

    [TestMethod]
    public void AnElementLongerThanTheCeiling_IsFlaggedRatherThanSplit()
    {
        // One node whose label is a 2,000-character run with no whitespace: no rung could cut
        // it, and this cutter does not try - the reader cannot tell half a label from a whole
        // one. It comes back whole, oversized and Degraded; its neighbours fit.
        var huge  = new string('x', 2000);
        var body  = "{\"nodes\":[" +
                    "{\"id\":\"a\",\"label\":\"" + Prose(6) + "\"}," +
                    "{\"id\":\"b\",\"label\":\"" + huge + "\"}," +
                    "{\"id\":\"c\",\"label\":\"" + Prose(6) + "\"}]}";
        var block = Fenced(body);

        var pieces = Cut(block, 60);

        var flagged = pieces.Where(p => p.Degraded).ToList();
        Assert.AreEqual(1, flagged.Count, "exactly the oversized element is flagged");
        StringAssert.Contains(flagged[0].Text, huge, "and it is intact");

        foreach (var piece in pieces.Where(p => !p.Degraded))
            Assert.IsTrue(Tokens(piece.Text) <= 60);

        Assert.IsFalse(pieces.Any(p => p.BoundaryLevel == BoundaryLevel.HardCut));
        AssertPureSlices(block, pieces);
    }

    [TestMethod]
    public void CoordinatesSurvive_WhenTheDiagramSitsDeepInTheDocument()
    {
        var before  = Prose(50) + "\n\n";
        var block   = Fenced(FlowchartJson(30));
        var content = before + block + "\n\nNa het diagram.\n";

        var pieces = DiagramCutter.Cut(
            BlockIn(content, before.Length, before.Length + block.Length, BlockKind.Diagram),
            Tokens(block) / 3);

        Assert.IsTrue(pieces.Count > 1);
        AssertPureSlices(content, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void NothingIsLostOrDuplicated()
    {
        var block  = Fenced(FlowchartJson(40));
        var pieces = Cut(block, Tokens(block) / 4);

        AssertAscendingAndDisjoint(pieces);

        // Every non-whitespace character of the block lands in exactly one fragment; only the
        // whitespace at the seams (trimmed by PieceFactory) is outside all of them.
        var covered = new bool[block.Length];
        foreach (var piece in pieces)
            for (var i = piece.Start; i < piece.Start + piece.Length; i++)
                covered[i] = true;

        for (var i = 0; i < block.Length; i++)
            if (!char.IsWhiteSpace(block[i]))
                Assert.IsTrue(covered[i], "character " + i + " ('" + block[i] + "') is in no fragment");
    }

    // ── the real failure ─────────────────────────────────────────────────────

    [TestMethod]
    public void TheBlockThatFailedRun260921_NeverReachesHardCutter()
    {
        // The 4,607-character ` ```mermaid ` block of pdf/5d10e5bd… (D209 §4.1), verbatim from
        // the run's extraction artifact. Its edges array is a 1,487-character run with no
        // whitespace - the largest in the corpus - which is what beat the word rung.
        var block = CorpusText("diagram-stepped-care-55555555.md");

        var longestRun = Regex.Matches(block, @"\S+").Max(m => m.Length);
        Assert.IsTrue(longestRun >= 1400, "the fixture must still carry the gap-free run that caused the failure: " + longestRun);

        var content = "# Stepped Care model\n\n" + block + "\n";
        var pieces  = BlockCascade.Cut(content, 0, content.Length, ChunkingBudget.TokenCeiling, tables: []);

        var diagram = pieces.Where(p => p.Start >= content.IndexOf("```", StringComparison.Ordinal)).ToList();

        Assert.IsTrue(diagram.Count > 1, "4,592 characters of JSON do not fit one 512-token piece");
        Assert.IsFalse(pieces.Any(p => p.BoundaryLevel == BoundaryLevel.HardCut), "HardCutter is unreachable from a diagram block");

        foreach (var piece in diagram)
        {
            Assert.AreEqual(BoundaryLevel.DiagramElement, piece.BoundaryLevel);
            Assert.IsTrue(Tokens(piece.Text) <= ChunkingBudget.TokenCeiling || piece.Degraded);
        }

        AssertPureSlices(content, pieces);
        AssertAscendingAndDisjoint(pieces);
    }

    [TestMethod]
    public void TheCascadePricesTheFigureContext_OnCutFragmentsOnly()
    {
        // D214 §2.6: a cut fragment will carry the caption in its prefix, so the cascade cuts
        // against a ceiling reduced by that cost - priced with the same "\n\n" joiner
        // EmbeddingText uses. A block that fits whole is not charged: it gets no context.
        const string caption = "Stepped Care Triageproces VGZ";
        var body    = FlowchartJson(40);
        var content = Fenced(body) + "\n";
        var figures = new[] { new FigureInfo(caption, 0, 1, "2.1", [], Kind: "mermaid", Payload: body) };

        var ceiling      = Tokens(content) / 3;
        var contextCost  = Tokens("\n\n" + caption);
        var pieces       = BlockCascade.Cut(content, 0, content.Length, ceiling, tables: [], figures);

        Assert.IsTrue(pieces.Count > 1);
        foreach (var piece in pieces)
            Assert.IsTrue(Tokens(piece.Text) + contextCost <= ceiling || piece.Degraded,
                "a cut fragment must leave room for its context: " + Tokens(piece.Text) + " + " + contextCost + " > " + ceiling);

        // Fits whole → one piece, uncharged, regardless of the figure.
        var small = Fenced(FlowchartJson(2)) + "\n";
        var whole = BlockCascade.Cut(small, 0, small.Length, Tokens(small), tables: [], figures);
        Assert.AreEqual(1, whole.Count);
        Assert.AreEqual(BoundaryLevel.None, whole[0].BoundaryLevel);
    }

    [TestMethod]
    public void JsonLinesInsideAFence_AreNeverAKeyValueRun()
    {
        // The median JSON block has three lines, and each of them satisfies the key-value line
        // test - which is why the fence has to claim the line before any detector sees it.
        const string l1 = "{\"type\":\"flowchart\",\"orientation\":\"TB\",";
        const string l2 = "\"nodes\":[{\"id\":\"A\",\"label\":\"Signaal\"}],";
        const string l3 = "\"edges\":[{\"from\":\"A\",\"to\":\"B\",\"label\":\"Nee\"}]}";

        Assert.IsTrue(KeyValueDetector.IsPair(l1) && KeyValueDetector.IsPair(l2) && KeyValueDetector.IsPair(l3),
            "the premise: each JSON line looks like a label: value pair");

        var content = "Alinea.\n\n" + Fenced(l1 + "\n" + l2 + "\n" + l3) + "\n";
        var blocks  = BlockParser.Parse(content, []);

        Assert.AreEqual(1, blocks.Count(b => b.Kind == BlockKind.Diagram));
        Assert.AreEqual(0, blocks.Count(b => b.Kind == BlockKind.KeyValue));
    }
}
