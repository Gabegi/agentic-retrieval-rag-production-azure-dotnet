using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class TokenCounterTests
{
    [TestMethod]
    public void EmptyOrNull_IsZero()
    {
        Assert.AreEqual(0, TokenCounter.Count(null));
        Assert.AreEqual(0, TokenCounter.Count(""));
    }

    [TestMethod]
    public void KnownEnglishPhrase_MatchesCl100kBase()
    {
        // "hello world" is 2 tokens in cl100k_base. One concrete anchor is worth having:
        // without it every other assertion here is only self-consistent, and a tokenizer
        // silently resolving to the wrong encoding would still pass them all.
        Assert.AreEqual(2, TokenCounter.Count("hello world"));
    }

    [TestMethod]
    public void TableMarkdownTokenizesLessEfficientlyThanProse_AtEqualLength()
    {
        // This is the whole reason a single blended chars-per-token ratio was unsafe, and
        // therefore the reason the real tokenizer is used for anything stored or enforced.
        // Pipes and short cells fragment into more tokens than continuous prose does.
        var prose = new string('a', 0) + "Dit is een gewone Nederlandse zin met normale woorden erin.";
        var table = "| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |";

        var proseTokens = TokenCounter.Count(prose);
        var tableTokens = TokenCounter.Count(table);

        Assert.IsTrue(tableTokens / (double)table.Length > proseTokens / (double)prose.Length,
            $"table {tableTokens}/{table.Length} vs prose {proseTokens}/{prose.Length}");
    }

    [TestMethod]
    public void RealCountDivergesFromTheRatioEstimate_OnTableMarkdown()
    {
        // The reason C2 exists. The ratio estimate is calibrated on continuous prose; a
        // table-heavy chunk sized by it can cross a 512-token ceiling undetected, and the
        // stored count is kept precisely because it cannot be re-derived from length later.
        // If these two ever agreed closely on table markdown, the estimate would have been
        // good enough and the tokenizer would be dead weight.
        var table = "| Functie | Schaal | Bedrag |\n| --- | --- | --- |\n" +
                    string.Join("\n", Enumerable.Range(0, 30).Select(i => $"| Rol {i} | FWG {i} | {i}00,00 |"));

        var real     = TokenCounter.Count(table);
        var estimate = ChunkingHelper.EstimateTokens(table, isTable: true);

        Assert.AreNotEqual(real, estimate);
        Assert.IsTrue(real > 0 && estimate > 0);
    }

    [TestMethod]
    public void RepeatedCalls_AreStable()
    {
        // The tokenizer is built once and shared across the parallel document loops, so it
        // must be safe to call repeatedly and concurrently.
        const string text = "Vakantietoeslag bedraagt 8,33% van het brutoloon.";
        var expected = TokenCounter.Count(text);

        Parallel.For(0, 50, _ => Assert.AreEqual(expected, TokenCounter.Count(text)));
    }

    // ── Truncate (D214 §2.6) ─────────────────────────────────────────────────

    [TestMethod]
    public void Truncate_LeavesTextUnderTheCapAlone_AndHandlesNothing()
    {
        Assert.AreEqual("hello world", TokenCounter.Truncate("hello world", 2));
        Assert.AreEqual("hello world", TokenCounter.Truncate("hello world", 40));
        Assert.IsNull(TokenCounter.Truncate(null, 40));
        Assert.AreEqual("", TokenCounter.Truncate("", 40));
        Assert.AreEqual("", TokenCounter.Truncate("hello world", 0));
    }

    [TestMethod]
    public void Truncate_NeverExceedsTheCap_AndEndsOnAWholeWord()
    {
        var text = string.Join(" ", Enumerable.Range(0, 200).Select(i => "beschrijving" + i));

        var cut = TokenCounter.Truncate(text, 40)!;

        Assert.IsTrue(TokenCounter.Count(cut) <= 40, "got " + TokenCounter.Count(cut));
        Assert.IsTrue(cut.Length > 0 && cut.Length < text.Length);
        StringAssert.StartsWith(text, cut);
        Assert.IsFalse(char.IsWhiteSpace(cut[^1]), "no trailing whitespace");
        Assert.IsTrue(char.IsWhiteSpace(text[cut.Length]), "the cut falls on a word boundary, not inside a word");
    }

    [TestMethod]
    public void Truncate_TextWithoutWhitespace_KeepsTheTokenBoundary()
    {
        // Nothing to back up to, so the token boundary itself is the cut - still under the cap.
        var text = new string('x', 600);

        var cut = TokenCounter.Truncate(text, 10)!;

        Assert.IsTrue(cut.Length > 0 && cut.Length < text.Length);
        Assert.IsTrue(TokenCounter.Count(cut) <= 10);
    }
}
