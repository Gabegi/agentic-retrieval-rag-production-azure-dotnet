using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

// The GFM table members (ContainsTable, SplitIntoBlocks) and their tests went 2026-09-09: tables
// are typed by Content Understanding and cut by TableCutter, and a pipe-row regex matched nothing
// CU emits.
[TestClass]
public class ChunkingHelperTests
{
    [TestMethod]
    public void EstimateTokens_EmptyContent_IsZero()
    {
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: false));
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: true));
    }

    [TestMethod]
    public void EstimateTokens_Prose_UsesProseRatioAndRoundsUp()
    {
        // 10 chars / 3.1 = 3.226... -> ceil to 4.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: false);

        Assert.AreEqual(4, tokens);
    }

    [TestMethod]
    public void EstimateTokens_Table_UsesTableRatioAndRoundsUp()
    {
        // 10 chars / 1.8 = 5.55... -> ceil to 6. The table ratio was 2.2 until it was
        // re-measured with the real tokenizer over the whole cached text of the big four:
        // two documents came back below 2.20 (CAO VVT 1.88, CAO GHZ 2.00), which made the
        // old constant underestimate table tokens by ~17% - the direction that silently
        // overruns a ceiling.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: true);

        Assert.AreEqual(6, tokens);
    }

    [TestMethod]
    public void EstimateTokens_SameContent_TableEstimateIsHigherThanProse()
    {
        // Table markdown tokenizes less efficiently (fewer chars/token), so the same content
        // must never estimate *fewer* tokens under the table ratio than the prose ratio.
        var content = new string('a', 500);

        var proseTokens = ChunkingHelper.EstimateTokens(content, isTable: false);
        var tableTokens = ChunkingHelper.EstimateTokens(content, isTable: true);

        Assert.IsTrue(tableTokens > proseTokens);
    }

    [TestMethod]
    public void SafeKey_IsUrlSafeBase64_NoPlusOrSlash()
    {
        // Pick inputs whose base64 encoding is known to contain '+' and '/' before replacement.
        var key = ChunkingHelper.SafeKey("blob>>??", 999999);

        Assert.IsFalse(key.Contains('+'));
        Assert.IsFalse(key.Contains('/'));
    }

    [TestMethod]
    public void SafeKey_SameInputs_AreDeterministic()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 3);
        var key2 = ChunkingHelper.SafeKey("doc1", 3);

        Assert.AreEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentIndex_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc1", 1);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentBlobName_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc2", 0);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_Decodes_BackToBlobNameAndIndex()
    {
        var key = ChunkingHelper.SafeKey("some::blob/name", 42);

        var restored = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/')));

        Assert.AreEqual("some::blob/name::42", restored);
    }

    // ── CharBudgetForTokens ──────────────────────────────────────────────────

    [TestMethod]
    public void CharBudgetForTokens_Prose_IsTheTokenCeilingTimesTheProseRatio()
    {
        // 512 * 3.1 = 1,587.2, truncated to 1,587.
        Assert.AreEqual(1_587, ChunkingHelper.CharBudgetForTokens(512, isTable: false));
    }

    [TestMethod]
    public void CharBudgetForTokens_Table_IsSmallerThanTheProseBudgetForTheSameCeiling()
    {
        // Table markdown costs more tokens per character, so the same token ceiling has to buy
        // fewer characters. A budget that got this backwards would size table cuts as if they
        // were prose and overrun the ceiling by the full width of the ratio gap.
        Assert.IsTrue(
            ChunkingHelper.CharBudgetForTokens(512, isTable: true) <
            ChunkingHelper.CharBudgetForTokens(512, isTable: false));
    }

    [TestMethod]
    public void CharBudgetForTokens_RoundTripsUnderTheCeilingItWasSizedFor()
    {
        // The two directions have to agree: text filling the returned budget must not estimate
        // above the ceiling that produced it. HardCutter sizes its window this way and then
        // counts the pieces exactly, so a budget that overshot would produce cuts that fail
        // the later exact check.
        foreach (var isTable in new[] { false, true })
        {
            var budget = ChunkingHelper.CharBudgetForTokens(512, isTable);

            Assert.IsTrue(
                ChunkingHelper.EstimateTokens(new string('a', budget), isTable) <= 512,
                $"Budget of {budget} chars estimated above the 512-token ceiling (isTable: {isTable}).");
        }
    }

    [TestMethod]
    public void CharBudgetForTokens_ZeroCeiling_IsZero()
    {
        // HardCutter clamps this to a minimum of 1 itself; the helper is not the place that
        // invents a non-zero window.
        Assert.AreEqual(0, ChunkingHelper.CharBudgetForTokens(0, isTable: false));
    }

    // ── TitleLine ────────────────────────────────────────────────────────────

    [TestMethod]
    public void TitleLine_NoDomainTag_IsJustTheTitle()
    {
        Assert.AreEqual("Gedragscode medewerkers", ChunkingHelper.TitleLine("Gedragscode medewerkers", null));
        Assert.AreEqual("Gedragscode medewerkers", ChunkingHelper.TitleLine("Gedragscode medewerkers", ""));
    }

    [TestMethod]
    public void TitleLine_WithDomainTag_AppendsItInBrackets()
    {
        Assert.AreEqual("Zorgplan [vvt]", ChunkingHelper.TitleLine("Zorgplan", "vvt"));
    }

    [TestMethod]
    public void TitleLine_NullTitle_IsEmptyRatherThanTheWordNull()
    {
        // This line is prepended to the embedded text, so a "null" here would be embedded
        // verbatim on every chunk of an untitled document.
        Assert.AreEqual("", ChunkingHelper.TitleLine(null, null));
    }

    [TestMethod]
    public void TitleLine_NullTitleWithDomainTag_StillCarriesTheTag()
    {
        // The sector tag is what keeps a VVT answer out of a GHZ question; losing it because
        // the title happens to be missing is the failure worth pinning.
        Assert.AreEqual(" [ghz]", ChunkingHelper.TitleLine(null, "ghz"));
    }

    [TestMethod]
    public void TitleLine_IsTheSameLineTheBudgetAndTheEmbeddedTextBothUse()
    {
        // One rule, one call site each side - PrefixBuilder builds the real prefix from this
        // and the cascade charges it against the ceiling, so any divergence here silently
        // means the budgeted prefix is not the embedded one.
        var title  = "Arbeidsvoorwaarden";
        var tagged = ChunkingHelper.TitleLine(title, "vvt");

        Assert.IsTrue(tagged.StartsWith(title, StringComparison.Ordinal));
        Assert.IsTrue(tagged.Length > title.Length);
    }
}
